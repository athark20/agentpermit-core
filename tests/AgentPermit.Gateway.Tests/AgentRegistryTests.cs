using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// The Agent Registry is data now (store-backed, cached read-through). Config seeds a fresh store, and
/// admin CRUD adds/updates/removes agents live — the same shape as the Target System Registry.
/// </summary>
public sealed class AgentRegistryTests
{
    private static HttpRequestMessage Put(string agentId, object body, string? key)
    {
        var msg = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/agents/{agentId}") { Content = JsonContent.Create(body) };
        if (key is not null) msg.Headers.Add("X-Admin-Key", key);
        return msg;
    }

    [Fact]
    public async Task ConfiguredAgent_SeedsTheStore()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/admin/agents")).Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(a => a.GetProperty("agentId").GetString());
        Assert.Contains("copilot-refund-demo", ids);
    }

    [Fact]
    public async Task Admin_CanRegisterNewAgent_AndItResolves()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var put = await client.SendAsync(Put("billing-agent",
            new { displayName = "Billing Agent", entraClientId = "11111111-1111-1111-1111-111111111111", enabled = true }, null));
        put.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await (await client.GetAsync("/api/admin/agents")).Content.ReadAsStringAsync());
        var agent = doc.RootElement.EnumerateArray().Single(a => a.GetProperty("agentId").GetString() == "billing-agent");
        Assert.Equal("Billing Agent", agent.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Admin_Agents_RequireKey_WhenConfigured()
    {
        using var factory = new GatewayFactory { AdminApiKey = "s3cret-admin" };
        var client = factory.CreateClient();

        var noKey = await client.SendAsync(Put("x", new { displayName = "X" }, null));
        Assert.Equal(HttpStatusCode.Forbidden, noKey.StatusCode);

        var withKey = await client.SendAsync(Put("x", new { displayName = "X" }, "s3cret-admin"));
        withKey.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Admin_CanDeleteAgent()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        (await client.SendAsync(Put("temp-agent", new { displayName = "Temp" }, null))).EnsureSuccessStatusCode();
        var del = await client.DeleteAsync("/api/admin/agents/temp-agent");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/admin/agents/temp-agent")).StatusCode);
    }
}
