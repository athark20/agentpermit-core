using System.Net.Http.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>The always-on safety net: the global kill switch and the velocity circuit breaker both hard-deny
/// before policy, independent of warm-up.</summary>
public sealed class SafetyControlsTests
{
    private static async Task<ActionResponseDto> Refund(HttpClient c, decimal amount) =>
        (await (await c.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(amount)))
            .Content.ReadFromJsonAsync<ActionResponseDto>())!;

    // Kill switch: freeze denies everything; unfreeze restores.
    [Fact]
    public async Task KillSwitch_FreezesAndUnfreezes()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        Assert.Equal("ALLOW", (await Refund(client, 100m)).Decision);

        (await client.PostAsJsonAsync("/api/admin/safety/freeze", new { reason = "incident-42" })).EnsureSuccessStatusCode();
        var frozen = await Refund(client, 100m);
        Assert.Equal("DENY", frozen.Decision);
        Assert.Equal(ReasonCode.SystemFrozen, frozen.ReasonCode);

        (await client.PostAsync("/api/admin/safety/unfreeze", null)).EnsureSuccessStatusCode();
        Assert.Equal("ALLOW", (await Refund(client, 100m)).Decision);
    }

    // Circuit breaker: once an agent exceeds the velocity limit, further actions trip the breaker.
    [Fact]
    public async Task CircuitBreaker_TripsOnRunaway()
    {
        using var factory = new GatewayFactory { CircuitBreakerMax = 2 };
        var client = factory.CreateClient();

        Assert.Equal("ALLOW", (await Refund(client, 100m)).Decision); // 1st (0 prior)
        Assert.Equal("ALLOW", (await Refund(client, 100m)).Decision); // 2nd (1 prior)
        var tripped = await Refund(client, 100m);                     // 3rd (2 prior >= limit)
        Assert.Equal("DENY", tripped.Decision);
        Assert.Equal(ReasonCode.CircuitBreaker, tripped.ReasonCode);
    }

    // Safety status endpoint reflects the config.
    [Fact]
    public async Task SafetyStatus_ReportsConfig()
    {
        using var factory = new GatewayFactory { CircuitBreakerMax = 42, DenyBreakerMax = 7 };
        var client = factory.CreateClient();
        var status = await (await client.GetAsync("/api/admin/safety")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(42, status.GetProperty("circuitBreaker").GetProperty("maxExecutionsPerWindow").GetInt32());
        Assert.Equal(7, status.GetProperty("denyBreaker").GetProperty("maxDenialsPerWindow").GetInt32());
        Assert.True(status.GetProperty("denyBreaker").GetProperty("autoQuarantine").GetBoolean());
        Assert.False(status.GetProperty("killSwitch").GetProperty("frozen").GetBoolean());
        Assert.Equal(0, status.GetProperty("quarantined").GetArrayLength());
    }

    private static async Task<ActionResponseDto> Post(HttpClient c, object body) =>
        (await (await c.PostAsJsonAsync("/api/actions/request", body)).Content.ReadFromJsonAsync<ActionResponseDto>())!;

    // Deny-velocity breaker: an agent refused too often (here: unknown actions it keeps trying) is quarantined;
    // its next, perfectly valid request is denied AGENT_QUARANTINED; an admin lift restores it.
    [Fact]
    public async Task DenyBreaker_QuarantinesProbingAgent_UntilLifted()
    {
        using var factory = new GatewayFactory { DenyBreakerMax = 3 };
        var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var probe = await Post(client, RequestBuilder.Refund(100m, actionId: $"Not.An.Action.{i}"));
            Assert.Equal("DENY", probe.Decision);
            Assert.Equal(ReasonCode.UnknownAction, probe.ReasonCode);
        }

        // 4th request is valid — but the agent has 3 denials in the window (limit 3) -> quarantined.
        var tripped = await Post(client, RequestBuilder.Refund(100m));
        Assert.Equal("DENY", tripped.Decision);
        Assert.Equal(ReasonCode.AgentQuarantined, tripped.ReasonCode);

        // Sticky: still quarantined on the next request, even though the deny count hasn't grown.
        var still = await Post(client, RequestBuilder.Refund(100m));
        Assert.Equal(ReasonCode.AgentQuarantined, still.ReasonCode);

        var status = await (await client.GetAsync("/api/admin/safety/quarantine")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var q = Assert.Single(status.EnumerateArray());
        Assert.Equal(RequestBuilder.RegisteredAgentId, q.GetProperty("agentId").GetString());
        Assert.Equal(3, q.GetProperty("denialCount").GetInt32());

        // Nothing was executed throughout.
        Assert.Equal(0, factory.Services.GetRequiredService<AgentPermit.Gateway.Simulator.IRefundLedger>().Count);

        // Admin lifts the quarantine -> the lift resets the deny clock, so the agent's next valid request
        // goes through even though the 3 old denials are still inside the 60s window.
        (await client.PostAsJsonAsync($"/api/admin/safety/quarantine/{RequestBuilder.RegisteredAgentId}/lift", new { reason = "reviewed" }))
            .EnsureSuccessStatusCode();
        var after = await Post(client, RequestBuilder.Refund(100m));
        Assert.Equal("ALLOW", after.Decision);
        Assert.Equal(1, factory.Services.GetRequiredService<AgentPermit.Gateway.Simulator.IRefundLedger>().Count);
        var none = await (await client.GetAsync("/api/admin/safety/quarantine")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, none.GetArrayLength());
    }

    // Manual quarantine + lift round-trip: a lifted agent with no denials in the window is allowed again.
    [Fact]
    public async Task Quarantine_ManualSetAndLift()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        Assert.Equal("ALLOW", (await Refund(client, 100m)).Decision);

        (await client.PostAsJsonAsync($"/api/admin/safety/quarantine/{RequestBuilder.RegisteredAgentId}", new { reason = "incident-7" }))
            .EnsureSuccessStatusCode();
        var denied = await Refund(client, 100m);
        Assert.Equal("DENY", denied.Decision);
        Assert.Equal(ReasonCode.AgentQuarantined, denied.ReasonCode);
        Assert.Contains("incident-7", denied.Reason);

        (await client.PostAsJsonAsync($"/api/admin/safety/quarantine/{RequestBuilder.RegisteredAgentId}/lift", new { reason = "resolved" }))
            .EnsureSuccessStatusCode();
        Assert.Equal("ALLOW", (await Refund(client, 100m)).Decision);

        // Lifting an agent that isn't quarantined is a 404, not a silent success.
        var again = await client.PostAsJsonAsync($"/api/admin/safety/quarantine/{RequestBuilder.RegisteredAgentId}/lift", new { });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, again.StatusCode);
    }

    // Unauthenticated probes that merely CLAIM an agent's name must not be able to quarantine that agent.
    [Fact]
    public async Task DenyBreaker_IgnoresUnresolvedIdentityProbes()
    {
        using var factory = new GatewayFactory { DenyBreakerMax = 2, EnforceIdentityBinding = true, ApimSharedSecret = "s3cret" };
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var probe = await Post(client, RequestBuilder.Refund(100m)); // no APIM origin -> NOT_FROM_APIM
            Assert.Equal("NOT_FROM_APIM", probe.ReasonCode);
        }

        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/actions/request")
        {
            Content = JsonContent.Create(RequestBuilder.Refund(100m))
        };
        msg.Headers.Add("X-Agent-Client-Id", GatewayFactory.BoundClientId);
        msg.Headers.Add("X-APIM-Origin", "s3cret");
        var real = await (await client.SendAsync(msg)).Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("ALLOW", real!.Decision);
    }

    // Safety-layer denials feed the breaker but are NOT rollout evidence: an agent cannot earn a promotion by
    // generating rejected traffic.
    [Fact]
    public async Task SafetyDenials_CountForBreaker_ButNotForRollout()
    {
        using var factory = new GatewayFactory { DenyBreakerMax = 100 };
        var client = factory.CreateClient();
        for (var i = 0; i < 4; i++)
            await Post(client, RequestBuilder.Refund(100m, actionId: "Nope.Nope"));

        var store = factory.Services.GetRequiredService<AgentPermit.Gateway.Observations.IObservationStore>();
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        Assert.Equal(4, await store.CountDenialsAsync(RequestBuilder.RegisteredAgentId, since, CancellationToken.None));
        var stats = await store.GetAgentStatsAsync(RequestBuilder.RegisteredAgentId, since, CancellationToken.None);
        Assert.Equal(0, stats.Total);
    }

    // Policy denials count too (they are ordinary observations with an effective DENY).
    [Fact]
    public async Task PolicyDenials_CountTowardDenyVelocity()
    {
        using var factory = new GatewayFactory { DenyBreakerMax = 2 };
        var client = factory.CreateClient();

        Assert.Equal(ReasonCode.PolicyDeny, (await Refund(client, 9_000m)).ReasonCode); // over the deny threshold
        Assert.Equal(ReasonCode.PolicyDeny, (await Refund(client, 9_000m)).ReasonCode);
        var tripped = await Refund(client, 100m);                                        // would be ALLOW
        Assert.Equal(ReasonCode.AgentQuarantined, tripped.ReasonCode);
    }

    // With auto-quarantine off, the trip denies the current request but isn't sticky.
    [Fact]
    public async Task DenyBreaker_WithoutAutoQuarantine_IsNotSticky()
    {
        using var factory = new GatewayFactory { DenyBreakerMax = 1, DenyBreakerAutoQuarantine = false };
        var client = factory.CreateClient();

        await Post(client, RequestBuilder.Refund(100m, actionId: "Nope.Nope"));       // 1 denial
        var tripped = await Refund(client, 100m);
        Assert.Equal(ReasonCode.CircuitBreaker, tripped.ReasonCode);

        var status = await (await client.GetAsync("/api/admin/safety/quarantine")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, status.GetArrayLength());
    }
}
