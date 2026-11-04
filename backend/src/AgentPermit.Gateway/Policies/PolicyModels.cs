using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPermit.Gateway.Policies;

/// <summary>
/// Turns a condition from "this request's field" into "this field aggregated over recent requests".
/// <c>{ field: "amount", operator: "greaterThan", value: 5000, aggregate: { function: "sum", window: "PT24H", groupBy: ["agentId","customerId"] } }</c>
/// reads: the sum of <c>amount</c> over the last 24 h for this agent and this customer — <em>including the
/// current request</em> — is greater than 5000. This is what stops salami-slicing: a per-request threshold of
/// 1,000 says nothing about a hundred 999s.
/// </summary>
public sealed class PolicyAggregate
{
    public const string Sum = "sum";
    public const string Count = "count";
    public const string Distinct = "distinct";
    public const string Max = "max";
    public static readonly string[] Functions = { Sum, Count, Distinct, Max };

    public const string ScopeCommitted = "committed";
    public const string ScopeAll = "all";
    public static readonly string[] Scopes = { ScopeCommitted, ScopeAll };

    /// <summary>sum | count | distinct | max. <c>count</c> ignores the field; <c>distinct</c> counts distinct field values.</summary>
    [JsonPropertyName("function")]
    public string Function { get; set; } = Sum;

    /// <summary>Look-back window as an ISO-8601 duration (PT1H, PT24H, P7D).</summary>
    [JsonPropertyName("window")]
    public string Window { get; set; } = "PT24H";

    /// <summary>
    /// Fields whose values must equal the current request's to count: <c>agentId</c>, <c>initiatingUserId</c>,
    /// <c>rootAgentId</c>, or any request parameter name (e.g. <c>customerId</c>, <c>toIban</c>). Default: agentId.
    /// </summary>
    [JsonPropertyName("groupBy")]
    public string[] GroupBy { get; set; } = { "agentId" };

    /// <summary>Action ids to aggregate across. Default (null): only the rule's own action.</summary>
    [JsonPropertyName("actions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Actions { get; set; }

    /// <summary>
    /// <c>committed</c> (default): requests that ran or are pending (SUCCEEDED, EXECUTING, EXECUTION_PENDING,
    /// APPROVED, AWAITING_APPROVAL) — i.e. real or pending exposure. <c>all</c>: every request including
    /// denials — useful to throttle an agent that keeps asking.
    /// </summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = ScopeCommitted;

    /// <summary>Canonical key for this aggregate over <paramref name="field"/> (dedupes identical specs within a snapshot).</summary>
    public string Key(string field, string ruleActionId) =>
        $"agg:{Function.ToLowerInvariant()}:{field}:{Window}:{string.Join(',', GroupBy ?? Array.Empty<string>())}:" +
        $"{string.Join(',', Actions ?? new[] { ruleActionId })}:{Scope.ToLowerInvariant()}";
}

/// <summary>One deterministic condition, e.g. { field: "amount", operator: "lessThan", value: 250 }.</summary>
public sealed class PolicyCondition
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    /// <summary>Comparison value as raw JSON (number, string, boolean, or array for in/notIn).</summary>
    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }

    /// <summary>
    /// When present, <see cref="Field"/> is aggregated over a window of recent requests instead of read from
    /// this request alone. Omitted when null so snapshots signed before this existed still hash identically.
    /// </summary>
    [JsonPropertyName("aggregate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PolicyAggregate? Aggregate { get; set; }
}

/// <summary>
/// A rule: when ALL conditions match (for the applicable action), the decision applies.
/// Rules are evaluated in ascending Priority; the first match wins.
/// </summary>
public sealed class PolicyRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    /// <summary>Restrict this rule to one action id; null/empty = any action.</summary>
    [JsonPropertyName("actionId")]
    public string? ActionId { get; set; }

    [JsonPropertyName("conditions")]
    public List<PolicyCondition> Conditions { get; set; } = new();

    [JsonPropertyName("decision")]
    public string Decision { get; set; } = string.Empty; // ALLOW | DENY | REQUIRE_APPROVAL

    [JsonPropertyName("approverRoles")]
    public string[] ApproverRoles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// REQUIRE_APPROVAL only: how many DIFFERENT people must approve before the action runs (1–5). Each one must
    /// be eligible on their own (approver group, separation of duties), and a single rejection stops the action.
    /// Omitted when null (one approver) so snapshots signed before this field existed still hash identically.
    /// </summary>
    [JsonPropertyName("requiredApprovals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequiredApprovals { get; set; }

    /// <summary>
    /// Human description of the rule (author aid; not used by the engine). Omitted when null so that
    /// snapshots signed before this field existed still hash identically and verify.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// An immutable, versioned set of rules. In the enterprise flow this is compiled from
/// Dataverse authoring, integrity-hashed (and later signed), published, and cached in the
/// runtime — the gateway never queries Dataverse per request.
/// </summary>
public sealed class PolicySnapshot
{
    [JsonPropertyName("policyCode")]
    public string PolicyCode { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>Integrity hash over the canonical rules; verified on load when present.</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    /// <summary>Detached RS256 signature over the hash (Key Vault); makes a published snapshot tamper-evident.</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("signatureAlgorithm")]
    public string? SignatureAlgorithm { get; set; }

    [JsonPropertyName("signingKeyId")]
    public string? SigningKeyId { get; set; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTimeOffset? PublishedAtUtc { get; set; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    [JsonPropertyName("effectiveFrom")]
    public DateTimeOffset? EffectiveFrom { get; set; }

    [JsonPropertyName("effectiveTo")]
    public DateTimeOffset? EffectiveTo { get; set; }

    /// <summary>Decision when no rule matches (enterprise posture: DENY).</summary>
    [JsonPropertyName("defaultDecision")]
    public string DefaultDecision { get; set; } = "DENY";

    [JsonPropertyName("rules")]
    public List<PolicyRule> Rules { get; set; } = new();
}
