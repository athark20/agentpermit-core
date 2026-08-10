using System.Text.Json;
using AgentPermit.Gateway.Domain;

namespace AgentPermit.Gateway.Policies;

/// <summary>
/// Evaluates the active immutable policy snapshot: builds a field context from the request,
/// walks the applicable rules in priority order, and returns the first match — or the
/// snapshot's default (DENY). Fully deterministic; no external state, no per-request DB call.
/// </summary>
public sealed class ConfigurablePolicyEngine(IPolicySnapshotProvider snapshots) : IPolicyEngine
{
    public PolicyEvaluationResult Evaluate(PolicyEvaluationContext context)
    {
        var snapshot = snapshots.Current;
        var version = string.IsNullOrEmpty(snapshot.Version) ? "unversioned" : snapshot.Version;

        // Effective-date window: outside it, no rules apply -> default decision.
        var now = DateTimeOffset.UtcNow;
        var withinWindow = (snapshot.EffectiveFrom is null || snapshot.EffectiveFrom <= now)
                           && (snapshot.EffectiveTo is null || snapshot.EffectiveTo >= now);

        if (withinWindow)
        {
            var fields = BuildFieldContext(context);
            foreach (var rule in snapshot.Rules
                         .Where(r => string.IsNullOrEmpty(r.ActionId)
                                     || string.Equals(r.ActionId, context.Action.ActionId, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(r => r.Priority))
            {
                if (rule.Conditions.All(c => PolicyConditionEvaluator.Matches(Lookup(fields, c.Field), c.Operator, c.Value)))
                    return Match(snapshot, version, rule);
            }
        }

        return Default(snapshot, version, withinWindow);
    }

    private static PolicyEvaluationResult Match(PolicySnapshot snapshot, string version, PolicyRule rule) => new()
    {
        Decision = rule.Decision,
        PolicyCode = snapshot.PolicyCode,
        PolicyVersion = version,
        ReasonCode = ReasonFor(rule.Decision),
        Reason = rule.Reason ?? $"Matched rule '{rule.Name}' ({rule.Id}).",
        MatchedRuleId = rule.Id,
        RequiredApproverRoles = rule.ApproverRoles,
        TraceJson = JsonSerializer.Serialize(new
        {
            matchedRuleId = rule.Id,
            ruleName = rule.Name,
            priority = rule.Priority,
            decision = rule.Decision,
            snapshotVersion = version
        })
    };

    private static PolicyEvaluationResult Default(PolicySnapshot snapshot, string version, bool withinWindow) => new()
    {
        Decision = string.IsNullOrEmpty(snapshot.DefaultDecision) ? Decision.Deny : snapshot.DefaultDecision,
        PolicyCode = snapshot.PolicyCode,
        PolicyVersion = version,
        ReasonCode = ReasonFor(snapshot.DefaultDecision),
        Reason = withinWindow
            ? "No rule matched; default decision applied."
            : "Policy snapshot is outside its effective window; default decision applied.",
        MatchedRuleId = null,
        TraceJson = JsonSerializer.Serialize(new { matchedRuleId = (string?)null, withinWindow, snapshotVersion = version })
    };

    private static string ReasonFor(string decision) => decision switch
    {
        Decision.Allow => ReasonCode.PolicyAllow,
        Decision.RequireApproval => ReasonCode.PolicyRequireApproval,
        _ => ReasonCode.PolicyDeny
    };

    /// <summary>Flatten request attributes into a field lookup the conditions can reference.</summary>
    private static Dictionary<string, JsonElement> BuildFieldContext(PolicyEvaluationContext ctx)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        if (ctx.Parameters.ValueKind == JsonValueKind.Object)
            foreach (var prop in ctx.Parameters.EnumerateObject())
                fields[prop.Name] = prop.Value;

        void Add(string name, object? value)
        {
            if (value is not null) fields[name] = JsonSerializer.SerializeToElement(value);
        }

        Add("actionId", ctx.Action.ActionId);
        Add("agentId", ctx.AgentId);
        Add("rootAgentId", ctx.RootAgentId);
        Add("initiatingUserId", ctx.InitiatingUserId);
        Add("action.risk", ctx.Action.Risk);
        Add("action.domain", ctx.Action.Domain);
        Add("action.targetSystem", ctx.Action.TargetSystem);
        return fields;
    }

    private static JsonElement? Lookup(Dictionary<string, JsonElement> fields, string field)
        => fields.TryGetValue(field, out var v) ? v : null;
}
