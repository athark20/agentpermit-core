using System.Net.Http.Json;
using AgentPermit.Gateway.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>Simulation must predict the decision from the active snapshot without executing.</summary>
public sealed class PolicySimulationTests
{
    private static object Body(decimal amount) => new
    {
        actionId = "Refund.Issue",
        agentId = "copilot-refund-demo",
        parameters = new { customerId = "CUST-1", amount, currency = "AED", reason = "sim" }
    };

    [Theory]
    [InlineData(100, "ALLOW")]
    [InlineData(750, "REQUIRE_APPROVAL")]
    [InlineData(3000, "DENY")]
    public async Task Simulate_PredictsDecision_WithoutExecuting(decimal amount, string expected)
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/policies/simulate", Body(amount));
        resp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(expected, root.GetProperty("decision").GetString());
        Assert.Equal("2.0.0", root.GetProperty("policyVersion").GetString());
        // Nothing executed.
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    [Fact]
    public async Task ActiveSnapshot_IsExposed()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        using var doc = System.Text.Json.JsonDocument.Parse(
            await (await client.GetAsync("/api/policies/active")).Content.ReadAsStringAsync());
        Assert.Equal("2.0.0", doc.RootElement.GetProperty("version").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("ruleCount").GetInt32());
    }
}
