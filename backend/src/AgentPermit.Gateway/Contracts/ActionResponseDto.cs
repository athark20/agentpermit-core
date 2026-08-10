using System.Text.Json.Serialization;

namespace AgentPermit.Gateway.Contracts;

/// <summary>
/// Envelope returned by POST /api/actions/request. A single shape carries all
/// three outcomes (ALLOW / DENY / REQUIRE_APPROVAL) so the Copilot connector has
/// one contract to bind against.
/// </summary>
public sealed class ActionResponseDto
{
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("actionRequestId")]
    public Guid? ActionRequestId { get; set; }

    [JsonPropertyName("actionId")]
    public string ActionId { get; set; } = string.Empty;

    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = string.Empty;

    /// <summary>ALLOW | DENY | REQUIRE_APPROVAL.</summary>
    [JsonPropertyName("decision")]
    public string Decision { get; set; } = string.Empty;

    /// <summary>Lifecycle status: Denied | PendingApproval | Executed | Failed.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// How binding this decision was: ENFORCED | OBSERVED | WARNED. OBSERVED/WARNED means the agent was in
    /// a warm-up posture and a low/medium-consequence block was recorded but not applied — see
    /// <see cref="ComputedDecision"/> for what the policy would have decided.
    /// </summary>
    [JsonPropertyName("enforcementMode")]
    public string? EnforcementMode { get; set; }

    /// <summary>The policy's computed decision, present only when it differs from <see cref="Decision"/>
    /// because a warm-up posture downgraded it (evidence of the would-have-been outcome).</summary>
    [JsonPropertyName("computedDecision")]
    public string? ComputedDecision { get; set; }

    [JsonPropertyName("policyCode")]
    public string PolicyCode { get; set; } = string.Empty;

    [JsonPropertyName("policyVersion")]
    public string PolicyVersion { get; set; } = string.Empty;

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("payloadHash")]
    public string? PayloadHash { get; set; }

    /// <summary>Set when the decision is REQUIRE_APPROVAL and an approval was opened.</summary>
    [JsonPropertyName("approvalRequestId")]
    public Guid? ApprovalRequestId { get; set; }

    /// <summary>Set when the action was executed (ALLOW path).</summary>
    [JsonPropertyName("receipt")]
    public ReceiptDto? Receipt { get; set; }

    /// <summary>The executed action's result data as JSON (e.g. the vendor/invoice looked up, or the payment
    /// created) — what the calling agent needs to answer the user. Null when nothing executed.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    /// <summary>Flat copies of the receipt's key ids, for easy binding by connectors/agents.</summary>
    [JsonPropertyName("receiptNumber")]
    public string? ReceiptNumber { get; set; }

    [JsonPropertyName("targetResourceId")]
    public string? TargetResourceId { get; set; }

    /// <summary>True when this response replays a previously seen idempotency key.</summary>
    [JsonPropertyName("replayed")]
    public bool Replayed { get; set; }

    [JsonPropertyName("evaluatedAtUtc")]
    public DateTimeOffset EvaluatedAtUtc { get; set; }
}

public sealed class ReceiptDto
{
    [JsonPropertyName("receiptId")]
    public Guid ReceiptId { get; set; }

    [JsonPropertyName("receiptNumber")]
    public string ReceiptNumber { get; set; } = string.Empty;

    [JsonPropertyName("executionStatus")]
    public string ExecutionStatus { get; set; } = string.Empty;

    [JsonPropertyName("targetResourceId")]
    public string? TargetResourceId { get; set; }

    [JsonPropertyName("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [JsonPropertyName("receiptHash")]
    public string ReceiptHash { get; set; } = string.Empty;

    [JsonPropertyName("previousReceiptHash")]
    public string? PreviousReceiptHash { get; set; }

    /// <summary>Detached RS256 signature over the receipt hash (null when signing is not configured).</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("signatureAlgorithm")]
    public string? SignatureAlgorithm { get; set; }

    [JsonPropertyName("signingKeyId")]
    public string? SigningKeyId { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }
}
