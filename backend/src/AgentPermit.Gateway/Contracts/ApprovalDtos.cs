using System.Text.Json.Serialization;

namespace AgentPermit.Gateway.Contracts;

/// <summary>A pending approval, as shown to an approver (e.g. on a Teams card).</summary>
public sealed class ApprovalSummaryDto
{
    [JsonPropertyName("approvalRequestId")]
    public Guid ApprovalRequestId { get; set; }

    [JsonPropertyName("actionRequestId")]
    public Guid ActionRequestId { get; set; }

    [JsonPropertyName("actionId")]
    public string ActionId { get; set; } = string.Empty;

    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("customerId")]
    public string? CustomerId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// The COMPLETE frozen request payload — every parameter, not just the refund-shaped quartet above. An
    /// approver signs off on this; a bank-detail change must show the IBAN being approved.
    /// </summary>
    [JsonPropertyName("parameters")]
    public System.Text.Json.JsonElement? Parameters { get; set; }

    /// <summary>The human on whose behalf the agent acted, if the request carried one (separation of duties).</summary>
    [JsonPropertyName("initiatingUserId")]
    public string? InitiatingUserId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("approverRole")]
    public string? ApproverRole { get; set; }

    /// <summary>The payload hash frozen at request time; an approver approves *this* exact payload.</summary>
    [JsonPropertyName("payloadHash")]
    public string PayloadHash { get; set; } = string.Empty;

    [JsonPropertyName("requestedAtUtc")]
    public DateTimeOffset RequestedAtUtc { get; set; }

    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>How many different people must approve before the action runs (1 = a single approver).</summary>
    [JsonPropertyName("requiredApprovals")]
    public int RequiredApprovals { get; set; } = 1;

    /// <summary>Who has approved so far, when several approvers are required.</summary>
    [JsonPropertyName("approvedBy")]
    public string[] ApprovedBy { get; set; } = Array.Empty<string>();
}

/// <summary>Body for approve/reject.</summary>
public sealed class ApprovalDecisionRequestDto
{
    [JsonPropertyName("approverId")]
    public string? ApproverId { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    /// <summary>
    /// Optional: the payload hash the approver believes they are approving. If supplied and it
    /// no longer matches the frozen request payload, the approval is refused (tamper protection).
    /// </summary>
    [JsonPropertyName("expectedPayloadHash")]
    public string? ExpectedPayloadHash { get; set; }
}

/// <summary>Result of an approve/reject decision.</summary>
public sealed class ApprovalDecisionResponseDto
{
    [JsonPropertyName("approvalRequestId")]
    public Guid ApprovalRequestId { get; set; }

    [JsonPropertyName("actionRequestId")]
    public Guid ActionRequestId { get; set; }

    /// <summary>Executed | Rejected | PayloadChanged | Expired | AlreadyDecided | NotFound | Failed.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Present when the approval led to a successful execution.</summary>
    [JsonPropertyName("receipt")]
    public ReceiptDto? Receipt { get; set; }

    /// <summary>When several approvers are required: how many different people have approved so far.</summary>
    [JsonPropertyName("approvalsReceived")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ApprovalsReceived { get; set; }

    /// <summary>When several approvers are required: how many are needed in total.</summary>
    [JsonPropertyName("approvalsRequired")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ApprovalsRequired { get; set; }
}

/// <summary>Outcome codes for an approval decision.</summary>
public static class ApprovalOutcome
{
    public const string Executed = "Executed";
    public const string Rejected = "Rejected";
    public const string PayloadChanged = "PayloadChanged";
    public const string Expired = "Expired";
    public const string AlreadyDecided = "AlreadyDecided";
    public const string NotFound = "NotFound";
    public const string Failed = "Failed";

    /// <summary>The approver is not a member of the approver group the rule requires.</summary>
    public const string ApproverRoleRequired = "ApproverRoleRequired";

    /// <summary>The approver is the person who initiated the request or owns the agent — four-eyes violated.</summary>
    public const string SeparationOfDuties = "SeparationOfDuties";

    /// <summary>Several approvers are required: this approval counted, and more are still needed. Nothing ran yet.</summary>
    public const string ApprovalRecorded = "ApprovalRecorded";

    /// <summary>Several approvers are required and this person has already approved; the same person can't count twice.</summary>
    public const string AlreadyApprovedByYou = "AlreadyApprovedByYou";
}
