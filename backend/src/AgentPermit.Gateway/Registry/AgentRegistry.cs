namespace AgentPermit.Gateway.Registry;

/// <summary>A registered agent that is allowed to request actions through the gateway.</summary>
public sealed class AgentInfo
{
    public string AgentId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The Entra workload identity (app/client id, i.e. the token's <c>azp</c>/<c>appid</c>) bound
    /// to this agent. Requests are authorized against this, not the body-supplied agentId.
    /// </summary>
    public string? EntraClientId { get; set; }

    /// <summary>Autonomous (runs without supervision) | Copilot (human-in-the-loop).</summary>
    public string? Type { get; set; }

    /// <summary>Business/technical owner accountable for the agent.</summary>
    public string? Owner { get; set; }

    /// <summary>Environment the agent runs in (e.g. prod-us-east, staging).</summary>
    public string? Environment { get; set; }

    /// <summary>Risk classification: Low | Medium | High.</summary>
    public string? RiskTier { get; set; }

    /// <summary>
    /// Rollout stage for this agent: OBSERVE | WARN | ENFORCE (see <see cref="Domain.EnforcementPosture"/>).
    /// Controls whether computed policy blocks / step-ups on LOW/MEDIUM-consequence actions are binding or
    /// merely recorded while the agent warms up after onboarding. Unset =&gt; ENFORCE (secure by default);
    /// HIGH/CRITICAL-consequence actions are always enforced regardless of posture.
    /// </summary>
    public string? EnforcementPosture { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}

public interface IAgentRegistry
{
    /// <summary>Returns the agent if registered and enabled; otherwise null.</summary>
    AgentInfo? Find(string? agentId);

    /// <summary>Resolve the agent bound to an authenticated Entra client id; otherwise null.</summary>
    AgentInfo? FindByEntraClientId(string? entraClientId);

    /// <summary>All registered agents (for the admin console).</summary>
    IReadOnlyList<AgentInfo> All();

    /// <summary>Refresh the registry from its backing store after a write.</summary>
    void Invalidate();
}

/// <summary>Options bound from configuration section "Registry:Agents".</summary>
public sealed class AgentRegistryOptions
{
    public List<AgentInfo> Agents { get; set; } = new();
}

/// <summary>
/// Registry seeded from configuration. In production this is backed by the
/// Dataverse ap_agent table (Week 1); for the vertical slice the registered set
/// is provided via appsettings so the slice runs without Dataverse connectivity.
/// </summary>
public sealed class ConfigAgentRegistry : IAgentRegistry
{
    private readonly Dictionary<string, AgentInfo> _byId;
    private readonly Dictionary<string, AgentInfo> _byClientId;
    private readonly IReadOnlyList<AgentInfo> _all;

    public ConfigAgentRegistry(AgentRegistryOptions options)
    {
        _all = options.Agents.ToList();
        _byId = options.Agents.ToDictionary(a => a.AgentId, a => a, StringComparer.OrdinalIgnoreCase);
        _byClientId = options.Agents
            .Where(a => !string.IsNullOrWhiteSpace(a.EntraClientId))
            .ToDictionary(a => a.EntraClientId!, a => a, StringComparer.OrdinalIgnoreCase);
    }

    public AgentInfo? Find(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        return _byId.TryGetValue(agentId, out var info) && info.Enabled ? info : null;
    }

    public AgentInfo? FindByEntraClientId(string? entraClientId)
    {
        if (string.IsNullOrWhiteSpace(entraClientId)) return null;
        return _byClientId.TryGetValue(entraClientId, out var info) && info.Enabled ? info : null;
    }

    public IReadOnlyList<AgentInfo> All() => _all;

    public void Invalidate() { /* config-backed registry is static */ }
}
