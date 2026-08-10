using System.Collections.Concurrent;

namespace AgentPermit.Gateway.Registry;

/// <summary>Persistence for registered agents (InMemory for local, Azure SQL in production).</summary>
public interface IAgentStore
{
    Task<IReadOnlyList<AgentInfo>> ListAsync(CancellationToken ct);
    Task<AgentInfo?> GetAsync(string agentId, CancellationToken ct);
    Task UpsertAsync(AgentInfo agent, CancellationToken ct);
    Task<bool> DeleteAsync(string agentId, CancellationToken ct);
}

public sealed class InMemoryAgentStore : IAgentStore
{
    private readonly ConcurrentDictionary<string, AgentInfo> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<AgentInfo>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentInfo>>(_items.Values.OrderBy(a => a.AgentId).ToList());

    public Task<AgentInfo?> GetAsync(string agentId, CancellationToken ct)
        => Task.FromResult(_items.TryGetValue(agentId, out var a) ? a : null);

    public Task UpsertAsync(AgentInfo agent, CancellationToken ct)
    {
        agent.UpdatedUtc = DateTimeOffset.UtcNow;
        _items[agent.AgentId] = agent;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string agentId, CancellationToken ct)
        => Task.FromResult(_items.TryRemove(agentId, out _));
}

/// <summary>
/// Cached read-through registry over <see cref="IAgentStore"/>. The gateway resolves an agent here on
/// every request (identity binding: authenticated Entra client id → registered agent). Writes go
/// through the store then call <see cref="Invalidate"/>. Mirrors the Target System Registry.
/// </summary>
public sealed class CachedAgentRegistry : IAgentRegistry
{
    private readonly IAgentStore _store;
    private readonly ILogger<CachedAgentRegistry> _logger;
    private readonly object _lock = new();
    private Snapshot _cache;

    public CachedAgentRegistry(IAgentStore store, ILogger<CachedAgentRegistry> logger)
    {
        _store = store;
        _logger = logger;
        _cache = Load();
    }

    private Snapshot Load()
    {
        try
        {
            var all = _store.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
            var byId = new Dictionary<string, AgentInfo>(StringComparer.OrdinalIgnoreCase);
            var byClientId = new Dictionary<string, AgentInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in all.Where(a => a.Enabled))
            {
                byId[a.AgentId] = a;
                if (string.IsNullOrWhiteSpace(a.EntraClientId)) continue;
                // Two agents must never share a bound identity; dedupe defensively so a bad import can
                // never empty the whole registry (which would deny every agent).
                if (byClientId.TryGetValue(a.EntraClientId!, out var other))
                    _logger.LogWarning("Entra client id {ClientId} bound to multiple agents ({A}, {B}); keeping the first.",
                        a.EntraClientId, other.AgentId, a.AgentId);
                else
                    byClientId[a.EntraClientId!] = a;
            }
            return new Snapshot(all, byId, byClientId);
        }
        catch (Exception ex)
        {
            // Never fail startup on an agent-store read; requests will simply not resolve until refreshed.
            _logger.LogWarning(ex, "Could not load agents; using an empty registry until refreshed.");
            return new Snapshot(Array.Empty<AgentInfo>(),
                new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        }
    }

    public AgentInfo? Find(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        lock (_lock) return _cache.ById.TryGetValue(agentId, out var a) ? a : null;
    }

    public AgentInfo? FindByEntraClientId(string? entraClientId)
    {
        if (string.IsNullOrWhiteSpace(entraClientId)) return null;
        lock (_lock) return _cache.ByClientId.TryGetValue(entraClientId, out var a) ? a : null;
    }

    public IReadOnlyList<AgentInfo> All()
    {
        lock (_lock) return _cache.All;
    }

    public void Invalidate()
    {
        var fresh = Load();
        lock (_lock) _cache = fresh;
    }

    private sealed record Snapshot(
        IReadOnlyList<AgentInfo> All,
        Dictionary<string, AgentInfo> ById,
        Dictionary<string, AgentInfo> ByClientId);
}
