using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Windowed aggregate conditions — the answer to salami-slicing. A per-request threshold of 1,000 says
/// nothing about a hundred 999s; these rules look at the sum / count / distinct counterparties over a window,
/// including the request being decided.
/// </summary>
public sealed class AggregateLimitTests
{
    private static async Task<ActionResponseDto> Refund(
        HttpClient c, decimal amount, string customerId = "CUST-1001", string? user = null, string agentId = RequestBuilder.RegisteredAgentId)
    {
        var body = new Dictionary<string, object?>
        {
            ["actionId"] = RequestBuilder.RefundActionId,
            ["agentId"] = agentId,
            ["correlationId"] = Guid.NewGuid().ToString(),
            ["idempotencyKey"] = $"idem-{Guid.NewGuid():N}",
            ["parameters"] = new { customerId, amount, currency = "AED", reason = "Service failure" }
        };
        if (user is not null) body["initiatingUserId"] = user;
        var resp = await c.PostAsJsonAsync("/api/actions/request", body);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ActionResponseDto>())!;
    }

    /// <summary>Publish a snapshot: small refunds allowed, but blocked once the aggregate trips.</summary>
    private static async Task PublishAsync(HttpClient c, string version, object aggregateRule)
    {
        var snapshot = new
        {
            policyCode = "Refund.Aggregate",
            version,
            defaultDecision = "DENY",
            rules = new[]
            {
                aggregateRule,
                new
                {
                    id = "allow-small", name = "auto-approve small", priority = 100,
                    actionId = "Refund.Issue", decision = "ALLOW", approverRoles = Array.Empty<string>(),
                    conditions = new object[] { new { field = "amount", @operator = "lessThanOrEqual", value = 1000 } }
                }
            }
        };
        var pub = await c.PostAsJsonAsync("/api/policies/publish", snapshot);
        pub.EnsureSuccessStatusCode();
    }

    private static object Rule(string id, string name, string decision, object aggregateCondition, int priority = 10) => new
    {
        id, name, priority, actionId = "Refund.Issue", decision,
        approverRoles = decision == "REQUIRE_APPROVAL" ? new[] { "RefundApprover" } : Array.Empty<string>(),
        conditions = new[] { aggregateCondition }
    };

    // Daily spend cap of 1,000: two 400s pass; the third would take the window to 1,200, so it is denied.
    [Fact]
    public async Task SumOverWindow_BlocksTheRequestThatBreachesTheCap()
    {
        using var factory = new GatewayFactory { CircuitBreakerMax = 100, DenyBreakerMax = 100 };
        var client = factory.CreateClient();
        await PublishAsync(client, "agg-sum-1", Rule("daily-cap", "Daily refund cap per agent", "DENY", new
        {
            field = "amount",
            @operator = "greaterThan",
            value = 1000,
            aggregate = new { function = "sum", window = "PT24H", groupBy = new[] { "agentId" } }
        }));

        Assert.Equal("ALLOW", (await Refund(client, 400m)).Decision);  // running total 400
        Assert.Equal("ALLOW", (await Refund(client, 400m)).Decision);  // 800
        var breach = await Refund(client, 400m);                       // 1200 > 1000 -> denied
        Assert.Equal("DENY", breach.Decision);
        Assert.Equal("POLICY_DENY", breach.ReasonCode);

        // The denied one did not count toward the committed total, so a smaller top-up still fits.
        Assert.Equal("ALLOW", (await Refund(client, 200m)).Decision);  // 1000, not > 1000
        Assert.Equal(3, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // The decision trace records the aggregate value the verdict rested on (so an auditor sees WHY).
    [Fact]
    public async Task AggregateValue_IsRecordedInTheDecisionTrace()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await PublishAsync(client, "agg-trace-1", Rule("daily-cap", "Daily cap", "DENY", new
        {
            field = "amount",
            @operator = "greaterThan",
            value = 500,
            aggregate = new { function = "sum", window = "PT24H", groupBy = new[] { "agentId" } }
        }));

        await Refund(client, 400m);
        var denied = await Refund(client, 400m);
        Assert.Equal("DENY", denied.Decision);

        var store = factory.Services.GetRequiredService<AgentPermit.Gateway.Persistence.IAgentPermitStore>();
        var decision = await store.GetLatestPolicyDecisionAsync(denied.ActionRequestId, CancellationToken.None);
        using var trace = JsonDocument.Parse(decision!.EvaluationTraceJson!);
        var aggregates = trace.RootElement.GetProperty("policyTrace").GetProperty("aggregates");
        var entry = Assert.Single(aggregates.EnumerateObject());
        Assert.StartsWith("agg:sum:amount:PT24H:agentId:", entry.Name, StringComparison.Ordinal);
        Assert.Equal(800m, entry.Value.GetDecimal()); // 400 prior + 400 current
    }

    // Grouping by a request parameter: the cap is per customer, so a different customer is unaffected.
    [Fact]
    public async Task GroupByParameter_ScopesTheWindowPerCounterparty()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await PublishAsync(client, "agg-cust-1", Rule("per-customer", "Per-customer cap", "DENY", new
        {
            field = "amount",
            @operator = "greaterThan",
            value = 600,
            aggregate = new { function = "sum", window = "PT24H", groupBy = new[] { "agentId", "customerId" } }
        }));

        Assert.Equal("ALLOW", (await Refund(client, 400m, customerId: "CUST-A")).Decision);
        Assert.Equal("DENY", (await Refund(client, 400m, customerId: "CUST-A")).Decision);   // 800 > 600
        Assert.Equal("ALLOW", (await Refund(client, 400m, customerId: "CUST-B")).Decision);  // other customer: 400
    }

    // count: step up to a human after N refunds in the window, whatever their size.
    [Fact]
    public async Task CountOverWindow_StepsUpToApproval()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await PublishAsync(client, "agg-count-1", Rule("burst", "Too many refunds in an hour", "REQUIRE_APPROVAL", new
        {
            field = "amount",
            @operator = "greaterThan",
            value = 3,
            aggregate = new { function = "count", window = "PT1H", groupBy = new[] { "agentId" } }
        }));

        for (var i = 0; i < 3; i++)
            Assert.Equal("ALLOW", (await Refund(client, 100m)).Decision);   // counts 1,2,3
        var fourth = await Refund(client, 100m);                            // count 4 > 3
        Assert.Equal("REQUIRE_APPROVAL", fourth.Decision);
        Assert.NotNull(fourth.ApprovalRequestId);
    }

    // distinct: fan-out across counterparties is the fraud signature, not the amount.
    [Fact]
    public async Task DistinctOverWindow_CatchesFanOut()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await PublishAsync(client, "agg-distinct-1", Rule("fan-out", "Too many distinct customers", "DENY", new
        {
            field = "customerId",
            @operator = "greaterThan",
            value = 2,
            aggregate = new { function = "distinct", window = "PT1H", groupBy = new[] { "agentId" } }
        }));

        Assert.Equal("ALLOW", (await Refund(client, 50m, customerId: "C1")).Decision);
        Assert.Equal("ALLOW", (await Refund(client, 50m, customerId: "C2")).Decision);
        Assert.Equal("ALLOW", (await Refund(client, 50m, customerId: "C1")).Decision);  // still 2 distinct
        Assert.Equal("DENY", (await Refund(client, 50m, customerId: "C3")).Decision);   // 3 distinct > 2
    }

    // Per-human caps: the window is grouped by initiatingUserId, so one user's spend doesn't block another's.
    [Fact]
    public async Task GroupByInitiatingUser_ScopesPerHuman()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await PublishAsync(client, "agg-user-1", Rule("per-user", "Per-user cap", "DENY", new
        {
            field = "amount",
            @operator = "greaterThan",
            value = 600,
            aggregate = new { function = "sum", window = "PT24H", groupBy = new[] { "initiatingUserId" } }
        }));

        Assert.Equal("ALLOW", (await Refund(client, 400m, user: "ann@contoso.com")).Decision);
        Assert.Equal("DENY", (await Refund(client, 400m, user: "ann@contoso.com")).Decision);
        Assert.Equal("ALLOW", (await Refund(client, 400m, user: "bob@contoso.com")).Decision);
    }

    // A snapshot with no aggregate conditions must not change behaviour (and makes no aggregate query).
    [Fact]
    public async Task SnapshotWithoutAggregates_BehavesExactlyAsBefore()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal("ALLOW", (await Refund(client, 100m)).Decision);
        Assert.Equal("REQUIRE_APPROVAL", (await Refund(client, 750m)).Decision);
        Assert.Equal("DENY", (await Refund(client, 9000m)).Decision);
    }

    // Publish-time validation: a malformed aggregate is rejected before it can break production requests.
    [Theory]
    [InlineData("median", "PT24H", "amount", "in")]      // unknown function
    [InlineData("sum", "banana", "amount", "greaterThan")] // not an ISO-8601 duration
    [InlineData("sum", "PT24H", "", "greaterThan")]        // sum needs a field
    public async Task MalformedAggregate_IsRejectedAtPublish(string function, string window, string field, string op)
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var snapshot = new
        {
            policyCode = "Refund.Bad",
            version = "bad-1",
            defaultDecision = "DENY",
            rules = new[]
            {
                new
                {
                    id = "bad", name = "bad", priority = 10, actionId = "Refund.Issue", decision = "DENY",
                    approverRoles = Array.Empty<string>(),
                    conditions = new[]
                    {
                        new { field, @operator = op, value = (object)1000, aggregate = new { function, window, groupBy = new[] { "agentId" } } }
                    }
                }
            }
        };

        var pub = await client.PostAsJsonAsync("/api/policies/publish", snapshot);
        Assert.Equal(HttpStatusCode.BadRequest, pub.StatusCode);
    }

    // An aggregate paired with a set/existence operator is meaningless (aggregates yield a number).
    [Fact]
    public async Task AggregateWithSetOperator_IsRejectedAtPublish()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var snapshot = new
        {
            policyCode = "Refund.Bad",
            version = "bad-2",
            defaultDecision = "DENY",
            rules = new[]
            {
                new
                {
                    id = "bad", name = "bad", priority = 10, actionId = "Refund.Issue", decision = "DENY",
                    approverRoles = Array.Empty<string>(),
                    conditions = new[]
                    {
                        new { field = "amount", @operator = "exists", value = (object)true,
                              aggregate = new { function = "sum", window = "PT24H", groupBy = new[] { "agentId" } } }
                    }
                }
            }
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/policies/publish", snapshot)).StatusCode);
    }
}
