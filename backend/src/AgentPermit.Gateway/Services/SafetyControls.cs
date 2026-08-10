using System.Text.Json;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Observations;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Services;

public sealed class CircuitBreakerOptions
{
    /// <summary>Always-on runaway protection. Independent of policy and warm-up.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Max executed (ALLOWed) actions per agent within the window before the breaker trips.</summary>
    public int MaxExecutionsPerWindow { get; set; } = 100;

    public int WindowSeconds { get; set; } = 60;
}

public sealed class KillSwitchOptions
{
    /// <summary>How long the freeze state is cached in-process before re-reading the store. Keeps the check
    /// cheap while bounding how long a freeze/unfreeze takes to propagate across replicas.</summary>
    public int CacheSeconds { get; set; } = 5;
}

/// <summary>Result of a safety check — allowed, or a hard deny with a stable reason.</summary>
public sealed class SafetyResult
{
    public bool Allowed { get; init; }
    public string? ReasonCode { get; init; }
    public string? Reason { get; init; }
    public static readonly SafetyResult Ok = new() { Allowed = true };
}

/// <summary>The current global freeze state.</summary>
public sealed class FreezeState
{
    public bool Frozen { get; set; }
    public string? Reason { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset? SinceUtc { get; set; }
}

/// <summary>
/// Emergency stop. A freeze denies EVERY action request (SYSTEM_FROZEN) until lifted. State is persisted in
/// the config store so it survives restarts and applies fleet-wide; reads are cached for a few seconds so the
/// per-request check is cheap (a freeze/unfreeze propagates within the cache window).
/// </summary>
public sealed class KillSwitchService(IConfigStore config, KillSwitchOptions options)
{
    private const string Type = "killswitch";
    private const string Id = "global";
    private FreezeState _cache = new();
    private DateTimeOffset _cacheUntil = DateTimeOffset.MinValue;
    private readonly object _lock = new();

    public async Task<FreezeState> GetAsync(CancellationToken ct)
    {
        lock (_lock) { if (DateTimeOffset.UtcNow < _cacheUntil) return _cache; }
        var rec = await config.GetAsync(Type, Id, ct);
        var state = rec is null ? new FreezeState()
            : JsonSerializer.Deserialize<FreezeState>(rec.Json) ?? new FreezeState();
        lock (_lock) { _cache = state; _cacheUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, options.CacheSeconds)); }
        return state;
    }

    public async Task SetAsync(bool frozen, string? reason, string? actor, CancellationToken ct)
    {
        var state = new FreezeState { Frozen = frozen, Reason = reason, Actor = actor, SinceUtc = frozen ? DateTimeOffset.UtcNow : null };
        await config.UpsertAsync(Type, Id, JsonSerializer.Serialize(state), ct);
        lock (_lock) { _cache = state; _cacheUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, options.CacheSeconds)); }
    }
}

/// <summary>
/// Velocity circuit breaker: caps how many actions an agent can execute in a short window, using the
/// (cross-replica, accurate) observation store as the counter. Catches a runaway or compromised agent
/// regardless of what policy or warm-up would allow.
/// </summary>
public sealed class CircuitBreaker(IObservationStore observations, CircuitBreakerOptions options)
{
    public async Task<SafetyResult> CheckAsync(string agentId, CancellationToken ct)
    {
        if (!options.Enabled || options.MaxExecutionsPerWindow <= 0) return SafetyResult.Ok;
        var since = DateTimeOffset.UtcNow.AddSeconds(-Math.Max(1, options.WindowSeconds));
        var stats = await observations.GetAgentStatsAsync(agentId, since, ct);
        if (stats.EffectiveAllow < options.MaxExecutionsPerWindow) return SafetyResult.Ok;
        return new SafetyResult
        {
            Allowed = false,
            ReasonCode = ReasonCode.CircuitBreaker,
            Reason = $"Circuit breaker: agent '{agentId}' executed {stats.EffectiveAllow} action(s) in the last " +
                     $"{options.WindowSeconds}s (limit {options.MaxExecutionsPerWindow})."
        };
    }
}

/// <summary>
/// Always-on safety gate checked before policy — the day-one net that does not warm up: global freeze first,
/// then the per-agent velocity breaker. Either trip is a hard deny.
/// </summary>
public sealed class SafetyGate(KillSwitchService killSwitch, CircuitBreaker breaker)
{
    public async Task<SafetyResult> CheckAsync(string agentId, CancellationToken ct)
    {
        var freeze = await killSwitch.GetAsync(ct);
        if (freeze.Frozen)
            return new SafetyResult
            {
                Allowed = false,
                ReasonCode = ReasonCode.SystemFrozen,
                Reason = string.IsNullOrWhiteSpace(freeze.Reason) ? "System is frozen (kill switch engaged)." : freeze.Reason
            };

        return await breaker.CheckAsync(agentId, ct);
    }
}
