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

    /// <summary>
    /// When false, a trusted connector may NO LONGER name the calling agent in the request body /
    /// X-Agent-Identity header: the agent is whoever the validated token says it is, full stop. Set this
    /// once every agent has its own Entra Agent ID (Copilot Studio issues one per agent since May 2026) or
    /// its own connection, because a shared-connector badge is an assertion the gateway cannot verify —
    /// any agent behind that connector could claim another's badge and inherit its rules and posture.
    /// </summary>
    public bool AllowBodyAssertedAgentIdentity { get; set; } = true;

    /// <summary>
    /// Gateway-side validation of the AGENT's bearer token (defence in depth behind APIM). Configure this in
    /// production: without it the gateway trusts an APIM-forwarded header plus a static shared secret, so a
    /// leaked secret or an ingress reachable around APIM lets a caller impersonate any registered agent.
    /// </summary>
    public AgentAuthOptions AgentAuth { get; set; } = new();
}

/// <summary>
/// Entra (or any OIDC issuer) configuration for validating the calling AGENT's token at the gateway itself.
/// When configured, the agent's identity is read from the validated token's <c>azp</c>/<c>appid</c> claim —
/// the APIM-forwarded header is ignored — and a delegated token's user claim becomes the verified
/// <c>initiatingUserId</c> that separation-of-duties checks rely on.
/// </summary>
public sealed class AgentAuthOptions
{
    /// <summary>The authentication scheme the gateway registers for agent tokens.</summary>
    public const string Scheme = "AgentPermit.AgentAuth";

    /// <summary>OIDC authority, e.g. https://login.microsoftonline.com/&lt;tenantId&gt;/v2.0.</summary>
    public string? Authority { get; set; }

    /// <summary>Accepted audience — the gateway API's app id (or api://&lt;appId&gt;).</summary>
    public string? Audience { get; set; }

    /// <summary>Extra accepted issuers. Entra v1/v2 issuers for the authority's tenant are added automatically.</summary>
    public string[] AdditionalIssuers { get; set; } = Array.Empty<string>();

    /// <summary>
    /// When true, an action request without a valid agent token is denied (<c>TOKEN_INVALID</c>). Leave false
    /// while migrating so the APIM header path keeps working; turn on once every agent sends a token.
    /// </summary>
    public bool Required { get; set; } = false;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(Audience);

    /// <summary>Accepted issuers: the tenant's v2 and v1 forms (Entra issues v1 for custom APIs), plus any extras.</summary>
    public string[] ValidIssuers()
    {
        var issuers = new List<string>(AdditionalIssuers);
        var tenant = Authority?.TrimEnd('/').Split('/').FirstOrDefault(p => Guid.TryParse(p, out _));
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            issuers.Add($"https://login.microsoftonline.com/{tenant}/v2.0");
            issuers.Add($"https://sts.windows.net/{tenant}/");
        }
        return issuers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Audiences accepted for a token: the configured value in both bare and api:// forms.</summary>
    public string[] ValidAudiences()
    {
        var aud = Audience ?? string.Empty;
        return aud.StartsWith("api://", StringComparison.Ordinal)
            ? new[] { aud, aud["api://".Length..] }
            : new[] { aud, $"api://{aud}" };
    }
}

/// <summary>
/// Who is calling. Established from a token the GATEWAY validated when agent auth is configured, else from
/// APIM-forwarded headers — never from the JSON body.
/// </summary>
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

    /// <summary>
    /// True when the gateway itself validated the caller's bearer token (signature, issuer, audience,
    /// lifetime). False means the identity rests on APIM's validation plus the forwarded header.
    /// </summary>
    public bool TokenVerified { get; init; }

    /// <summary>
    /// The human the agent is acting for, derived from a DELEGATED token's own claims. Null for an app-only
    /// token. When present it overrides any body-declared initiatingUserId, so separation-of-duties checks
    /// rest on a verified identity rather than a value the caller chose.
    /// </summary>
    public string? AuthenticatedUserId { get; init; }

    /// <summary>
    /// True when the caller presented a bearer token at all. Presenting a token that fails validation is an
    /// attack signal and is always refused; presenting none may still be allowed while migrating.
    /// </summary>
    public bool TokenPresented { get; init; }

    /// <summary>Why token validation failed, when agent auth is configured and a token was rejected.</summary>
    public string? TokenError { get; init; }

    /// <summary>A caller with no identity assertions (local/dev direct calls).</summary>
    public static readonly CallerContext Anonymous = new();
}
