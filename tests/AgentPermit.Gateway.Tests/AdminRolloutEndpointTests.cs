using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Domain;

namespace AgentPermit.Gateway.Tests;

/// <summary>End-to-end: promoting an agent along the rollout ladder through the admin endpoints.</summary>
public sealed class AdminRolloutEndpointTests
{
    private const string Agent = "rollout-agent";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static string Posture(JsonElement e) => e.GetProperty("posture").GetString()!;

    [Fact]
    public async Task Promote_Then_Demote_WalksTheLadder()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        // Register a freshly-onboarded agent in OBSERVE warm-up.
        (await client.PutAsJsonAsync($"/api/admin/agents/{Agent}", new
        {
            displayName = "Rollout Agent", enabled = true, enforcementPosture = EnforcementPosture.Observe
        })).EnsureSuccessStatusCode();

        var start = await ReadAsync(await client.GetAsync($"/api/admin/rollout/{Agent}"));
        Assert.Equal(EnforcementPosture.Observe, Posture(start));
        Assert.True(start.GetProperty("warmingUp").GetBoolean());

        // Promote OBSERVE -> WARN -> ENFORCE.
        Assert.Equal(EnforcementPosture.Warn, Posture(await ReadAsync(await client.PostAsync($"/api/admin/rollout/{Agent}/promote", null))));
        var enforced = await ReadAsync(await client.PostAsync($"/api/admin/rollout/{Agent}/promote", null));
        Assert.Equal(EnforcementPosture.Enforce, Posture(enforced));
        Assert.True(enforced.GetProperty("fullyEnforced").GetBoolean());

        // Promoting again saturates (no change).
        var again = await ReadAsync(await client.PostAsync($"/api/admin/rollout/{Agent}/promote", null));
        Assert.True(again.GetProperty("unchanged").GetBoolean());

        // Rollback ENFORCE -> WARN.
        Assert.Equal(EnforcementPosture.Warn, Posture(await ReadAsync(await client.PostAsync($"/api/admin/rollout/{Agent}/demote", null))));

        // Fleet overview includes the agent and counts warm-ups.
        var overview = await ReadAsync(await client.GetAsync("/api/admin/rollout"));
        Assert.True(overview.GetProperty("total").GetInt32() >= 1);
        Assert.Contains(overview.GetProperty("agents").EnumerateArray(),
            a => a.GetProperty("agentId").GetString() == Agent);
    }
}
