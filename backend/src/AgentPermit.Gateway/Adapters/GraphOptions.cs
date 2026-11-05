namespace AgentPermit.Gateway.Adapters;

/// <summary>
/// Microsoft Graph app registration used by <see cref="GraphShareAdapter"/> (configuration section "Graph").
/// Client-credentials flow; the secret lives only here, never with the agent.
/// </summary>
public sealed class GraphOptions
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Drive (document library) used when a request names a file but no driveId.</summary>
    public string DefaultDriveId { get; set; } = string.Empty;
}
