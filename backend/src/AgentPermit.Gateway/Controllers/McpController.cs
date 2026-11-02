using System.Text.Json;
using AgentPermit.Gateway.Mcp;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Model Context Protocol (MCP) server front-door — JSON-RPC 2.0 over HTTP. Lets MCP-capable agents
/// (pro-code SDKs or low-code hosts) discover AgentPermit's governed actions as tools and invoke them.
/// A <c>tools/call</c> is executed through the SAME <see cref="ActionRequestService"/> as the REST path,
/// so policy, agent identity, caller-owned idempotency, signed receipts and consequence tiers all apply
/// exactly as before — MCP adds a transport, not an enforcement bypass.
/// </summary>
[ApiController]
[Route("mcp")]
public sealed class McpController(
    McpToolCatalog catalog, ActionRequestService actions, CallerContextFactory callers, ILogger<McpController> logger) : ControllerBase
{
    private const string DefaultProtocolVersion = "2024-11-05";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Streamable-HTTP clients may open a GET to receive server-initiated messages over SSE. AgentPermit's
    /// MCP server is request/response only (no server-initiated notifications), so per the spec it returns
    /// 405 here — clients fall back to POST-only, which is fully supported.
    /// </summary>
    [HttpGet]
    public IActionResult NoServerStream()
    {
        Response.Headers.Allow = "POST";
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    [HttpPost]
    public async Task<IActionResult> Rpc([FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind == JsonValueKind.Array)
        {
            var batch = new List<object>();
            foreach (var el in body.EnumerateArray())
                if (await HandleOne(el, ct) is { } r) batch.Add(r);
            return batch.Count == 0 ? Accepted() : Ok(batch);
        }
        return await HandleOne(body, ct) is { } single ? Ok(single) : Accepted();
    }

    private async Task<object?> HandleOne(JsonElement req, CancellationToken ct)
    {
        var id = req.TryGetProperty("id", out var idEl) && idEl.ValueKind is not JsonValueKind.Null ? (JsonElement?)idEl : null;
        var method = req.TryGetProperty("method", out var m) ? m.GetString() : null;
        var prm = req.TryGetProperty("params", out var p) ? p : default;

        object Result(object result) => new Envelope { Id = id, Result = result };
        object Error(int code, string message) => new Envelope { Id = id, Error = new { code, message } };

        try
        {
            switch (method)
            {
                case "initialize":
                    var pv = prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty("protocolVersion", out var pvEl)
                             && pvEl.ValueKind == JsonValueKind.String ? pvEl.GetString()! : DefaultProtocolVersion;
                    return Result(new { protocolVersion = pv, capabilities = new { tools = new { } }, serverInfo = new { name = "AgentPermit", version = "1.0.0" } });

                case "notifications/initialized":
                case "notifications/cancelled":
                    return null; // notifications get no response

                case "ping":
                    return Result(new { });

                case "tools/list":
                    return Result(new { tools = catalog.ListTools() });

                case "tools/call":
                    return id is null ? null : await ToolsCall(prm, Result, Error, ct);

                default:
                    return id is null ? null : Error(-32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP method {Method} failed.", method);
            return id is null ? null : Error(-32603, "Internal error");
        }
    }

    private async Task<object> ToolsCall(JsonElement prm, Func<object, object> Result, Func<int, string, object> Error, CancellationToken ct)
    {
        var name = prm.ValueKind == JsonValueKind.Object && prm.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return Error(-32602, "tool name is required");
        var args = prm.TryGetProperty("arguments", out var a) ? a : default;

        var dto = catalog.ToRequest(name!, args);
        if (dto is null) return Result(ToolContent($"Unknown tool '{name}'.", isError: true));

        try
        {
            var response = await actions.ProcessAsync(dto, await callers.BuildAsync(HttpContext), ct);
            // A governance DENY/REQUIRE_APPROVAL is a valid tool result, not a protocol error — surface it as content.
            return Result(ToolContent(JsonSerializer.Serialize(response, Json), isError: false));
        }
        catch (GatewayValidationException ex)
        {
            return Result(ToolContent($"Invalid tool arguments: {ex.Message}", isError: true));
        }
    }

    private static object ToolContent(string text, bool isError) =>
        new { content = new[] { new { type = "text", text } }, isError };

    /// <summary>JSON-RPC 2.0 response envelope; omits the field that isn't set (result XOR error).</summary>
    private sealed class Envelope
    {
        public string jsonrpc => "2.0";
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public JsonElement? Id { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("result")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public object? Result { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public object? Error { get; init; }
    }
}
