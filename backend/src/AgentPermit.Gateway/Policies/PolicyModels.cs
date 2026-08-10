using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPermit.Gateway.Policies;

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
