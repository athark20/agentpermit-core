using System.Security.Claims;
using System.Text.Json;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Services;

/// <summary>
/// Resolves who a caller is and which AgentPermit personas (roles) they hold. AgentPermit's stored
/// role membership (Roles &amp; access) is the source of truth; the Entra admin app-role/group also
/// grants admin. Follows the identity-binding rule: a human's identity is derived from the
/// authenticated caller, never trusted from a request body — except on the service-to-service path
/// (valid shared admin key, e.g. the Teams approval workflow), where the human responder is not the
/// HTTP caller.
/// </summary>
public sealed class AccessDirectoryService(IConfigStore store, AdminOptions key, AdminAuthOptions entra, ILogger<AccessDirectoryService> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile Dictionary<string, HashSet<string>>? _membership;   // role key -> member emails (lower-case)
    private DateTimeOffset _loadedUtc = DateTimeOffset.MinValue;

    /// <summary>True when no auth mechanism is configured at all (local/dev/tests) — everything open.</summary>
    public bool DevOpen => !key.IsConfigured && !entra.IsConfigured;

    /// <summary>True when the request presents the valid shared admin key (service-to-service identity).</summary>
    public bool IsServiceCaller(HttpContext ctx)
    {
        if (!key.IsConfigured) return false;
        var provided = ctx.Request.Headers[key.HeaderName].ToString();
        return !string.IsNullOrEmpty(provided) && key.Matches(provided);
    }

    /// <summary>The caller's persona keys: admin · approver · agent-owner · viewer. Empty = no access.</summary>
    public async Task<IReadOnlyList<string>> RolesForAsync(HttpContext ctx, CancellationToken ct = default)
    {
        if (DevOpen || IsServiceCaller(ctx)) return new[] { "admin" };
        var user = ctx.User;
        if (user?.Identity?.IsAuthenticated != true) return Array.Empty<string>();

        var roles = new List<string>();
        if (AdminAuthorizationService.HasAdminClaim(user, entra)) roles.Add("admin");

        var email = EmailOf(user);
        if (!string.IsNullOrEmpty(email))
        {
            var membership = await GetMembershipAsync(ct);
            foreach (var (roleKey, members) in membership)
                if (members.Contains(email) && !roles.Contains(roleKey))
                    roles.Add(roleKey);
        }
        return roles;
    }

    /// <summary>Cached-only variant safe for synchronous callers; kicks off a background refresh when stale.</summary>
    public IReadOnlyList<string> RolesForCached(HttpContext ctx)
    {
        if (DevOpen || IsServiceCaller(ctx)) return new[] { "admin" };
        var user = ctx.User;
        if (user?.Identity?.IsAuthenticated != true) return Array.Empty<string>();

        var roles = new List<string>();
        if (AdminAuthorizationService.HasAdminClaim(user, entra)) roles.Add("admin");

        var email = EmailOf(user);
        var snapshot = _membership;
        if (snapshot is null || DateTimeOffset.UtcNow - _loadedUtc > CacheTtl)
            _ = Task.Run(() => GetMembershipAsync(CancellationToken.None));   // warm for next time
        if (!string.IsNullOrEmpty(email) && snapshot is not null)
            foreach (var (roleKey, members) in snapshot)
                if (members.Contains(email) && !roles.Contains(roleKey))
                    roles.Add(roleKey);
        return roles;
    }

    /// <summary>Human-readable identity of the caller, derived from the authenticated principal.</summary>
    public (string Name, string Email) Describe(HttpContext ctx)
    {
        if (DevOpen) return ("Developer", string.Empty);
        if (IsServiceCaller(ctx)) return ("service", string.Empty);
        var user = ctx.User;
        var email = EmailOf(user) ?? string.Empty;
        var name = user?.FindFirstValue("name") ?? user?.FindFirstValue(ClaimTypes.Name) ?? email;
        return (string.IsNullOrEmpty(name) ? email : name, email);
    }

    /// <summary>
    /// The approver identity to record for an approval decision, per the identity-binding rule:
    /// Entra path → the authenticated person (body ignored); service-key path → body (the Teams
    /// responder) or "teams-workflow"; dev-open → body or "developer".
    /// </summary>
    public string ResolveApproverIdentity(HttpContext ctx, string? bodyApproverId)
    {
        if (IsServiceCaller(ctx)) return string.IsNullOrWhiteSpace(bodyApproverId) ? "teams-workflow" : bodyApproverId.Trim();
        if (DevOpen) return string.IsNullOrWhiteSpace(bodyApproverId) ? "developer" : bodyApproverId.Trim();
        var (name, email) = Describe(ctx);
        return string.IsNullOrEmpty(email) || string.Equals(name, email, StringComparison.OrdinalIgnoreCase)
            ? (string.IsNullOrEmpty(email) ? name : email)
            : $"{name} <{email}>";
    }

    /// <summary>Drop the membership cache (called after Roles &amp; access changes so they apply immediately).</summary>
    public void Invalidate() => _loadedUtc = DateTimeOffset.MinValue;

    private static string? EmailOf(ClaimsPrincipal? user)
    {
        var raw = user?.FindFirstValue("preferred_username")
                  ?? user?.FindFirstValue(ClaimTypes.Upn)
                  ?? user?.FindFirstValue("email")
                  ?? user?.FindFirstValue(ClaimTypes.Email);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();
    }

    private async Task<Dictionary<string, HashSet<string>>> GetMembershipAsync(CancellationToken ct)
    {
        if (_membership is not null && DateTimeOffset.UtcNow - _loadedUtc <= CacheTtl) return _membership;
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_membership is not null && DateTimeOffset.UtcNow - _loadedUtc <= CacheTtl) return _membership;
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in AccessRoles.All)
            {
                var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var rec = await store.GetAsync("access-role", role.Key, ct);
                    if (rec is not null)
                    {
                        using var doc = JsonDocument.Parse(rec.Json);
                        if (doc.RootElement.TryGetProperty("members", out var arr) && arr.ValueKind == JsonValueKind.Array)
                            foreach (var m in arr.EnumerateArray())
                                if (m.TryGetProperty("email", out var e) && e.GetString() is { Length: > 0 } em)
                                    members.Add(em.Trim().ToLowerInvariant());
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not load membership for role {Role}; treating as empty.", role.Key);
                }
                map[role.Key] = members;
            }
            _membership = map;
            _loadedUtc = DateTimeOffset.UtcNow;
            return map;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
