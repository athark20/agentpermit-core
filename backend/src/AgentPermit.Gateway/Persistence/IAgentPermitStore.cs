namespace AgentPermit.Gateway.Persistence;

/// <summary>
/// Persistence boundary for the gateway. Two implementations exist:
/// <see cref="InMemoryAgentPermitStore"/> for local smoke tests and
/// <see cref="SqlAgentPermitStore"/> for Azure SQL (dbo.* runtime schema).
/// Keeping the boundary narrow lets the vertical slice run end-to-end locally
/// without any database or secrets.
/// </summary>
public interface IAgentPermitStore
{
    /// <summary>Idempotency lookup: returns the prior request for a key, or null.</summary>
    Task<ActionRequestRecord?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);

    Task<ActionRequestRecord?> GetActionRequestAsync(Guid actionRequestId, CancellationToken ct);

    /// <summary>Most recent action requests (newest first) for the admin Runtime view.</summary>
    Task<IReadOnlyList<ActionRequestRecord>> ListRecentActionRequestsAsync(int take, CancellationToken ct);

    /// <summary>Aggregate request counts for the admin dashboard.</summary>
    Task<PlatformMetrics> GetMetricsAsync(CancellationToken ct);

    /// <summary>Per-agent activity summary since <paramref name="since"/> — the centralized fleet trail view.</summary>
    Task<IReadOnlyList<AgentTrailRow>> GetAgentTrailSummaryAsync(DateTimeOffset since, CancellationToken ct);

    Task InsertActionRequestAsync(ActionRequestRecord record, CancellationToken ct);

    Task UpdateActionRequestStatusAsync(Guid actionRequestId, string status, CancellationToken ct);

    Task InsertPolicyDecisionAsync(PolicyDecisionRecord record, CancellationToken ct);

    /// <summary>Most recent policy decision for a request (used to rebuild replay responses).</summary>
    Task<PolicyDecisionRecord?> GetLatestPolicyDecisionAsync(Guid actionRequestId, CancellationToken ct);

    Task InsertApprovalRequestAsync(ApprovalRequestRecord record, CancellationToken ct);

    Task<ApprovalRequestRecord?> GetApprovalAsync(Guid approvalRequestId, CancellationToken ct);

    /// <summary>All approvals still awaiting a decision, oldest first.</summary>
    Task<IReadOnlyList<ApprovalRequestRecord>> GetPendingApprovalsAsync(CancellationToken ct);

    Task UpdateApprovalAsync(ApprovalRequestRecord record, CancellationToken ct);

    /// <summary>Succeeded execution for a request, if any (duplicate-execution guard).</summary>
    Task<ExecutionRecord?> FindSucceededExecutionAsync(Guid actionRequestId, CancellationToken ct);

    /// <summary>Most recent executions (newest first) for the admin Runtime view.</summary>
    Task<IReadOnlyList<ExecutionRecord>> ListRecentExecutionsAsync(int take, CancellationToken ct);

    Task InsertExecutionAsync(ExecutionRecord record, CancellationToken ct);

    Task UpdateExecutionAsync(ExecutionRecord record, CancellationToken ct);

    /// <summary>Hash of the most recently written receipt (for chaining), or null.</summary>
    Task<string?> GetLatestReceiptHashAsync(CancellationToken ct);

    Task<ActionReceiptRecord?> GetReceiptForRequestAsync(Guid actionRequestId, CancellationToken ct);

    /// <summary>Most recent receipts (newest first) for the admin evidence view.</summary>
    Task<IReadOnlyList<ActionReceiptRecord>> ListRecentReceiptsAsync(int take, CancellationToken ct);

    Task InsertReceiptAsync(ActionReceiptRecord record, CancellationToken ct);
}
