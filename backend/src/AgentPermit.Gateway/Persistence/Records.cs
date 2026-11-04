namespace AgentPermit.Gateway.Persistence;

/// <summary>Aggregate counts for the admin dashboard.</summary>
public sealed class PlatformMetrics
{
    public int TotalRequests { get; set; }
    public Dictionary<string, int> ByStatus { get; set; } = new();
    public int TodayTotal { get; set; }
    public int TodayAllowed { get; set; }
    public int TodayDenied { get; set; }
    public int TodayApproval { get; set; }
    public int TodayFailed { get; set; }
}

/// <summary>One agent's activity totals over a window — the centralized fleet trail row.</summary>
public sealed class AgentTrailRow
{
    public string AgentId { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Allowed { get; set; }
    public int StepUp { get; set; }
    public int Denied { get; set; }
    public int Failed { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
}

/// <summary>
/// Builds <see cref="PlatformMetrics"/> from raw (status, count) groupings, normalizing legacy status
/// spellings (from before the canonical state machine) to canonical buckets so the dashboard is
/// consistent regardless of when a request was recorded.
/// </summary>
public static class MetricsBuilder
{
    public static string Canonical(string? status) => (status ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "EXECUTED" or "SUCCEEDED" => "SUCCEEDED",
        "PENDINGAPPROVAL" or "AWAITING_APPROVAL" => "AWAITING_APPROVAL",
        "DENIED" => "DENIED",
        "REJECTED" => "REJECTED",
        "FAILED" => "FAILED",
        "EXPIRED" => "EXPIRED",
        "CANCELLED" or "CANCELED" => "CANCELLED",
        var other => string.IsNullOrEmpty(other) ? "UNKNOWN" : other,
    };

    public static PlatformMetrics Build(
        IEnumerable<(string Status, int Count)> all, IEnumerable<(string Status, int Count)> today)
    {
        var byStatus = Fold(all);
        var t = Fold(today);
        return new PlatformMetrics
        {
            TotalRequests = byStatus.Values.Sum(),
            ByStatus = byStatus,
            TodayTotal = t.Values.Sum(),
            TodayAllowed = t.GetValueOrDefault("SUCCEEDED"),
            TodayDenied = t.GetValueOrDefault("DENIED") + t.GetValueOrDefault("REJECTED"),
            TodayApproval = t.GetValueOrDefault("AWAITING_APPROVAL"),
            TodayFailed = t.GetValueOrDefault("FAILED"),
        };
    }

    private static Dictionary<string, int> Fold(IEnumerable<(string Status, int Count)> rows)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (status, count) in rows)
        {
            var c = Canonical(status);
            d[c] = d.GetValueOrDefault(c) + count;
        }
        return d;
    }
}

/// <summary>
/// A windowed look-back over dbo.ActionRequest for policy aggregates: "values of <see cref="ValueField"/> in
/// requests for these actions since <see cref="Since"/>, in these statuses, that share these group-by values".
/// Field names are validated by the caller (<c>^[A-Za-z0-9_.-]+$</c>); stores must still parameterise values.
/// </summary>
public sealed class AggregateQuery
{
    public required string[] ActionIds { get; init; }
    public required DateTimeOffset Since { get; init; }
    /// <summary>Null = any status.</summary>
    public string[]? Statuses { get; init; }
    public string? AgentId { get; init; }
    public string? RootAgentId { get; init; }
    public string? InitiatingUserId { get; init; }
    /// <summary>Request parameters that must equal these values (top-level JSON properties of the payload).</summary>
    public IReadOnlyDictionary<string, string> ParameterEquals { get; init; } = new Dictionary<string, string>();
    /// <summary>Top-level payload property whose value to return per matching row; null = return one null per row (count only).</summary>
    public string? ValueField { get; init; }
}

/// <summary>Row in dbo.ActionRequest — the audit spine of every request.</summary>
public sealed class ActionRequestRecord
{
    public Guid ActionRequestId { get; set; }
    public Guid CorrelationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? RootAgentId { get; set; }
    public string ExecutingAgentId { get; set; } = string.Empty;
    public string? InitiatingUserId { get; set; }
    public string ActionId { get; set; } = string.Empty;
    public string RequestPayloadJson { get; set; } = string.Empty;
    public string RequestPayloadHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Row in dbo.PolicyDecision — one per evaluation.</summary>
public sealed class PolicyDecisionRecord
{
    public Guid PolicyDecisionId { get; set; }
    public Guid ActionRequestId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string PolicyCode { get; set; } = string.Empty;
    public string PolicyVersion { get; set; } = string.Empty;
    public string? DecisionReason { get; set; }
    public string? EvaluationTraceJson { get; set; }
    public DateTimeOffset EvaluatedUtc { get; set; }
}

/// <summary>Row in dbo.ApprovalRequest — opened when a decision is REQUIRE_APPROVAL.</summary>
public sealed class ApprovalRequestRecord
{
    public Guid ApprovalRequestId { get; set; }
    public Guid ActionRequestId { get; set; }
    public string ApprovalType { get; set; } = string.Empty;
    public string? ApproverId { get; set; }
    public string? ApproverRole { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset RequestedUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public DateTimeOffset? RespondedUtc { get; set; }
    public string? ResponseComment { get; set; }

    /// <summary>How many different people must approve before the action runs (1 = a single approver).</summary>
    public int RequiredApprovals { get; set; } = 1;
}

/// <summary>
/// One person's decision on an approval that needs several approvers (dbo.ApprovalDecision). The store keeps at
/// most one per person per approval, so nobody can count twice.
/// </summary>
public sealed class ApprovalDecisionRecord
{
    public Guid ApprovalDecisionId { get; set; }
    public Guid ApprovalRequestId { get; set; }

    /// <summary>The approver as recorded, e.g. "Fatima Hassan &lt;fatima@bank.ae&gt;".</summary>
    public string ApproverId { get; set; } = string.Empty;

    /// <summary>Normalised identity (lower-case e-mail when there is one) that makes "one decision per person" checkable.</summary>
    public string ApproverKey { get; set; } = string.Empty;

    /// <summary>Approved | Rejected.</summary>
    public string Decision { get; set; } = string.Empty;

    public string? Comment { get; set; }
    public DateTimeOffset DecidedUtc { get; set; }
}

/// <summary>Row in dbo.Execution — one attempt to run the action against a target.</summary>
public sealed class ExecutionRecord
{
    public Guid ExecutionId { get; set; }
    public Guid ActionRequestId { get; set; }
    public string AdapterCode { get; set; } = string.Empty;
    public string? TargetResourceId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int AttemptNumber { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>Row in dbo.ActionReceipt — the tamper-evident, hash-chained proof.</summary>
public sealed class ActionReceiptRecord
{
    public Guid ActionReceiptId { get; set; }
    public Guid ActionRequestId { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public string ReceiptJson { get; set; } = string.Empty;
    public string ReceiptHash { get; set; } = string.Empty;
    public string? PreviousReceiptHash { get; set; }
    public string? SignatureValue { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>
    /// Position in the chain (genesis = 0, strictly +1 per receipt, unique). Lets a verifier detect a deleted
    /// receipt between two it holds, and lets the store refuse a fork when two writers race for the head.
    /// </summary>
    public long SequenceNumber { get; set; }

    /// <summary>EXECUTION or DECISION (see <see cref="Domain.ReceiptKind"/>).</summary>
    public string Kind { get; set; } = Domain.ReceiptKind.Execution;
}

/// <summary>The current tail of the receipt chain: the latest hash and its sequence number (null/-1 when empty).</summary>
public readonly record struct ReceiptChainHead(string? Hash, long Sequence)
{
    public static readonly ReceiptChainHead Empty = new(null, -1);
}

/// <summary>
/// Thrown by a store when a receipt insert would fork the chain: another writer appended to the same head
/// first (unique sequence / previous-hash violation). The writer re-reads the head and retries.
/// </summary>
public sealed class ReceiptChainConflictException(string message) : Exception(message);
