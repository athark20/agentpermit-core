namespace AgentPermit.Gateway.Licensing;

/// <summary>Feature keys gated by edition. Core enforcement is NEVER feature-gated — only advanced/scale capabilities are.</summary>
public static class Features
{
    public const string ConnectorGovernance = "connector-governance"; // discover · risk-classify · auto-DLP
    public const string ObservationFeeds     = "observation-feeds";    // Purview / Flow ingestion
    public const string Sso                   = "sso";
    public const string AdvancedRbac          = "advanced-rbac";
    public const string SiemExport            = "siem-export";
    public const string HighAvailability      = "ha";
}

/// <summary>
/// What a deployment is licensed to do. Derived from a signed license (see <see cref="LicenseService"/>).
/// IMPORTANT: entitlements gate <b>management and advanced features</b> — they never disable the core
/// enforcement of already-configured policy. A lapsed licence locks management, not protection.
/// </summary>
public sealed record Entitlements
{
    public string Edition { get; init; } = "Enterprise";
    public int MaxAgents { get; init; } = int.MaxValue;
    public int MaxEnvironments { get; init; } = 99;
    public IReadOnlySet<string> FeatureSet { get; init; } = new HashSet<string> { "*" };
    public DateTimeOffset? ExpiresUtc { get; init; }
    public string? CustomerId { get; init; }

    /// <summary>Management/writes are blocked (missing/expired/invalid licence) — enforcement of existing policy continues.</summary>
    public bool ManagementLocked { get; init; }

    public bool Has(string feature) => FeatureSet.Contains("*") || FeatureSet.Contains(feature);
    public bool Expired => ExpiresUtc is { } e && DateTimeOffset.UtcNow > e;

    // ---- presets ----
    /// <summary>Licensing not enforced (dev / unlicensed-off): everything on, no caps.</summary>
    public static Entitlements Unlimited => new() { Edition = "Unlicensed (enforcement off)" };
    public static Entitlements Standard  => new() { Edition = "Standard", MaxAgents = 25, MaxEnvironments = 1, FeatureSet = new HashSet<string>() };
    public static Entitlements Enterprise => new() { Edition = "Enterprise" };
    /// <summary>Enforce=on but no valid licence: block new management + premium features; keep protecting.</summary>
    public static Entitlements Restricted(string why) => new()
    {
        Edition = "Unlicensed", MaxAgents = 0, MaxEnvironments = 0,
        FeatureSet = new HashSet<string>(), ManagementLocked = true, CustomerId = why
    };
}

/// <summary>Resolves the deployment's current entitlements.</summary>
public interface IEntitlementsProvider
{
    Entitlements Current { get; }
}
