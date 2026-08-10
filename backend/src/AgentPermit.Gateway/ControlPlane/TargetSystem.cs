using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace AgentPermit.Gateway.ControlPlane;

/// <summary>A registered downstream system AgentPermit can execute actions against.</summary>
public sealed class TargetSystemRecord
{
    [JsonPropertyName("targetSystemCode")] public string TargetSystemCode { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("adapterType")] public string AdapterType { get; set; } = string.Empty;
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
    [JsonPropertyName("executePath")] public string? ExecutePath { get; set; }
    [JsonPropertyName("authMode")] public string? AuthMode { get; set; }
    [JsonPropertyName("credentialRef")] public string? CredentialRef { get; set; }
    [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; set; } = 30;
    [JsonPropertyName("maxRetries")] public int MaxRetries { get; set; } = 2;
    [JsonPropertyName("allowedActions")] public string[] AllowedActions { get; set; } = Array.Empty<string>();
    [JsonPropertyName("environment")] public string? Environment { get; set; }
    [JsonPropertyName("dataResidency")] public string? DataResidency { get; set; }
    [JsonPropertyName("operationalOwner")] public string? OperationalOwner { get; set; }

    /// <summary>
    /// Consequence class of this target (LOW | MEDIUM | HIGH | CRITICAL) — how much damage an action against
    /// it can do. Assigned by the risk classifier at registration when not set explicitly. An action's
    /// effective consequence is the severest of its own tier and its target's, so a target system of record
    /// for money/PII pulls its actions up to HIGH regardless of how the action declared itself.
    /// </summary>
    [JsonPropertyName("consequenceTier")] public string? ConsequenceTier { get; set; }

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("updatedUtc")] public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Persistence for target systems (InMemory for local, Azure SQL in production).</summary>
public interface ITargetSystemStore
{
    Task<IReadOnlyList<TargetSystemRecord>> ListAsync(CancellationToken ct);
    Task<TargetSystemRecord?> GetAsync(string code, CancellationToken ct);
    Task UpsertAsync(TargetSystemRecord record, CancellationToken ct);
    Task<bool> DeleteAsync(string code, CancellationToken ct);
}

public sealed class InMemoryTargetSystemStore : ITargetSystemStore
{
    private readonly ConcurrentDictionary<string, TargetSystemRecord> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<TargetSystemRecord>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TargetSystemRecord>>(_items.Values.OrderBy(t => t.TargetSystemCode).ToList());

    public Task<TargetSystemRecord?> GetAsync(string code, CancellationToken ct)
        => Task.FromResult(_items.TryGetValue(code, out var r) ? r : null);

    public Task UpsertAsync(TargetSystemRecord record, CancellationToken ct)
    {
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        _items[record.TargetSystemCode] = record;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string code, CancellationToken ct)
        => Task.FromResult(_items.TryRemove(code, out _));
}

/// <summary>
/// Cached read-through registry over the store. The gateway resolves a target here at execution
/// time (which adapter, endpoint, timeout, retries). Writes go through the store + Invalidate().
/// </summary>
public interface ITargetSystemRegistry
{
    TargetSystemRecord? Find(string? code);
    IReadOnlyList<TargetSystemRecord> All();
    void Invalidate();
}

public sealed class CachedTargetSystemRegistry : ITargetSystemRegistry
{
    private readonly ITargetSystemStore _store;
    private readonly ILogger<CachedTargetSystemRegistry> _logger;
    private readonly object _lock = new();
    private Dictionary<string, TargetSystemRecord> _cache;

    public CachedTargetSystemRegistry(ITargetSystemStore store, ILogger<CachedTargetSystemRegistry> logger)
    {
        _store = store;
        _logger = logger;
        _cache = Load();
    }

    private Dictionary<string, TargetSystemRecord> Load()
    {
        try
        {
            return _store.ListAsync(CancellationToken.None).GetAwaiter().GetResult()
                .Where(t => t.Enabled)
                .ToDictionary(t => t.TargetSystemCode, t => t, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Never fail startup on a target-store read; the runtime falls back to target-code == adapter-code.
            _logger.LogWarning(ex, "Could not load target systems; using an empty registry until refreshed.");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public TargetSystemRecord? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        lock (_lock) return _cache.TryGetValue(code, out var r) ? r : null;
    }

    public IReadOnlyList<TargetSystemRecord> All()
    {
        lock (_lock) return _cache.Values.ToList();
    }

    public void Invalidate()
    {
        var fresh = Load();
        lock (_lock) _cache = fresh;
    }
}
