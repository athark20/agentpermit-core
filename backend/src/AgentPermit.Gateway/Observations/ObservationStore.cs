using System.Collections.Concurrent;

namespace AgentPermit.Gateway.Observations;

/// <summary>
/// One observed governed action — the append-only evidence trail behind rollout decisions. Today the only
/// source is the gateway's own decisions (Feed C: every request is scored, and during warm-up we record
/// what we WOULD have blocked). Later, passive feeds (Purview audit, Power Automate run history) write
/// observations from the same shape so "what an agent actually does" is one queryable store.
/// </summary>
public sealed class ObservationRecord
{
    public Guid ObservationId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredUtc { get; init; }
    public string Source { get; init; } = ObservationSource.Gateway;
    public string AgentId { get; init; } = string.Empty;
    public string ActionId { get; init; } = string.Empty;
    public string? TargetSystem { get; init; }
    public string ComputedDecision { get; init; } = string.Empty;
    public string EffectiveDecision { get; init; } = string.Empty;
    public string EnforcementMode { get; init; } = string.Empty;
    public string ConsequenceTier { get; init; } = string.Empty;

    /// <summary>True when warm-up let through something policy would have blocked/stepped-up — the signal
    /// that decides whether an agent is safe to promote.</summary>
    public bool WouldHaveBlocked { get; init; }

    public string? CorrelationId { get; init; }
}

public static class ObservationSource
{
    public const string Gateway = "GATEWAY";
    public const string PurviewAudit = "PURVIEW_AUDIT";
    public const string FlowRun = "FLOW_RUN";
}

/// <summary>Aggregated warm-up evidence for one agent over a window — feeds the rollout recommendation.</summary>
public sealed class AgentObservationStats
{
    public required string AgentId { get; init; }
    public required int Total { get; init; }
    public required int WouldHaveBlocked { get; init; }
    public required int EffectiveAllow { get; init; }
    public required int EffectiveDeny { get; init; }
    public required int EffectiveApproval { get; init; }
    public DateTimeOffset? FirstObservedUtc { get; init; }
    public DateTimeOffset? LastObservedUtc { get; init; }
}

public interface IObservationStore
{
    Task AppendAsync(ObservationRecord observation, CancellationToken ct);
    Task<AgentObservationStats> GetAgentStatsAsync(string agentId, DateTimeOffset since, CancellationToken ct);
    Task<IReadOnlyList<ObservationRecord>> ListForAgentAsync(string agentId, int limit, CancellationToken ct);
}

/// <summary>In-memory observation store for local/dev/tests.</summary>
public sealed class InMemoryObservationStore : IObservationStore
{
    private readonly ConcurrentQueue<ObservationRecord> _items = new();

    public Task AppendAsync(ObservationRecord observation, CancellationToken ct)
    {
        _items.Enqueue(observation);
        return Task.CompletedTask;
    }

    public Task<AgentObservationStats> GetAgentStatsAsync(string agentId, DateTimeOffset since, CancellationToken ct)
    {
        var rows = _items.Where(o =>
            string.Equals(o.AgentId, agentId, StringComparison.OrdinalIgnoreCase) && o.OccurredUtc >= since).ToList();
        return Task.FromResult(new AgentObservationStats
        {
            AgentId = agentId,
            Total = rows.Count,
            WouldHaveBlocked = rows.Count(o => o.WouldHaveBlocked),
            EffectiveAllow = rows.Count(o => o.EffectiveDecision == "ALLOW"),
            EffectiveDeny = rows.Count(o => o.EffectiveDecision == "DENY"),
            EffectiveApproval = rows.Count(o => o.EffectiveDecision == "REQUIRE_APPROVAL"),
            FirstObservedUtc = rows.Count == 0 ? null : rows.Min(o => o.OccurredUtc),
            LastObservedUtc = rows.Count == 0 ? null : rows.Max(o => o.OccurredUtc)
        });
    }

    public Task<IReadOnlyList<ObservationRecord>> ListForAgentAsync(string agentId, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ObservationRecord>>(_items
            .Where(o => string.Equals(o.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.OccurredUtc).Take(limit).ToList());
}
