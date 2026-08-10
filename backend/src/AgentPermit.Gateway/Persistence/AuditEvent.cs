using System.Collections.Concurrent;

namespace AgentPermit.Gateway.Persistence;

/// <summary>An immutable record of an administrative change — who did what, when and why.</summary>
public sealed class AuditEventRecord
{
    public Guid AuditEventId { get; set; }
    public DateTimeOffset OccurredUtc { get; set; }
    public string Actor { get; set; } = string.Empty;       // display name / upn
    public string? ActorId { get; set; }                    // Entra object id (oid)
    public string Action { get; set; } = string.Empty;      // e.g. policy.publish, target.upsert
    public string TargetType { get; set; } = string.Empty;  // Policy | TargetSystem | Agent | Approval | ...
    public string? TargetId { get; set; }
    public string? Summary { get; set; }
}

/// <summary>Append-only audit trail (InMemory for local, Azure SQL in production).</summary>
public interface IAuditStore
{
    Task AppendAsync(AuditEventRecord e, CancellationToken ct);
    Task<IReadOnlyList<AuditEventRecord>> ListRecentAsync(int take, CancellationToken ct);
}

public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly ConcurrentQueue<AuditEventRecord> _events = new();

    public Task AppendAsync(AuditEventRecord e, CancellationToken ct)
    {
        _events.Enqueue(e);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEventRecord>> ListRecentAsync(int take, CancellationToken ct)
    {
        IReadOnlyList<AuditEventRecord> recent = _events
            .OrderByDescending(e => e.OccurredUtc).Take(take <= 0 ? 100 : take).ToList();
        return Task.FromResult(recent);
    }
}
