using System.Net.Http.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;

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
        using var factory = new GatewayFactory { CircuitBreakerMax = 42 };
        var client = factory.CreateClient();
        var status = await (await client.GetAsync("/api/admin/safety")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(42, status.GetProperty("circuitBreaker").GetProperty("maxExecutionsPerWindow").GetInt32());
        Assert.False(status.GetProperty("killSwitch").GetProperty("frozen").GetBoolean());
    }
}
