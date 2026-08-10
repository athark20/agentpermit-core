using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// End-to-end: a governed request writes an observation, which then drives the agent's rollout
/// recommendation and is retrievable as evidence. With the promote threshold lowered to 1, a single clean
/// (non-blocked) observation makes the OBSERVE agent recommend promotion to WARN.
/// </summary>
public sealed class ObservationEvidenceTests
{
    private const string Agent = "warmup-evidence";

    [Fact]
    public async Task GovernedRequest_RecordsObservation_AndDrivesRecommendation()
    {
        using var factory = new GatewayFactory { MinObservationsToPromote = 1 };
        var client = factory.CreateClient();

        (await client.PutAsJsonAsync($"/api/admin/agents/{Agent}", new
        {
            displayName = "Warm-up Evidence", enabled = true, enforcementPosture = EnforcementPosture.Observe
        })).EnsureSuccessStatusCode();

        // A clean, allowed refund (HIGH action, small amount) — executes and records one observation.
        var reqResp = await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(100m, agentId: Agent));
        var body = await reqResp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("ALLOW", body!.Decision);

        // Rollout status now carries the evidence and recommends promotion (1 clean obs, threshold 1).
        var rollout = JsonSerializer.Deserialize<JsonElement>(
            await (await client.GetAsync($"/api/admin/rollout/{Agent}")).Content.ReadAsStringAsync());
        Assert.Equal(EnforcementPosture.Observe, rollout.GetProperty("posture").GetString());
        Assert.True(rollout.GetProperty("observationsInWindow").GetInt32() >= 1);
        Assert.Equal(0, rollout.GetProperty("wouldHaveBlockedInWindow").GetInt32());
        Assert.Equal(EnforcementPosture.Warn, rollout.GetProperty("recommendation").GetString());

        // The raw observation is retrievable as evidence.
        var obs = JsonSerializer.Deserialize<JsonElement>(
            await (await client.GetAsync($"/api/admin/observations/{Agent}")).Content.ReadAsStringAsync());
        Assert.True(obs.GetProperty("stats").GetProperty("total").GetInt32() >= 1);
        Assert.NotEmpty(obs.GetProperty("recent").EnumerateArray());

        // The feeds view shows the always-on gateway source (with ingested count) and the disabled externals.
        var feeds = JsonSerializer.Deserialize<JsonElement>(
            await (await client.GetAsync("/api/admin/observations/feeds")).Content.ReadAsStringAsync());
        var byName = feeds.EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
        Assert.True(byName["gateway"].GetProperty("enabled").GetBoolean());
        Assert.True(byName["gateway"].GetProperty("totalIngested").GetInt64() >= 1);
        Assert.False(byName["purview-audit"].GetProperty("enabled").GetBoolean());
        Assert.False(byName["flow-run"].GetProperty("enabled").GetBoolean());
    }
}
