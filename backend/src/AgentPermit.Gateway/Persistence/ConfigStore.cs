using System.Collections.Concurrent;

namespace AgentPermit.Gateway.Persistence;

/// <summary>A control-plane configuration record stored as a JSON document, keyed by type + id.</summary>
public sealed class ConfigEntityRecord
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Json { get; set; } = "{}";
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>
/// Generic document store for control-plane configuration (environments, role assignments, policy
/// assignments, approval matrices, platform settings, retention). One table serves every config
/// module; each record is the authored JSON object. InMemory for local, Azure SQL in production.
/// </summary>
public interface IConfigStore
{
    Task<IReadOnlyList<ConfigEntityRecord>> ListAsync(string type, CancellationToken ct);
    Task<ConfigEntityRecord?> GetAsync(string type, string id, CancellationToken ct);
    Task UpsertAsync(string type, string id, string json, CancellationToken ct);
    Task<bool> DeleteAsync(string type, string id, CancellationToken ct);
}

public sealed class InMemoryConfigStore : IConfigStore
{
    private readonly ConcurrentDictionary<(string, string), ConfigEntityRecord> _items = new();

    public Task<IReadOnlyList<ConfigEntityRecord>> ListAsync(string type, CancellationToken ct)
    {
        IReadOnlyList<ConfigEntityRecord> rows = _items.Values
            .Where(r => string.Equals(r.EntityType, type, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.EntityId).ToList();
        return Task.FromResult(rows);
    }

    public Task<ConfigEntityRecord?> GetAsync(string type, string id, CancellationToken ct)
        => Task.FromResult(_items.TryGetValue((type, id), out var r) ? r : null);

    public Task UpsertAsync(string type, string id, string json, CancellationToken ct)
    {
        _items[(type, id)] = new ConfigEntityRecord
        {
            EntityType = type, EntityId = id, Json = json, UpdatedUtc = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string type, string id, CancellationToken ct)
        => Task.FromResult(_items.TryRemove((type, id), out _));
}
