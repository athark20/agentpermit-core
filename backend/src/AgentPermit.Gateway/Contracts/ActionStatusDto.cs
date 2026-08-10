using System.Text.Json.Serialization;

namespace AgentPermit.Gateway.Contracts;

/// <summary>
/// Current state of a previously submitted action request. Returned by
/// GET /api/actions/{id} and surfaced to Copilot via the connector's GetActionStatus.
/// </summary>
public sealed class ActionStatusDto
{
    [JsonPropertyName("actionRequestId")]
    public Guid ActionRequestId { get; set; }

    [JsonPropertyName("actionId")]
    public string ActionId { get; set; } = string.Empty;

    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>ALLOW | DENY | REQUIRE_APPROVAL (from the recorded policy decision).</summary>
    [JsonPropertyName("decision")]
    public string? Decision { get; set; }

    /// <summary>Denied | PendingApproval | Executing | Executed | Failed.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("policyCode")]
    public string? PolicyCode { get; set; }

    [JsonPropertyName("policyVersion")]
    public string? PolicyVersion { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>Present once the action has executed.</summary>
    [JsonPropertyName("receipt")]
    public ReceiptDto? Receipt { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
