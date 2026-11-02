using System.Net.Http.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// End-to-end proof of the secure-by-default warm-up guarantee: even with an agent explicitly in OBSERVE
/// warm-up, a HIGH-consequence action (the Financial refund) is still enforced by policy. Warm-up never
/// opens the vault — the exact scenario a customer worries about during month-one onboarding.
/// </summary>
public sealed class EnforcementPostureTests
{
    private const string ObserverAgent = "warmup-observer";

    private static async Task RegisterObserverAsync(HttpClient client)
    {
        // Admin CRUD is open in tests (no admin key/Entra configured). Register an agent in OBSERVE warm-up.
        var resp = await client.PutAsJsonAsync($"/api/admin/agents/{ObserverAgent}", new
        {
            displayName = "Warm-up Observer",
            enabled = true,
            enforcementPosture = EnforcementPosture.Observe
        });
        resp.EnsureSuccessStatusCode();
    }

    // The money test: OBSERVE warm-up + a large (HIGH-consequence) refund is STILL denied.
    [Fact]
    public async Task ObserveWarmup_HighConsequenceDeny_IsStillEnforced()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await RegisterObserverAsync(client);

        var resp = await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(3000m, agentId: ObserverAgent));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("DENY", body!.Decision);                       // NOT downgraded
        Assert.Equal(ReasonCode.PolicyDeny, body.ReasonCode);
        Assert.Equal(EnforcementMode.Enforced, body.EnforcementMode);
        Assert.Null(body.ComputedDecision);                         // no warm-up divergence
        Assert.Equal("DECISION", body.Receipt?.Kind);               // signed decision receipt, nothing executed
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // A permitted (small) refund still flows normally for the same warm-up agent.
    [Fact]
    public async Task ObserveWarmup_AllowedRefund_ExecutesNormally()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await RegisterObserverAsync(client);

        var resp = await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(100m, agentId: ObserverAgent));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("ALLOW", body!.Decision);
        Assert.Equal(EnforcementMode.Enforced, body.EnforcementMode);
        Assert.NotNull(body.Receipt);
        Assert.Equal(1, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }
}
