using System.Security.Claims;

namespace AgentPermit.Gateway.Services;

/// <summary>
/// Entra ID configuration for the admin control-plane. When <see cref="IsConfigured"/>, the gateway
/// validates admin callers' Entra bearer tokens and authorizes them by Entra <b>group</b> membership
/// (RBAC-as-Entra-groups, the product direction) or an app role. This is how our own admin UI signs
/// admins in without us re-implementing identity or role management.
/// </summary>
public sealed class AdminAuthOptions
{
    /// <summary>Entra v2 authority, e.g. https://login.microsoftonline.com/&lt;tenantId&gt;/v2.0.</summary>
    public string? Authority { get; set; }

    /// <summary>Accepted audience — the admin API's Entra app id (or api://&lt;appId&gt;).</summary>
    public string? Audience { get; set; }

    /// <summary>Entra security group object id whose members are administrators (RBAC via groups).</summary>
    public string? AdminGroupId { get; set; }

    /// <summary>App role value that also grants admin (alternative to group membership).</summary>
    public string AdminAppRole { get; set; } = "Admin.Access";

    /// <summary>Browser origins allowed to call the API (the admin SPA). Empty disables CORS.</summary>
    public string[] AllowedCorsOrigins { get; set; } = Array.Empty<string>();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(Audience);

    /// <summary>
    /// Both accepted issuers for the tenant. Custom-API access tokens are commonly issued as v1
    /// (<c>sts.windows.net</c>) even when the authority is v2, so we trust both forms.
    /// </summary>
    public string[] ValidIssuers()
    {
        if (string.IsNullOrWhiteSpace(Authority)) return Array.Empty<string>();
        var tenant = Authority.TrimEnd('/').Split('/').FirstOrDefault(p => Guid.TryParse(p, out _));
        if (string.IsNullOrWhiteSpace(tenant)) return Array.Empty<string>();
        return new[]
        {
            $"https://login.microsoftonline.com/{tenant}/v2.0",
            $"https://sts.windows.net/{tenant}/",
        };
    }
}

/// <summary>
/// Single gate for administrative operations. Authorizes a caller by <b>either</b> the interim
/// shared admin key (bootstrap / service-to-service) <b>or</b> a valid Entra bearer token carrying
/// the admin group / app-role claim. When neither the key nor Entra is configured (local/dev/tests),
/// admin operations are open. Centralising this keeps every admin controller consistent.
/// </summary>
public sealed class AdminAuthorizationService(
    AdminOptions key, AdminAuthOptions entra, AccessDirectoryService directory, ILogger<AdminAuthorizationService> logger)
{
    public bool IsAuthorized(HttpContext ctx)
    {
        // Dev-open only when no admin mechanism is configured at all.
        if (!key.IsConfigured && !entra.IsConfigured) return true;

        // 1) Shared admin key (still configured → constant-time compare, not dev-open).
        if (key.IsConfigured)
        {
            var provided = ctx.Request.Headers[key.HeaderName].ToString();
            if (key.Matches(string.IsNullOrEmpty(provided) ? null : provided)) return true;
        }

        // 2) Entra bearer token with an admin group / app role.
        if (entra.IsConfigured && HasAdminClaim(ctx.User, entra))
        {
            logger.LogInformation("Admin authorized via Entra identity {Sub}.", ctx.User.FindFirstValue("oid") ?? "?");
            return true;
        }

        // 3) Membership in AgentPermit's stored 'admin' role (Roles & access is the source of truth).
        if (entra.IsConfigured && directory.RolesForCached(ctx).Contains("admin"))
        {
            logger.LogInformation("Admin authorized via AgentPermit admin-role membership.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the caller may READ the workspace: any admin, or membership in any AgentPermit role
    /// (approver / agent owner / viewer). Writes remain gated by <see cref="IsAuthorized"/>.
    /// </summary>
    public async Task<bool> IsMemberAsync(HttpContext ctx, CancellationToken ct = default)
    {
        if (IsAuthorized(ctx)) return true;
        var roles = await directory.RolesForAsync(ctx, ct);
        return roles.Count > 0;
    }

    /// <summary>Whether the principal carries the Entra admin app role or admin group claim.</summary>
    internal static bool HasAdminClaim(ClaimsPrincipal? user, AdminAuthOptions entra)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        // App role — accept the original AAD "roles" claim and the mapped ClaimTypes.Role, plus IsInRole.
        if (HasClaimValue(user, "roles", entra.AdminAppRole) ||
            HasClaimValue(user, ClaimTypes.Role, entra.AdminAppRole) ||
            user.IsInRole(entra.AdminAppRole))
            return true;
        // Group membership (RBAC via Entra groups).
        if (!string.IsNullOrWhiteSpace(entra.AdminGroupId) &&
            (HasClaimValue(user, "groups", entra.AdminGroupId!) ||
             HasClaimValue(user, ClaimTypes.GroupSid, entra.AdminGroupId!)))
            return true;
        return false;
    }

    private static bool HasClaimValue(ClaimsPrincipal user, string type, string value)
        => user.FindAll(type).Any(c => string.Equals(c.Value, value, StringComparison.Ordinal));
}
