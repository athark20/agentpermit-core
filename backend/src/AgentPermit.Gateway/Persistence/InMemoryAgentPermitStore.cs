using System.Collections.Concurrent;

namespace AgentPermit.Gateway.Persistence;

/// <summary>
/// Thread-safe in-memory store used for local runs and smoke tests. Registered
/// as a singleton so state survives across requests within a process.
/// </summary>
public sealed class InMemoryAgentPermitStore : IAgentPermitStore
{
    private readonly ConcurrentDictionary<Guid, ActionRequestRecord> _requests = new();
    private readonly ConcurrentDictionary<string, Guid> _idempotency = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, PolicyDecisionRecord> _decisions = new();
    private readonly ConcurrentDictionary<Guid, ApprovalRequestRecord> _approvals = new();
    private readonly ConcurrentDictionary<Guid, ExecutionRecord> _executions = new();
    private readonly ConcurrentDictionary<Guid, ActionReceiptRecord> _receipts = new();
    private readonly object _chainLock = new();
    private string? _latestReceiptHash;

    public Task<ActionRequestRecord?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct)
    {
        if (_idempotency.TryGetValue(idempotencyKey, out var id) && _requests.TryGetValue(id, out var rec))
            return Task.FromResult<ActionRequestRecord?>(Clone(rec));
        return Task.FromResult<ActionRequestRecord?>(null);
    }

    public Task<ActionRequestRecord?> GetActionRequestAsync(Guid actionRequestId, CancellationToken ct)
        => Task.FromResult(_requests.TryGetValue(actionRequestId, out var r) ? Clone(r) : null);

    public Task<IReadOnlyList<ActionRequestRecord>> ListRecentActionRequestsAsync(int take, CancellationToken ct)
    {
        IReadOnlyList<ActionRequestRecord> recent = _requests.Values
            .OrderByDescending(r => r.CreatedUtc).Take(take <= 0 ? 100 : take).Select(Clone).ToList();
        return Task.FromResult(recent);
    }

    public Task<PlatformMetrics> GetMetricsAsync(CancellationToken ct)
    {
        var all = _requests.Values.ToList();
        var today = DateTimeOffset.UtcNow.Date;
        static IEnumerable<(string, int)> Group(IEnumerable<ActionRequestRecord> rows)
            => rows.GroupBy(r => r.Status).Select(g => (g.Key, g.Count()));
        return Task.FromResult(MetricsBuilder.Build(
            Group(all), Group(all.Where(r => r.CreatedUtc.UtcDateTime.Date == today))));
    }

    public Task<IReadOnlyList<AgentTrailRow>> GetAgentTrailSummaryAsync(DateTimeOffset since, CancellationToken ct)
    {
        IReadOnlyList<AgentTrailRow> rows = _requests.Values
            .Where(r => r.CreatedUtc >= since)
            .GroupBy(r => r.ExecutingAgentId)
            .Select(g => new AgentTrailRow
            {
                AgentId = g.Key,
                Total = g.Count(),
                Allowed = g.Count(r => r.Status is "SUCCEEDED" or "EXECUTED" or "ALLOW"),
                StepUp = g.Count(r => r.Status is "AWAITING_APPROVAL" or "PENDINGAPPROVAL"),
                Denied = g.Count(r => r.Status is "DENIED" or "REJECTED"),
                Failed = g.Count(r => r.Status == "FAILED"),
                LastSeenUtc = g.Max(r => r.CreatedUtc),
            })
            .OrderByDescending(x => x.Total).ToList();
        return Task.FromResult(rows);
    }

    public Task InsertActionRequestAsync(ActionRequestRecord record, CancellationToken ct)
    {
        // Enforce the unique idempotency key like the SQL UNIQUE constraint would.
        if (!_idempotency.TryAdd(record.IdempotencyKey, record.ActionRequestId))
            throw new InvalidOperationException($"Duplicate idempotency key '{record.IdempotencyKey}'.");
        _requests[record.ActionRequestId] = Clone(record);
        return Task.CompletedTask;
    }

    public Task UpdateActionRequestStatusAsync(Guid actionRequestId, string status, CancellationToken ct)
    {
        if (_requests.TryGetValue(actionRequestId, out var r))
        {
            r.Status = status;
            r.UpdatedUtc = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task InsertPolicyDecisionAsync(PolicyDecisionRecord record, CancellationToken ct)
    {
        _decisions[record.PolicyDecisionId] = record;
        return Task.CompletedTask;
    }

    public Task<PolicyDecisionRecord?> GetLatestPolicyDecisionAsync(Guid actionRequestId, CancellationToken ct)
    {
        var match = _decisions.Values
            .Where(d => d.ActionRequestId == actionRequestId)
            .OrderByDescending(d => d.EvaluatedUtc)
            .FirstOrDefault();
        return Task.FromResult(match);
    }

    public Task InsertApprovalRequestAsync(ApprovalRequestRecord record, CancellationToken ct)
    {
        _approvals[record.ApprovalRequestId] = record;
        return Task.CompletedTask;
    }

    public Task<ApprovalRequestRecord?> GetApprovalAsync(Guid approvalRequestId, CancellationToken ct)
        => Task.FromResult(_approvals.TryGetValue(approvalRequestId, out var a) ? a : null);

    public Task<IReadOnlyList<ApprovalRequestRecord>> GetPendingApprovalsAsync(CancellationToken ct)
    {
        IReadOnlyList<ApprovalRequestRecord> pending = _approvals.Values
            .Where(a => a.Status == Domain.ApprovalStatus.Pending)
            .OrderBy(a => a.RequestedUtc)
            .ToList();
        return Task.FromResult(pending);
    }

    public Task UpdateApprovalAsync(ApprovalRequestRecord record, CancellationToken ct)
    {
        _approvals[record.ApprovalRequestId] = record;
        return Task.CompletedTask;
    }

    public Task<ExecutionRecord?> FindSucceededExecutionAsync(Guid actionRequestId, CancellationToken ct)
    {
        var match = _executions.Values.FirstOrDefault(
            e => e.ActionRequestId == actionRequestId && e.Status == Domain.ExecutionStatus.Succeeded);
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<ExecutionRecord>> ListRecentExecutionsAsync(int take, CancellationToken ct)
    {
        IReadOnlyList<ExecutionRecord> recent = _executions.Values
            .OrderByDescending(e => e.StartedUtc).Take(take <= 0 ? 100 : take).ToList();
        return Task.FromResult(recent);
    }

    public Task InsertExecutionAsync(ExecutionRecord record, CancellationToken ct)
    {
        _executions[record.ExecutionId] = record;
        return Task.CompletedTask;
    }

    public Task UpdateExecutionAsync(ExecutionRecord record, CancellationToken ct)
    {
        _executions[record.ExecutionId] = record;
        return Task.CompletedTask;
    }

    public Task<string?> GetLatestReceiptHashAsync(CancellationToken ct)
    {
        lock (_chainLock) return Task.FromResult(_latestReceiptHash);
    }

    public Task<ActionReceiptRecord?> GetReceiptForRequestAsync(Guid actionRequestId, CancellationToken ct)
    {
        var match = _receipts.Values
            .Where(r => r.ActionRequestId == actionRequestId)
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefault();
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<ActionReceiptRecord>> ListRecentReceiptsAsync(int take, CancellationToken ct)
    {
        IReadOnlyList<ActionReceiptRecord> recent = _receipts.Values
            .OrderByDescending(r => r.CreatedUtc)
            .Take(take <= 0 ? 50 : take)
            .ToList();
        return Task.FromResult(recent);
    }

    public Task InsertReceiptAsync(ActionReceiptRecord record, CancellationToken ct)
    {
        lock (_chainLock)
        {
            _receipts[record.ActionReceiptId] = record;
            _latestReceiptHash = record.ReceiptHash;
        }
        return Task.CompletedTask;
    }

    private static ActionRequestRecord Clone(ActionRequestRecord r) => new()
    {
        ActionRequestId = r.ActionRequestId,
        CorrelationId = r.CorrelationId,
        IdempotencyKey = r.IdempotencyKey,
        RootAgentId = r.RootAgentId,
        ExecutingAgentId = r.ExecutingAgentId,
        InitiatingUserId = r.InitiatingUserId,
        ActionId = r.ActionId,
        RequestPayloadJson = r.RequestPayloadJson,
        RequestPayloadHash = r.RequestPayloadHash,
        Status = r.Status,
        CreatedUtc = r.CreatedUtc,
        UpdatedUtc = r.UpdatedUtc
    };
}
