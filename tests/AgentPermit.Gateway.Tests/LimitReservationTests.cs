using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Policies;
using AgentPermit.Gateway.Registry;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Race-proof limits. A windowed limit ("no more than 1,000 an hour") used to be checked by reading the prior
/// total and then recording the request, so requests arriving together could all read the same total and all
/// pass. Now a request holds the limit's lock from the read until it is recorded: the next one waits and counts
/// it. The store used here answers aggregate queries slowly on purpose, which makes the race certain without the
/// lock.
/// </summary>
public sealed class LimitReservationTests
{
    // ================================================================ end to end

    [Fact]
    public async Task ConcurrentRequests_CannotAllSlipUnderTheCap()
    {
        using var baseFactory = new GatewayFactory { DenyBreakerMax = 1000 };
        using var factory = WithSlowAggregateQueries(baseFactory);
        var client = factory.CreateClient();
        await PublishCapAsync(client, "20.0.0", groupBy: new[] { "agentId" });

        // Twelve requests of 400 against a cap of 1,000 an hour, all at once: only two fit (400 + 400 = 800).
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => RefundAsync(client, 400m)));

        Assert.Equal(2, results.Count(r => r.Decision == "REQUIRE_APPROVAL"));
        Assert.Equal(10, results.Count(r => r.Decision == "DENY"));
    }

    // Requests in different groups don't share a total, so each group gets its own two.
    [Fact]
    public async Task ConcurrentRequests_EachGroupGetsItsOwnCap()
    {
        using var baseFactory = new GatewayFactory { DenyBreakerMax = 1000 };
        using var factory = WithSlowAggregateQueries(baseFactory);
        var client = factory.CreateClient();
        await PublishCapAsync(client, "20.1.0", groupBy: new[] { "agentId", "customerId" });

        var results = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => RefundAsync(client, 400m, "CUST-A"))
                .Concat(Enumerable.Range(0, 5).Select(_ => RefundAsync(client, 400m, "cust-b"))));

        Assert.Equal(4, results.Count(r => r.Decision == "REQUIRE_APPROVAL"));
    }

    // A rule for "any action" (the console sends an empty action id) must total the current action.
    [Fact]
    public async Task AnyActionRule_TotalsTheCurrentAction()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await PublishCapAsync(client, "20.2.0", groupBy: new[] { "agentId" }, capActionId: "");

        Assert.Equal("REQUIRE_APPROVAL", (await RefundAsync(client, 600m)).Decision);
        Assert.Equal("DENY", (await RefundAsync(client, 600m)).Decision);   // 1,200 in the hour
    }

    // ================================================================ which locks a request takes

    [Fact]
    public void ReservationKeys_NoLimits_NoLocks()
    {
        var provider = Provider(Snapshot(new PolicyRule { Id = "plain", ActionId = "Refund.Issue", Decision = "ALLOW" }));
        Assert.Empty(provider.ReservationKeys(Refund, Params("CUST-1"), "agent-x", null, null));
    }

    [Fact]
    public void ReservationKeys_ScopedToTheRequestsOwnGroup()
    {
        var provider = Provider(Snapshot(CapRule("Refund.Issue", groupBy: new[] { "agentId", "customerId" })));

        var x = provider.ReservationKeys(Refund, Params("CUST-1"), "agent-x", null, null);
        var y = provider.ReservationKeys(Refund, Params("CUST-1"), "agent-y", null, null);
        var xUpper = provider.ReservationKeys(Refund, Params("cust-1"), "AGENT-X", null, null);

        Assert.Single(x);
        Assert.NotEqual(x, y);
        Assert.Equal(x, xUpper);   // totals match group values case-insensitively, so locks do too
    }

    // A rule on another action whose total includes this action must be locked by this action's requests too.
    [Fact]
    public void ReservationKeys_IncludeLimitsThisRequestCountsTowards()
    {
        var provider = Provider(Snapshot(
            CapRule("Payment.Issue", groupBy: new[] { "agentId" }, actions: new[] { "Payment.Issue", "Refund.Issue" }),
            CapRule("Vendor.Pay", groupBy: new[] { "agentId" })));   // only totals Vendor.Pay: not ours

        var keys = provider.ReservationKeys(Refund, Params("CUST-1"), "agent-x", null, null);

        var key = Assert.Single(keys);
        Assert.Contains("Payment.Issue,Refund.Issue", key);
    }

    // A group value the request doesn't carry can't be shared with anyone, so there is nothing to lock.
    [Fact]
    public void ReservationKeys_MissingGroupValue_NoLock()
    {
        var provider = Provider(Snapshot(CapRule("Refund.Issue", groupBy: new[] { "initiatingUserId" })));
        Assert.Empty(provider.ReservationKeys(Refund, Params("CUST-1"), "agent-x", null, initiatingUserId: null));
        Assert.Single(provider.ReservationKeys(Refund, Params("CUST-1"), "agent-x", null, initiatingUserId: "fatima@bank.ae"));
    }

    // ================================================================ the in-memory lock itself

    [Fact]
    public async Task Lock_SameKeyWaits_OtherKeysDoNot_AndNothingLeaks()
    {
        var locks = new InMemoryAggregateLock(new AggregateLockOptions { TimeoutSeconds = 5 });
        var first = await locks.AcquireAsync(new[] { "limit-a" }, CancellationToken.None);

        var second = locks.AcquireAsync(new[] { "limit-a" }, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);   // waits for the first

        var other = await locks.AcquireAsync(new[] { "limit-b" }, CancellationToken.None);   // doesn't wait

        await first.DisposeAsync();
        await first.DisposeAsync();         // releasing twice is harmless
        var secondHandle = await second.WaitAsync(TimeSpan.FromSeconds(5));
        await secondHandle.DisposeAsync();
        await other.DisposeAsync();

        Assert.Equal(0, locks.TrackedKeyCount);
    }

    [Fact]
    public async Task Lock_TimesOut_WithARetryableError()
    {
        var locks = new InMemoryAggregateLock(new AggregateLockOptions { TimeoutSeconds = 1 });
        await using var held = await locks.AcquireAsync(new[] { "limit-a" }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<LimitBusyException>(() => locks.AcquireAsync(new[] { "limit-a" }, CancellationToken.None));
        Assert.Equal(503, ex.StatusCode);
    }

    // Keys are taken in a fixed order, so two requests needing the same two limits can't deadlock.
    [Fact]
    public async Task Lock_OppositeOrders_DoNotDeadlock()
    {
        var locks = new InMemoryAggregateLock(new AggregateLockOptions { TimeoutSeconds = 5 });
        async Task Take(string[] keys)
        {
            for (var i = 0; i < 50; i++)
            {
                await using var h = await locks.AcquireAsync(keys, CancellationToken.None);
                await Task.Yield();
            }
        }

        await Task.WhenAll(Take(new[] { "limit-a", "limit-b" }), Take(new[] { "limit-b", "limit-a" }))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, locks.TrackedKeyCount);
    }

    // ================================================================ helpers

    private static readonly ActionDefinition Refund = new() { ActionId = "Refund.Issue" };

    /// <summary>Over 1,000 in an hour is stopped; anything under it waits for a person (so nothing executes).</summary>
    private static async Task PublishCapAsync(HttpClient client, string version, string[] groupBy, string capActionId = "Refund.Issue")
    {
        var snapshot = new
        {
            policyCode = "Refund.HourlyCap",
            version,
            defaultDecision = "DENY",
            rules = new object[]
            {
                new
                {
                    id = "hourly-cap", name = "No more than 1,000 an hour", priority = 10, actionId = capActionId,
                    decision = "DENY", approverRoles = Array.Empty<string>(),
                    conditions = new object[]
                    {
                        new { field = "amount", @operator = "greaterThan", value = 1000,
                              aggregate = new { function = "sum", window = "PT1H", groupBy } }
                    }
                },
                new
                {
                    id = "ask", name = "Everything else needs a person", priority = 20, actionId = "Refund.Issue",
                    decision = "REQUIRE_APPROVAL", approverRoles = new[] { "RefundApprover" }, conditions = Array.Empty<object>()
                }
            }
        };
        (await client.PostAsJsonAsync("/api/policies/publish", snapshot)).EnsureSuccessStatusCode();
    }

    private static async Task<ActionResponseDto> RefundAsync(HttpClient client, decimal amount, string customerId = "CUST-1001")
    {
        var resp = await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(amount, customerId: customerId));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ActionResponseDto>())!;
    }

    /// <summary>Same gateway, but every aggregate query pauses after reading, widening the read-then-record gap.</summary>
    private static WebApplicationFactory<Program> WithSlowAggregateQueries(GatewayFactory factory)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentPermitStore>();
            var proxy = DispatchProxy.Create<IAgentPermitStore, SlowAggregateQueries>();
            ((SlowAggregateQueries)(object)proxy).Inner = new InMemoryAgentPermitStore();
            services.AddSingleton(proxy);
        }));

    /// <summary>Forwards every store call; aggregate reads return only after a pause.</summary>
    public class SlowAggregateQueries : DispatchProxy
    {
        public IAgentPermitStore Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(Inner, args);
            return targetMethod.Name == nameof(IAgentPermitStore.ListParameterValuesAsync)
                ? AfterPause((Task<IReadOnlyList<string?>>)result!)
                : result;
        }

        private static async Task<IReadOnlyList<string?>> AfterPause(Task<IReadOnlyList<string?>> read)
        {
            var values = await read;
            await Task.Delay(40);
            return values;
        }
    }

    private static PolicyAggregateProvider Provider(PolicySnapshot snapshot)
        => new(new InMemoryAgentPermitStore(), new FixedSnapshot(snapshot));

    private static PolicySnapshot Snapshot(params PolicyRule[] rules)
        => new() { PolicyCode = "Test", Version = "1.0.0", Rules = rules.ToList() };

    private static PolicyRule CapRule(string actionId, string[] groupBy, string[]? actions = null) => new()
    {
        Id = $"cap-{actionId}",
        Name = "Cap",
        ActionId = actionId,
        Decision = "DENY",
        Conditions =
        {
            new PolicyCondition
            {
                Field = "amount",
                Operator = "greaterThan",
                Value = JsonSerializer.SerializeToElement(1000),
                Aggregate = new PolicyAggregate { Function = "sum", Window = "PT1H", GroupBy = groupBy, Actions = actions }
            }
        }
    };

    private static JsonElement Params(string customerId)
        => JsonSerializer.SerializeToElement(new { customerId, amount = 400, currency = "AED" });

    private sealed class FixedSnapshot(PolicySnapshot snapshot) : IPolicySnapshotProvider
    {
        public PolicySnapshot Current => snapshot;
        public string ActiveHash => "test";
    }
}
