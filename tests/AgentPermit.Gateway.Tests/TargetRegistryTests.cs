using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentPermit.Gateway.Tests;

/// <summary>The Target System Registry: seeded, manageable as data, and admin-gated.</summary>
public sealed class TargetRegistryTests
{
    [Fact]
    public async Task Registry_IsSeeded_WithRefundSimulator()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/api/admin/targets")).Content.ReadAsStringAsync());
        var codes = doc.RootElement.EnumerateArray().Select(t => t.GetProperty("targetSystemCode").GetString());
        Assert.Contains("REFUND_SIMULATOR", codes);
    }

    [Fact]
    public async Task Target_CanBeAdded_AndRemoved_AsData()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        // Add a new REST target — no code change.
        var put = await client.PutAsJsonAsync("/api/admin/targets/SAP_PROCUREMENT", new
        {
            displayName = "SAP Procurement",
            adapterType = "REST",
            baseUrl = "https://sap.example.com",
            executePath = "/api/v1/actions",
            timeoutSeconds = 20,
            maxRetries = 3,
            allowedActions = new[] { "Vendor.Create", "Payment.Release" },
            enabled = true
        });
        put.EnsureSuccessStatusCode();

        using var list = JsonDocument.Parse(await (await client.GetAsync("/api/admin/targets")).Content.ReadAsStringAsync());
        Assert.Contains("SAP_PROCUREMENT", list.RootElement.EnumerateArray().Select(t => t.GetProperty("targetSystemCode").GetString()));

        var del = await client.DeleteAsync("/api/admin/targets/SAP_PROCUREMENT");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var gone = await client.GetAsync("/api/admin/targets/SAP_PROCUREMENT");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task AdminTargets_RequireAdminKey_WhenConfigured()
    {
        using var factory = new GatewayFactory { AdminApiKey = "s3cret-admin" };
        var client = factory.CreateClient();

        var noKey = await client.GetAsync("/api/admin/targets");
        Assert.Equal(HttpStatusCode.Forbidden, noKey.StatusCode);

        var msg = new HttpRequestMessage(HttpMethod.Get, "/api/admin/targets");
        msg.Headers.Add("X-Admin-Key", "s3cret-admin");
        var withKey = await client.SendAsync(msg);
        withKey.EnsureSuccessStatusCode();
    }
}
