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

/// <summary>
/// Deny-velocity breaker. The execution breaker catches an agent that is doing too much; this one catches an
/// agent that is being told "no" too often — the signature of an agent probing a block (identity mismatches,
/// unknown actions, schema failures, idempotency misuse, repeated policy denials). The 2026 incident record is
/// full of agents that treated a refusal as a puzzle; a healthy business agent is rarely denied at all.
/// </summary>
public sealed class DenyBreakerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Denials per agent within the window before the breaker trips.</summary>
    public int MaxDenialsPerWindow { get; set; } = 25;

    public int WindowSeconds { get; set; } = 60;

    /// <summary>When true (default) a trip quarantines the agent until an admin lifts it; when false the
    /// trip only denies the current request (the agent can resume once the window rolls over).</summary>
    public bool AutoQuarantine { get; set; } = true;
}

/// <summary>Per-agent quarantine: a persisted hard deny that survives restarts and applies fleet-wide.</summary>
public sealed class QuarantineState
{
    public string AgentId { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset SinceUtc { get; set; }
    /// <summary>How many denials the agent had accumulated when quarantined automatically (null if manual).</summary>
    public int? DenialCount { get; set; }
}

/// <summary>
/// Quarantine store. State lives in the config store (type <c>quarantine</c>, id = agentId) and is cached in
/// process for a few seconds like the kill switch, so the per-request check is cheap and a lift propagates
/// across replicas within the cache window.
/// </summary>
public sealed class QuarantineService(IConfigStore config, KillSwitchOptions options)
{
    private const string Type = "quarantine";
    private readonly object _lock = new();
    private Dictionary<string, QuarantineState> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _cacheUntil = DateTimeOffset.MinValue;

    public async Task<QuarantineState?> GetAsync(string agentId, CancellationToken ct)
    {
        var all = await ListAsync(ct);
        return all.FirstOrDefault(q => string.Equals(q.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<QuarantineState>> ListAsync(CancellationToken ct)
    {
        lock (_lock) { if (DateTimeOffset.UtcNow < _cacheUntil) return _cache.Values.ToList(); }
        var rows = await config.ListAsync(Type, ct);
        var fresh = new Dictionary<string, QuarantineState>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var s = JsonSerializer.Deserialize<QuarantineState>(r.Json);
            if (s is not null) fresh[string.IsNullOrEmpty(s.AgentId) ? r.EntityId : s.AgentId] = s;
        }
        lock (_lock) { _cache = fresh; _cacheUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, options.CacheSeconds)); }
        return fresh.Values.ToList();
    }

    public async Task<QuarantineState> QuarantineAsync(string agentId, string? reason, string? actor, int? denialCount, CancellationToken ct)
    {
        var state = new QuarantineState
        {
            AgentId = agentId, Reason = reason, Actor = actor, SinceUtc = DateTimeOffset.UtcNow, DenialCount = denialCount
        };
        await config.UpsertAsync(Type, agentId, JsonSerializer.Serialize(state), ct);
        lock (_lock) { _cache[agentId] = state; _cacheUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, options.CacheSeconds)); }
        return state;
    }

    /// <summary>
    /// Lift a quarantine. Also stamps a lift marker: the deny-velocity breaker only counts denials AFTER the
    /// last lift, so an admin's "lift" is a real reset rather than an immediate re-trip on the same denials.
    /// </summary>
    public async Task<bool> LiftAsync(string agentId, CancellationToken ct)
    {
        var removed = await config.DeleteAsync(Type, agentId, ct);
        if (removed)
        {
            var liftedUtc = DateTimeOffset.UtcNow;
            await config.UpsertAsync(LiftType, agentId, JsonSerializer.Serialize(new LiftMarker { AgentId = agentId, LiftedUtc = liftedUtc }), ct);
            lock (_lock) { _lifts[agentId] = liftedUtc; }
        }
        lock (_lock) { _cache.Remove(agentId); _cacheUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, options.CacheSeconds)); }
        return removed;
    }

    /// <summary>When this agent's quarantine was last lifted, or null if never. Cached per agent.</summary>
    public async Task<DateTimeOffset?> GetLastLiftUtcAsync(string agentId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_lifts.TryGetValue(agentId, out var cached)) return cached;
        }
        var rec = await config.GetAsync(LiftType, agentId, ct);
        var marker = rec is null ? null : JsonSerializer.Deserialize<LiftMarker>(rec.Json);
        var value = marker?.LiftedUtc;
        lock (_lock) { _lifts[agentId] = value; }
        return value;
    }

    private const string LiftType = "quarantine-lift";
    private readonly Dictionary<string, DateTimeOffset?> _lifts = new(StringComparer.OrdinalIgnoreCase);

    private sealed class LiftMarker
    {
        public string AgentId { get; set; } = string.Empty;
        public DateTimeOffset LiftedUtc { get; set; }
    }
}

/// <summary>
/// Counts effective denials per agent over a short window (observation store = cross-replica, accurate) and
/// trips when the agent is being refused too often; with auto-quarantine on, the trip is sticky until lifted.
/// </summary>
public sealed class DenyVelocityBreaker(
    IObservationStore observations, QuarantineService quarantine, DenyBreakerOptions options,
    ILogger<DenyVelocityBreaker> logger)
{
    public async Task<SafetyResult> CheckAsync(string agentId, CancellationToken ct)
    {
        if (!options.Enabled || options.MaxDenialsPerWindow <= 0) return SafetyResult.Ok;
        var since = DateTimeOffset.UtcNow.AddSeconds(-Math.Max(1, options.WindowSeconds));
        // An admin lift resets the clock: denials before the lift were reviewed and don't count again.
        var lifted = await quarantine.GetLastLiftUtcAsync(agentId, ct);
        if (lifted is { } l && l > since) since = l;
        var denials = await observations.CountDenialsAsync(agentId, since, ct);
        if (denials < options.MaxDenialsPerWindow) return SafetyResult.Ok;

        var reason = $"Deny-velocity breaker: agent '{agentId}' was denied {denials} time(s) in the last " +
                     $"{options.WindowSeconds}s (limit {options.MaxDenialsPerWindow}).";
        if (options.AutoQuarantine)
        {
            logger.LogWarning("QUARANTINE {Agent}: {Reason}", agentId, reason);
            await quarantine.QuarantineAsync(agentId, reason, "gateway:deny-velocity-breaker", denials, ct);
            return new SafetyResult { Allowed = false, ReasonCode = ReasonCode.AgentQuarantined, Reason = reason + " Agent quarantined until an admin lifts it." };
        }
        logger.LogWarning("Deny-velocity breaker tripped for {Agent} (no quarantine): {Reason}", agentId, reason);
        return new SafetyResult { Allowed = false, ReasonCode = ReasonCode.CircuitBreaker, Reason = reason };
    }

    /// <summary>
    /// Record a safety-layer denial for a resolved agent so it counts toward the breaker. Best-effort: a store
    /// failure must never fail (or change) the governed request, so it is logged and swallowed.
    /// </summary>
    public async Task RecordDenialAsync(string agentId, string actionId, string reasonCode, Guid correlationId, CancellationToken ct)
    {
        try
        {
            await observations.AppendAsync(new ObservationRecord
            {
                OccurredUtc = DateTimeOffset.UtcNow,
                Source = ObservationSource.GatewaySafety,
                AgentId = agentId,
                ActionId = string.IsNullOrWhiteSpace(actionId) ? "(unknown)" : actionId,
                ComputedDecision = Decision.Deny,
                EffectiveDecision = Decision.Deny,
                EnforcementMode = EnforcementMode.Enforced,
                ConsequenceTier = "UNKNOWN", // never reached the catalog/target lookup; excluded from rollout stats anyway
                WouldHaveBlocked = false,
                CorrelationId = correlationId.ToString()
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record safety denial for agent {Agent} (non-fatal).", agentId);
        }
    }
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
/// Always-on safety gate checked before policy — the day-one net that does not warm up. Order: global freeze,
/// then per-agent quarantine, then the execution-velocity breaker, then the deny-velocity breaker. Any trip is
/// a hard deny.
/// </summary>
public sealed class SafetyGate(
    KillSwitchService killSwitch, QuarantineService quarantine, CircuitBreaker breaker, DenyVelocityBreaker denyBreaker)
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

        var q = await quarantine.GetAsync(agentId, ct);
        if (q is not null)
            return new SafetyResult
            {
                Allowed = false,
                ReasonCode = ReasonCode.AgentQuarantined,
                Reason = $"Agent '{agentId}' is quarantined since {q.SinceUtc:u}" +
                         (string.IsNullOrWhiteSpace(q.Reason) ? "." : $": {q.Reason}")
            };

        var velocity = await breaker.CheckAsync(agentId, ct);
        if (!velocity.Allowed) return velocity;

        return await denyBreaker.CheckAsync(agentId, ct);
    }

    /// <summary>
    /// Count a denial that happened OUTSIDE policy (so it has no policy observation) against the agent's
    /// deny velocity. Only call this for a RESOLVED agent: an unauthenticated probe claiming an agent's name
    /// must never be able to quarantine that agent. Frozen / breaker / quarantine denials are not counted,
    /// since they are consequences of the safety net itself.
    /// </summary>
    public Task RecordDenialAsync(string agentId, string actionId, string reasonCode, Guid correlationId, CancellationToken ct)
    {
        if (reasonCode is ReasonCode.SystemFrozen or ReasonCode.CircuitBreaker or ReasonCode.AgentQuarantined)
            return Task.CompletedTask;
        return denyBreaker.RecordDenialAsync(agentId, actionId, reasonCode, correlationId, ct);
    }
}
