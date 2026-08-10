using System.Net.Http.Json;
using System.Text.Json;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// The MCP (Model Context Protocol) front-door. It exposes governed actions as MCP tools over JSON-RPC and
/// routes tools/call through the same enforcement pipeline as the REST path — identity, caller-owned
/// idempotency and policy still apply; MCP does not relax them.
/// </summary>
public sealed class McpServerTests
{
    private static async Task<JsonElement> Rpc(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/mcp", body);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Initialize_ReturnsProtocolVersion_AndServerInfo()
    {
        using var f = new GatewayFactory();
        var r = await Rpc(f.CreateClient(), new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2024-11-05" } });
        var result = r.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("AgentPermit", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task ToolsList_ExposesGovernedActions_WithIdempotencyKeyRequired()
    {
        using var f = new GatewayFactory();
        var r = await Rpc(f.CreateClient(), new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var tools = r.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("Refund.Issue", names);
        Assert.Contains("Payment.Issue", names);

        var payment = tools.First(t => t.GetProperty("name").GetString() == "Payment.Issue");
        var required = payment.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(x => x.GetString());
        Assert.Contains("idempotencyKey", required); // caller-owned idempotency is part of the tool contract
    }

    [Fact]
    public async Task ToolsCall_MissingIdempotencyKey_IsToolError()
    {
        using var f = new GatewayFactory();
        var r = await Rpc(f.CreateClient(), new
        {
            jsonrpc = "2.0", id = 3, method = "tools/call",
            @params = new { name = "Vendor.Lookup", arguments = new { vendorCode = "V-1001", agentId = "copilot-refund-demo" } }
        });
        var result = r.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean()); // caller-owned idempotency is required — gateway rejects, MCP surfaces it
        Assert.Contains("idempotency", result.GetProperty("content")[0].GetProperty("text").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolsCall_RoutesThroughPolicyPipeline_AndReturnsAGovernedDecision()
    {
        using var f = new GatewayFactory();
        var r = await Rpc(f.CreateClient(), new
        {
            jsonrpc = "2.0", id = 4, method = "tools/call",
            @params = new { name = "Vendor.Lookup", arguments = new { vendorCode = "V-1001", agentId = "copilot-refund-demo", idempotencyKey = "mcp-test-0001" } }
        });
        var result = r.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        // The tool result carries the governed response (a real decision from the policy engine).
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("decision", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsJsonRpcMethodNotFound()
    {
        using var f = new GatewayFactory();
        var r = await Rpc(f.CreateClient(), new { jsonrpc = "2.0", id = 5, method = "does/notExist" });
        Assert.Equal(-32601, r.GetProperty("error").GetProperty("code").GetInt32());
    }
}
