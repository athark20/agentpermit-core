using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;

namespace AgentPermit.Gateway.Tests;

/// <summary>Evidence-based auto-promotion: an agent with enough clean warm-up traffic is graduated; one with
/// a would-have-been-block is held.</summary>
public sealed class AutoPromotionTests
{
    private static async Task Register(HttpClient c, string id, string posture) =>
        (await c.PutAsJsonAsync($"/api/admin/agents/{id}", new { displayName = id, enabled = true, enforcementPosture = posture }))
            .EnsureSuccessStatusCode();

    private static async Task<string> Posture(HttpClient c, string id)
    {
        var s = JsonSerializer.Deserialize<JsonElement>(await (await c.GetAsync($"/api/admin/rollout/{id}")).Content.ReadAsStringAsync());
        return s.GetProperty("posture").GetString()!;
    }

    [Fact]
    public async Task AutoPromote_GraduatesAgentWithCleanTraffic()
    {
        using var factory = new GatewayFactory { MinObservationsToPromote = 1 };
        var client = factory.CreateClient();
        await Register(client, "auto-agent", EnforcementPosture.Observe);

        // One clean allowed refund → 1 observation, 0 would-have-been-blocks → recommendation is WARN.
        var resp = await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(100m, agentId: "auto-agent"));
        Assert.Equal("ALLOW", (await resp.Content.ReadFromJsonAsync<ActionResponseDto>())!.Decision);

        var promoted = JsonSerializer.Deserialize<JsonElement>(
            await (await client.PostAsync("/api/admin/rollout/auto-promote", null)).Content.ReadAsStringAsync());
        var list = promoted.GetProperty("promoted").EnumerateArray().ToList();
        Assert.Contains(list, p => p.GetProperty("agentId").GetString() == "auto-agent"
                                   && p.GetProperty("to").GetString() == EnforcementPosture.Warn);
        Assert.Equal(EnforcementPosture.Warn, await Posture(client, "auto-agent"));
    }

    [Fact]
    public async Task AutoPromote_SkipsAgentWithNoTraffic()
    {
        using var factory = new GatewayFactory(); // default threshold (20) — a fresh agent has 0 obs
        var client = factory.CreateClient();
        await Register(client, "quiet-agent", EnforcementPosture.Observe);

        var promoted = JsonSerializer.Deserialize<JsonElement>(
            await (await client.PostAsync("/api/admin/rollout/auto-promote", null)).Content.ReadAsStringAsync());
        Assert.DoesNotContain(promoted.GetProperty("promoted").EnumerateArray(),
            p => p.GetProperty("agentId").GetString() == "quiet-agent");
        Assert.Equal(EnforcementPosture.Observe, await Posture(client, "quiet-agent")); // unchanged
    }
}
