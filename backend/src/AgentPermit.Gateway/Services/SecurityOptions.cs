namespace AgentPermit.Gateway.Services;

/// <summary>
/// Runtime security posture for agent identity binding and APIM-origin trust.
/// In production these are enforced; locally they default off so the gateway can be
/// exercised directly without APIM in front.
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// When true: the request must originate from APIM (verified shared secret) and carry an
    /// authenticated Entra identity bound to a registered agent; a body agentId that doesn't
    /// match that identity is rejected.
    /// </summary>
    public bool EnforceIdentityBinding { get; set; } = false;

    /// <summary>Header APIM sets to the token's app/client id (azp/appid).</summary>
    public string IdentityHeader { get; set; } = "X-Agent-Client-Id";

    /// <summary>Header APIM sets with the shared secret proving the call came through APIM.</summary>
    public string ApimOriginHeader { get; set; } = "X-APIM-Origin";

    /// <summary>Shared secret expected in <see cref="ApimOriginHeader"/>. Empty = origin unverifiable.</summary>
    public string ApimSharedSecret { get; set; } = string.Empty;

    /// <summary>
    /// Header the AgentPermit connector uses to carry the CALLING agent's own Entra Agent ID. When a
    /// call arrives from a trusted connector with this header, the gateway identifies the agent by the
    /// carried Agent ID rather than the connector app — per-agent governance of a shared-connector fleet.
    /// </summary>
    public string AgentIdentityHeader { get; set; } = "X-Agent-Identity";

    /// <summary>
    /// Client ids of trusted AgentPermit connector apps. Only these may carry an agent identity; a call
    /// from any other app cannot assert who the agent is. (Delivery trust; combine with Power Platform
    /// DLP + target lockdown for full zero-bypass.)
    /// </summary>
    public string[] TrustedConnectorClientIds { get; set; } = Array.Empty<string>();
}

/// <summary>Who is calling, as established from APIM-forwarded headers (never from the JSON body).</summary>
public sealed class CallerContext
{
    /// <summary>The authenticated Entra app/client id (azp), or null if not present.</summary>
    public string? AuthenticatedClientId { get; init; }

    /// <summary>True when the APIM shared-secret origin header matched.</summary>
    public bool OriginVerified { get; init; }

    /// <summary>The agent's own Entra Agent ID carried by a trusted connector, or null.</summary>
    public string? AssertedAgentIdentity { get; init; }

    /// <summary>True when the authenticated caller is a trusted AgentPermit connector app.</summary>
    public bool FromTrustedConnector { get; init; }

    /// <summary>A caller with no identity assertions (local/dev direct calls).</summary>
    public static readonly CallerContext Anonymous = new();
}
