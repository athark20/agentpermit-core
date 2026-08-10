using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Registry;

namespace AgentPermit.Gateway.Mcp;

/// <summary>
/// Bridges AgentPermit's governed actions to the Model Context Protocol. Each registered action is exposed
/// as an MCP tool, and a <c>tools/call</c> is translated into the SAME <see cref="ActionRequestDto"/> the
/// HTTP path uses — so the policy / identity / idempotency / receipt / consequence-tier pipeline is reused
/// unchanged. MCP is just another front door; no enforcement is bypassed, relaxed, or re-implemented here.
/// </summary>
public sealed class McpToolCatalog(IActionRegistry actions)
{
    /// <summary>Every governed action as an MCP tool definition (name + description + JSON input schema).</summary>
    public IReadOnlyList<JsonObject> ListTools()
    {
        var tools = new List<JsonObject>();
        foreach (var a in actions.All())
            tools.Add(new JsonObject
            {
                ["name"] = a.ActionId,
                ["description"] = string.IsNullOrWhiteSpace(a.DisplayName) ? a.ActionId : a.DisplayName,
                ["inputSchema"] = BuildInputSchema(a)
            });
        return tools;
    }

    /// <summary>The action's own parameter schema, plus the caller-owned <c>idempotencyKey</c> (required).</summary>
    private static JsonNode BuildInputSchema(ActionDefinition a)
    {
        var schema = a.RequestSchema.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(a.RequestSchema.GetRawText())!.AsObject()
            : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

        schema["type"] ??= "object";
        var props = schema["properties"] as JsonObject ?? new JsonObject();
        props["idempotencyKey"] = new JsonObject
        {
            ["type"] = "string",
            ["minLength"] = 8,
            ["description"] = "Caller-owned unique id for THIS request (>= 8 chars). A retry with the same key is de-duplicated; never reuse it for a different action."
        };
        schema["properties"] = props;

        var required = schema["required"] as JsonArray ?? new JsonArray();
        if (!required.Any(n => (string?)n == "idempotencyKey")) required.Add("idempotencyKey");
        schema["required"] = required;
        return schema;
    }

    /// <summary>Map an MCP tool call into an action request. Returns null when the tool is unknown.</summary>
    public ActionRequestDto? ToRequest(string toolName, JsonElement arguments)
    {
        if (actions.Find(toolName) is null) return null;

        string? idem = null, agentId = null, agentIdentity = null;
        var parameters = new JsonObject();
        if (arguments.ValueKind == JsonValueKind.Object)
            foreach (var p in arguments.EnumerateObject())
                switch (p.Name)
                {
                    case "idempotencyKey": idem = AsString(p.Value); break;
                    case "agentId": agentId = AsString(p.Value); break;         // identity is normally derived from auth
                    case "agentIdentity": agentIdentity = AsString(p.Value); break;
                    default: parameters[p.Name] = JsonNode.Parse(p.Value.GetRawText()); break;
                }

        return new ActionRequestDto
        {
            ActionId = toolName,
            AgentId = agentId,
            AgentIdentity = agentIdentity,
            IdempotencyKey = idem,
            Parameters = JsonSerializer.SerializeToElement(parameters)
        };
    }

    private static string? AsString(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
