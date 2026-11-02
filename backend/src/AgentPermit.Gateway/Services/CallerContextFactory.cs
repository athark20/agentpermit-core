using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace AgentPermit.Gateway.Services;

/// <summary>
/// Establishes <see cref="CallerContext"/> for an action request, on every front door (REST and MCP), from
/// the strongest evidence available:
///
/// 1. <b>A token this gateway validated</b> (when <see cref="AgentAuthOptions.IsConfigured"/>). The agent is
///    the token's <c>azp</c>/<c>appid</c>; the APIM-forwarded identity header is ignored entirely. This is
///    defence in depth: without it the gateway trusts a header plus a static shared secret, so a leaked
///    secret — or an ingress reachable around APIM — lets a caller impersonate any registered agent.
/// 2. <b>APIM-forwarded headers</b> otherwise (the pre-existing contract), unchanged.
///
/// A delegated token also yields the verified human (<see cref="CallerContext.AuthenticatedUserId"/>), which
/// is what separation-of-duties checks need: a body-declared initiating user is a value the caller chose.
/// </summary>
public sealed class CallerContextFactory(SecurityOptions security, ILogger<CallerContextFactory> logger)
{
    public async Task<CallerContext> BuildAsync(HttpContext ctx)
    {
        var headerClientId = ctx.Request.Headers[security.IdentityHeader].ToString();
        var origin = ctx.Request.Headers[security.ApimOriginHeader].ToString();
        var headerAgentIdentity = ctx.Request.Headers[security.AgentIdentityHeader].ToString();

        var originVerified = !string.IsNullOrEmpty(security.ApimSharedSecret)
                             && string.Equals(origin, security.ApimSharedSecret, StringComparison.Ordinal);

        string? clientId = string.IsNullOrWhiteSpace(headerClientId) ? null : headerClientId;
        string? userId = null;
        string? tokenError = null;
        var tokenVerified = false;
        var tokenPresented = ctx.Request.Headers.Authorization.Any(
            h => h is not null && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase));

        if (security.AgentAuth.IsConfigured)
        {
            var result = await ctx.AuthenticateAsync(AgentAuthOptions.Scheme);
            if (result.Succeeded && result.Principal is { } principal)
            {
                tokenVerified = true;
                // The token is now authoritative for WHO is calling; a disagreeing header is ignored (and
                // logged, because it means something upstream is misconfigured or probing).
                var fromToken = ClientIdOf(principal);
                if (!string.IsNullOrEmpty(clientId) && !string.Equals(clientId, fromToken, StringComparison.OrdinalIgnoreCase))
                    logger.LogWarning("Identity header '{Header}' disagrees with the validated token '{Token}'; using the token.",
                        clientId, fromToken);
                clientId = fromToken;
                userId = UserIdOf(principal);
            }
            else
            {
                tokenError = result.Failure?.Message
                             ?? (result.None ? "No bearer token was presented." : "The bearer token was rejected.");
                // Fail closed on the identity the token would have carried: an invalid token must never fall
                // back to the header path, or validation would be optional in practice.
                clientId = null;
                logger.LogWarning("Agent token validation failed: {Error}", tokenError);
            }
        }

        var trustedConnector = !string.IsNullOrWhiteSpace(clientId)
                               && security.TrustedConnectorClientIds.Contains(clientId, StringComparer.OrdinalIgnoreCase);

        return new CallerContext
        {
            AuthenticatedClientId = clientId,
            OriginVerified = originVerified,
            // A body/header-asserted agent badge is only honoured while that path is enabled.
            AssertedAgentIdentity = security.AllowBodyAssertedAgentIdentity && !string.IsNullOrWhiteSpace(headerAgentIdentity)
                ? headerAgentIdentity : null,
            FromTrustedConnector = trustedConnector && security.AllowBodyAssertedAgentIdentity,
            TokenVerified = tokenVerified,
            TokenPresented = tokenPresented,
            AuthenticatedUserId = userId,
            TokenError = tokenError
        };
    }

    /// <summary>The calling application: <c>azp</c> (v2 tokens) or <c>appid</c> (v1).</summary>
    private static string? ClientIdOf(ClaimsPrincipal p)
        => Trimmed(p.FindFirstValue("azp")) ?? Trimmed(p.FindFirstValue("appid"));

    /// <summary>
    /// The human a DELEGATED token acts for. App-only tokens carry an <c>oid</c> too (the service principal's),
    /// so a user is only claimed when the token actually represents one: a user-principal claim, or a
    /// delegated-scope token's object id. Entra's <c>idtyp=app</c> is honoured when present.
    /// </summary>
    private static string? UserIdOf(ClaimsPrincipal p)
    {
        if (string.Equals(Trimmed(p.FindFirstValue("idtyp")), "app", StringComparison.OrdinalIgnoreCase)) return null;

        var named = Trimmed(p.FindFirstValue("preferred_username"))
                    ?? Trimmed(p.FindFirstValue(ClaimTypes.Upn))
                    ?? Trimmed(p.FindFirstValue("upn"))
                    ?? Trimmed(p.FindFirstValue("email"));
        if (named is not null) return named;

        var delegated = Trimmed(p.FindFirstValue("scp")) ?? Trimmed(p.FindFirstValue("scope"));
        return delegated is null ? null : Trimmed(p.FindFirstValue("oid")) ?? Trimmed(p.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
