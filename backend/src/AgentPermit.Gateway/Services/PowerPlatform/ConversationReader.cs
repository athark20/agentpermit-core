using System.Text.Json;

namespace AgentPermit.Gateway.Services.PowerPlatform;

/// <summary>One normalized step in a Copilot Studio conversation (for the graphical flow in Investigate).</summary>
public sealed class ConversationStep
{
    /// <summary>user | bot | thinking | tool | governance.</summary>
    public string Kind { get; set; } = "";
    public string? Text { get; set; }
    public string? Tool { get; set; }
    public string? ActionId { get; set; }
    /// <summary>Result/decision string from the tool (e.g. ALLOW / DENY / suspended_for_consent / completed).</summary>
    public string? Decision { get; set; }
    public int? DurationMs { get; set; }
    public bool NeedsApproval { get; set; }
    public bool IsAgentPermit { get; set; }
    public DateTimeOffset? Ts { get; set; }
}

/// <summary>A parsed Copilot Studio conversation — the agent's reasoning + tool calls, with AgentPermit calls flagged.</summary>
public sealed class ConversationSummary
{
    public string ConversationId { get; set; } = "";
    public string? AgentId { get; set; }        // AgentPermit agentId, pulled from the "Request an action" call
    public string? BotName { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public bool HasGovernance { get; set; }
    public int GovernedCalls { get; set; }
    public string? Outcome { get; set; }        // last AgentPermit result seen
    public List<ConversationStep> Steps { get; set; } = new();
}

/// <summary>
/// Reads Copilot Studio conversation transcripts from Dataverse and parses the (very large) activity
/// stream into a compact, graph-friendly step list. This is the agent-reasoning layer of monitoring —
/// obtained WITHOUT any per-agent App Insights setup, straight from data already in the customer's Dataverse.
/// </summary>
public static class ConversationParser
{
    public static ConversationSummary Parse(string conversationId, string? botName, DateTimeOffset? start, JsonElement content)
    {
        var s = new ConversationSummary { ConversationId = conversationId, BotName = botName, StartTime = start };
        if (!content.TryGetProperty("activities", out var acts) || acts.ValueKind != JsonValueKind.Array)
            return s;

        // Pair tool Started/Completed by toolCallId.
        var started = new Dictionary<string, ConversationStep>(StringComparer.Ordinal);
        ConversationStep? pendingThinking = null;

        foreach (var a in acts.EnumerateArray())
        {
            var type = Str(a, "type");
            var name = Str(a, "name");
            var ts = a.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(t.GetString(), out var dt) ? dt : (DateTimeOffset?)null;

            // Reasoning is streamed token-by-token — merge consecutive ThinkingTrace into one block.
            if (name == "ThinkingTrace")
            {
                var think = a.TryGetProperty("value", out var tv) && tv.ValueKind == JsonValueKind.Object ? Str(tv, "thinkingText") : null;
                if (!string.IsNullOrEmpty(think))
                {
                    if (pendingThinking is null) { pendingThinking = new ConversationStep { Kind = "thinking", Text = think, Ts = ts }; s.Steps.Add(pendingThinking); }
                    else pendingThinking.Text += think;
                }
                continue;
            }
            pendingThinking = null;

            if (type == "message")
            {
                var text = Str(a, "text");
                if (string.IsNullOrWhiteSpace(text)) continue;
                var role = a.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object ? Str(f, "role") : null;
                s.Steps.Add(new ConversationStep { Kind = role == "1" ? "user" : "bot", Text = Trim(text, 400), Ts = ts });
                continue;
            }

            if (name == "ToolCallTrace:Started" && a.TryGetProperty("value", out var sv) && sv.ValueKind == JsonValueKind.Object)
            {
                var toolCallId = Str(sv, "toolCallId") ?? Guid.NewGuid().ToString();
                var display = Str(sv, "toolDisplayName") ?? Str(sv, "toolName");
                var (actionId, agentId) = ExtractActionParams(sv.TryGetProperty("filledParameters", out var fpEl) ? fpEl : default);
                var isAp = IsAgentPermitTool(Str(sv, "toolName"), display);
                var step = new ConversationStep { Kind = isAp ? "governance" : "tool", Tool = display, ActionId = actionId, IsAgentPermit = isAp, Ts = ts };
                if (isAp) { s.HasGovernance = true; s.GovernedCalls++; if (agentId is not null) s.AgentId ??= agentId; }
                started[toolCallId] = step;
                s.Steps.Add(step);
                continue;
            }

            if (name == "ToolCallTrace:Completed" && a.TryGetProperty("value", out var cv) && cv.ValueKind == JsonValueKind.Object)
            {
                var toolCallId = Str(cv, "toolCallId") ?? "";
                if (started.TryGetValue(toolCallId, out var step))
                {
                    step.DurationMs = cv.TryGetProperty("durationMs", out var dm) && dm.TryGetInt32(out var ms) ? ms : null;
                    step.Decision = ExtractResultStatus(Str(cv, "result"));
                    if (step.IsAgentPermit && step.Decision is not null) s.Outcome = step.Decision;
                }
                continue;
            }

            if (name == "turn.awaitConsent")
            {
                // Flag the most recent AgentPermit call as awaiting a human sign-off.
                for (var i = s.Steps.Count - 1; i >= 0; i--)
                    if (s.Steps[i].IsAgentPermit) { s.Steps[i].NeedsApproval = true; break; }
            }
        }
        return s;
    }

    // filledParameters and its "body" may each be a JSON object OR a JSON string — handle both.
    private static (string? actionId, string? agentId) ExtractActionParams(JsonElement filledParameters)
    {
        try
        {
            var fp = AsObject(filledParameters);
            if (fp is null || !fp.Value.TryGetProperty("body", out var bodyEl)) return (null, null);
            var body = AsObject(bodyEl);
            if (body is null) return (null, null);
            var r = body.Value;
            return (r.TryGetProperty("actionId", out var ai) && ai.ValueKind == JsonValueKind.String ? ai.GetString() : null,
                    r.TryGetProperty("agentId", out var ag) && ag.ValueKind == JsonValueKind.String ? ag.GetString() : null);
        }
        catch { return (null, null); }
    }

    // Return an object element directly, or parse it if it's a JSON-encoded string. Caller keeps the doc alive
    // via GetRawText round-trip so returned elements stay valid.
    private static JsonElement? AsObject(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object) return e;
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            try { return JsonDocument.Parse(s).RootElement.Clone(); } catch { return null; }
        }
        return null;
    }

    private static string? ExtractResultStatus(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var r = doc.RootElement;
            foreach (var key in new[] { "decision", "Decision", "status", "Status" })
                if (r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
            return null;
        }
        catch { return resultJson.Length > 60 ? null : resultJson; }
    }

    private static bool IsAgentPermitTool(string? toolName, string? display)
    {
        var n = $"{toolName} {display}".ToLowerInvariant();
        return n.Contains("requestanaction") || n.Contains("request an action") || n.Contains("agentpermit");
    }

    private static string? Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
