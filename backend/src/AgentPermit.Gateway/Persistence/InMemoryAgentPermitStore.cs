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

    /// <summary>Idempotency keys are scoped per agent: the uniqueness key is (agentId, idempotencyKey).</summary>
    private static string IdempotencyScope(string agentId, string idempotencyKey)
        => string.Concat(agentId.ToLowerInvariant(), "\u001f", idempotencyKey);

    public Task<ActionRequestRecord?> FindByIdempotencyKeyAsync(string agentId, string idempotencyKey, CancellationToken ct)
    {
        if (_idempotency.TryGetValue(IdempotencyScope(agentId, idempotencyKey), out var id)
            && _requests.TryGetValue(id, out var rec))
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

    public Task<IReadOnlyList<string?>> ListParameterValuesAsync(AggregateQuery q, CancellationToken ct)
    {
        var actions = new HashSet<string>(q.ActionIds, StringComparer.OrdinalIgnoreCase);
        var statuses = q.Statuses is null ? null : new HashSet<string>(q.Statuses, StringComparer.OrdinalIgnoreCase);
        var values = new List<string?>();
        foreach (var r in _requests.Values)
        {
            if (!actions.Contains(r.ActionId) || r.CreatedUtc < q.Since) continue;
            if (statuses is not null && !statuses.Contains(r.Status)) continue;
            if (q.AgentId is not null && !string.Equals(r.ExecutingAgentId, q.AgentId, StringComparison.OrdinalIgnoreCase)) continue;
            if (q.RootAgentId is not null && !string.Equals(r.RootAgentId ?? string.Empty, q.RootAgentId, StringComparison.OrdinalIgnoreCase)) continue;
            if (q.InitiatingUserId is not null && !string.Equals(r.InitiatingUserId ?? string.Empty, q.InitiatingUserId, StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(r.RequestPayloadJson) ? "{}" : r.RequestPayloadJson);
            var root = doc.RootElement;
            var ok = true;
            foreach (var (name, expected) in q.ParameterEquals)
            {
                if (!string.Equals(JsonText(root, name), expected, StringComparison.OrdinalIgnoreCase)) { ok = false; break; }
            }
            if (!ok) continue;
            values.Add(q.ValueField is null ? null : JsonText(root, q.ValueField));
        }
        return Task.FromResult<IReadOnlyList<string?>>(values);
    }

    /// <summary>Top-level payload property as text (strings raw, numbers/bools as their JSON text), or null.</summary>
    private static string? JsonText(System.Text.Json.JsonElement root, string name)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => v.GetString(),
            System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
            _ => v.GetRawText()
        };
    }

    public Task InsertActionRequestAsync(ActionRequestRecord record, CancellationToken ct)
    {
        // Enforce the unique (agent, idempotency key) pair like the SQL UNIQUE constraint would.
        if (!_idempotency.TryAdd(IdempotencyScope(record.ExecutingAgentId, record.IdempotencyKey), record.ActionRequestId))
            throw new InvalidOperationException(
                $"Duplicate idempotency key '{record.IdempotencyKey}' for agent '{record.ExecutingAgentId}'.");
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

    private long _latestSequence = -1;

    public Task<ReceiptChainHead> GetReceiptChainHeadAsync(CancellationToken ct)
    {
        lock (_chainLock) return Task.FromResult(new ReceiptChainHead(_latestReceiptHash, _latestSequence));
    }

    public Task<ActionReceiptRecord?> GetReceiptForRequestAsync(Guid actionRequestId, CancellationToken ct)
    {
        // Prefer the EXECUTION receipt; otherwise the latest DECISION receipt.
        var match = _receipts.Values
            .Where(r => r.ActionRequestId == actionRequestId)
            .OrderBy(r => r.Kind == Domain.ReceiptKind.Execution ? 0 : 1)
            .ThenByDescending(r => r.SequenceNumber)
            .FirstOrDefault();
        return Task.FromResult(match);
    }

    public Task<ActionReceiptRecord?> GetReceiptBySequenceAsync(long sequenceNumber, CancellationToken ct)
        => Task.FromResult(_receipts.Values.FirstOrDefault(r => r.SequenceNumber == sequenceNumber));

    public Task<IReadOnlyList<ActionReceiptRecord>> ListRecentReceiptsAsync(int take, CancellationToken ct)
    {
        IReadOnlyList<ActionReceiptRecord> recent = _receipts.Values
            .OrderByDescending(r => r.SequenceNumber)
            .Take(take <= 0 ? 50 : take)
            .ToList();
        return Task.FromResult(recent);
    }

    public Task InsertReceiptAsync(ActionReceiptRecord record, CancellationToken ct)
    {
        lock (_chainLock)
        {
            // Mirror the SQL unique constraints: the record must extend the CURRENT head, or it is a fork.
            if (!string.Equals(record.PreviousReceiptHash, _latestReceiptHash, StringComparison.OrdinalIgnoreCase)
                || record.SequenceNumber != _latestSequence + 1)
                throw new ReceiptChainConflictException(
                    $"Receipt chain conflict: expected previous hash '{_latestReceiptHash}' / sequence {_latestSequence + 1}, " +
                    $"got '{record.PreviousReceiptHash}' / {record.SequenceNumber}.");
            _receipts[record.ActionReceiptId] = record;
            _latestReceiptHash = record.ReceiptHash;
            _latestSequence = record.SequenceNumber;
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
