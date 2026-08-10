using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Services.PowerPlatform;

namespace AgentPermit.Gateway.Services;

/// <summary>
/// The four fixed AgentPermit access roles. AgentPermit is the source of truth: an admin decides who
/// belongs to each role in the app, and those decisions flow OUT to Microsoft Entra (one-way) — never
/// the reverse. Each role maps to one Entra security group AgentPermit owns and reconciles.
/// </summary>
public static class AccessRoles
{
    public sealed record RoleDef(string Key, string Name, string Description);

    public static readonly IReadOnlyList<RoleDef> All = new List<RoleDef>
    {
        new("admin",       "Admin",       "Full control — can set up connections, write rules and manage everyone's access."),
        new("approver",    "Approver",    "Can sign off on actions that pause for approval. Can't change the rules."),
        new("agent-owner", "Agent owner", "Looks after specific agents day-to-day. Can pause an agent and see its activity."),
        new("viewer",      "Viewer",      "Can see agents, activity and evidence — but can't change anything."),
    };

    public static RoleDef? Find(string key) =>
        All.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Entra security group AgentPermit creates/owns for a role.</summary>
    public static string GroupName(RoleDef r) => $"AgentPermit {r.Name}";
    public static string GroupMailNickname(RoleDef r) => $"agentpermit-{r.Key}";
}

/// <summary>Connection status for the app→Entra provisioning path — mirrors the gated feed pattern.</summary>
public sealed class EntraProvisioningStatus
{
    public bool Configured { get; set; }
    public bool Consented { get; set; }
    public string? TenantId { get; set; }
    public string? ConsentUrl { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Outcome of pushing one role's membership to Entra.</summary>
public sealed class RoleSyncResult
{
    public string Role { get; set; } = string.Empty;
    public string? EntraGroupId { get; set; }
    public bool GroupCreated { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    public List<string> Unresolved { get; set; } = new();
    public int MemberCount { get; set; }
    public DateTimeOffset SyncedUtc { get; set; }
}

/// <summary>A directory user suggestion for the people-picker (type-ahead).</summary>
public sealed class DirectoryUser
{
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? JobTitle { get; set; }
}

/// <summary>Raised when Entra rejects a call for lack of consented Graph permissions (one-time admin approval).</summary>
public sealed class EntraConsentRequiredException(string message) : Exception(message);

/// <summary>Deployment settings for the app→Entra consent flow.</summary>
public sealed class EntraProvisioningOptions
{
    /// <summary>
    /// Public HTTPS URL Microsoft returns the admin's browser to after they approve AgentPermit. Must be
    /// registered as a redirect URI on the app registration. When empty, the consent link omits an explicit
    /// return and Microsoft falls back to the app's default reply address (which may be a non-browser URI and
    /// break the flow) — so set this in every real deployment.
    /// </summary>
    public string ConsentReturnUrl { get; set; } = string.Empty;
}

/// <summary>
/// Pushes AgentPermit access-role membership into Microsoft Entra security groups via Microsoft Graph
/// (application permissions, client-credentials). Reuses the AgentPermit app registration
/// (<see cref="PowerPlatformOptions"/>); the only extra step is a ONE-TIME admin consent granting that app
/// the Graph write scopes (Group.ReadWrite.All, GroupMember.ReadWrite.All, User.Read.All). Until then the
/// service reports "needs approval" and performs no writes — nothing happens implicitly. Flow is one-way:
/// AgentPermit is authoritative and reconciles the group to match; it never reads membership back as truth.
/// </summary>
public sealed class EntraProvisioningService(PowerPlatformOptions options, EntraProvisioningOptions provisioning, IHttpClientFactory http, ILogger<EntraProvisioningService> logger)
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private (string token, DateTimeOffset exp) _cached;

    public bool IsConfigured => options.IsConfigured;

    /// <summary>
    /// One-time admin-consent link. Includes an explicit HTTPS <c>redirect_uri</c> so Microsoft returns the
    /// admin to AgentPermit's own "approved" page instead of the app's default reply address — the latter can
    /// be a non-browser URI (e.g. a legacy <c>urn:</c> from SP creation) that the consent page refuses to
    /// redirect to (AADSTS500119). The return URL must be registered on the app registration.
    /// </summary>
    public string ConsentUrl
    {
        get
        {
            var url = $"https://login.microsoftonline.com/{options.TenantId}/adminconsent?client_id={options.ClientId}";
            if (!string.IsNullOrWhiteSpace(provisioning.ConsentReturnUrl))
                url += $"&redirect_uri={Uri.EscapeDataString(provisioning.ConsentReturnUrl)}&state=agentpermit";
            return url;
        }
    }

    /// <summary>Fast status; probes Entra live only when configured, to tell "configured" from "consented".</summary>
    public async Task<EntraProvisioningStatus> GetStatusAsync(CancellationToken ct)
    {
        var status = new EntraProvisioningStatus
        {
            Configured = IsConfigured,
            TenantId = string.IsNullOrWhiteSpace(options.TenantId) ? null : options.TenantId,
            ConsentUrl = IsConfigured ? ConsentUrl : null,
        };
        if (!IsConfigured)
        {
            status.Message = "Not connected to Microsoft Entra yet. Connect your environment first (Settings → Connected systems).";
            return status;
        }
        try
        {
            var client = http.CreateClient();
            await AuthorizeAsync(client, ct);
            // A minimal read that requires the same directory consent as the writes.
            using var res = await client.GetAsync($"{Graph}/groups?$top=1&$select=id", ct);
            if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                status.Consented = false;
                status.Message = "One-time approval needed: an administrator must approve AgentPermit's permission to manage groups in Microsoft Entra.";
                return status;
            }
            res.EnsureSuccessStatusCode();
            status.Consented = true;
            status.Message = "Connected. Access changes you save here will be applied in Microsoft Entra.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Entra provisioning status probe failed.");
            status.Consented = false;
            status.Message = "Couldn't reach Microsoft Entra to confirm access. Try again, or check the connection.";
        }
        return status;
    }

    /// <summary>Reconcile one role's Entra group to exactly the given members. One-way: AgentPermit wins.</summary>
    public async Task<RoleSyncResult> SyncRoleAsync(AccessRoles.RoleDef role, IReadOnlyList<string> memberIdentifiers, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new EntraConsentRequiredException("Not connected to Microsoft Entra. Connect your environment first.");

        var client = http.CreateClient();
        await AuthorizeAsync(client, ct);

        var (groupId, created) = await EnsureGroupAsync(client, role, ct);
        var result = new RoleSyncResult { Role = role.Key, SyncedUtc = DateTimeOffset.UtcNow };

        // Resolve desired members (email / UPN) to directory object ids; skip any that don't resolve.
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // userId -> identifier
        foreach (var id in memberIdentifiers.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var userId = await ResolveUserAsync(client, id, ct);
            if (userId is null) result.Unresolved.Add(id);
            else desired[userId] = id;
        }

        // A just-created group has no members by definition — and querying it immediately can 404 on
        // Graph replicas that haven't caught up yet. Only read current members when the group pre-existed.
        HashSet<string> current;
        if (created)
        {
            current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // A group found by name can still be gone — deleted in Entra out-of-band, or a delete that
            // hasn't fully propagated to the find index. Reading its members then 404s. Self-heal by
            // recreating the group instead of failing the whole apply (AgentPermit owns this group).
            var read = await CurrentMemberIdsAsync(client, groupId, ct);
            if (read is null)
            {
                logger.LogWarning("Entra group {GroupId} for role {Role} no longer exists; recreating it.", groupId, role.Key);
                groupId = await CreateGroupAsync(client, role, ct);
                created = true;
                current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            else current = read;
        }

        result.EntraGroupId = groupId;
        result.GroupCreated = created;

        foreach (var userId in desired.Keys.Where(u => !current.Contains(u)))
        {
            await AddMemberAsync(client, groupId, userId, ct);
            result.Added++;
        }
        foreach (var userId in current.Where(u => !desired.ContainsKey(u)))
        {
            await RemoveMemberAsync(client, groupId, userId, ct);
            result.Removed++;
        }
        result.MemberCount = desired.Count;
        logger.LogInformation("Synced role {Role} to Entra group {GroupId}: +{Added} -{Removed}, {Count} members, {Unresolved} unresolved.",
            role.Key, groupId, result.Added, result.Removed, result.MemberCount, result.Unresolved.Count);
        return result;
    }

    // ---- Graph plumbing -----------------------------------------------------

    private async Task AuthorizeAsync(HttpClient client, CancellationToken ct)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_cached.token is not null && DateTimeOffset.UtcNow < _cached.exp) return _cached.token;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["scope"] = "https://graph.microsoft.com/.default",
        });
        using var client = http.CreateClient();
        using var res = await client.PostAsync($"https://login.microsoftonline.com/{options.TenantId}/oauth2/v2.0/token", form, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        _cached = (token, DateTimeOffset.UtcNow.AddMinutes(45));
        return token;
    }

    private async Task<(string groupId, bool created)> EnsureGroupAsync(HttpClient client, AccessRoles.RoleDef role, CancellationToken ct)
    {
        var name = AccessRoles.GroupName(role);
        var filter = Uri.EscapeDataString($"displayName eq '{name}'");
        using (var find = await Guard(client.GetAsync($"{Graph}/groups?$filter={filter}&$select=id,displayName", ct)))
        {
            using var doc = JsonDocument.Parse(await find.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var arr) && arr.GetArrayLength() > 0)
                return (arr[0].GetProperty("id").GetString()!, false);
        }

        return (await CreateGroupAsync(client, role, ct), true);
    }

    /// <summary>Create the Entra security group AgentPermit owns for a role (no find; always creates).</summary>
    private async Task<string> CreateGroupAsync(HttpClient client, AccessRoles.RoleDef role, CancellationToken ct)
    {
        var body = new
        {
            displayName = AccessRoles.GroupName(role),
            description = $"AgentPermit {role.Name} — {role.Description} (managed by AgentPermit; do not edit here).",
            mailEnabled = false,
            mailNickname = AccessRoles.GroupMailNickname(role),
            securityEnabled = true,
        };
        using var res = await Guard(client.PostAsJsonAsync($"{Graph}/groups", body, ct));
        using var createdDoc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return createdDoc.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Type-ahead directory search (name / email / UPN) for the people-picker. Uses Graph advanced query
    /// ($search + ConsistencyLevel: eventual), covered by the same User.Read.All consent as membership sync.
    /// Returns at most 15 suggestions; throws <see cref="EntraConsentRequiredException"/> if not consented.
    /// </summary>
    public async Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken ct)
    {
        if (!IsConfigured) throw new EntraConsentRequiredException("Not connected to Microsoft Entra.");
        var q = (query ?? string.Empty).Replace("\"", "").Trim();
        if (q.Length < 2) return Array.Empty<DirectoryUser>();

        var client = http.CreateClient();
        await AuthorizeAsync(client, ct);

        var searchExpr = $"\"displayName:{q}\" OR \"mail:{q}\" OR \"userPrincipalName:{q}\"";
        var url = $"{Graph}/users?$search={Uri.EscapeDataString(searchExpr)}"
                + "&$select=displayName,mail,userPrincipalName,jobTitle&$top=15&$count=true";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("ConsistencyLevel", "eventual");   // required for $search on directory objects
        using var res = await client.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw Consent(res);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning("Directory search returned {Status}.", res.StatusCode);
            return Array.Empty<DirectoryUser>();
        }
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var list = new List<DirectoryUser>();
        if (doc.RootElement.TryGetProperty("value", out var arr))
            foreach (var u in arr.EnumerateArray())
            {
                var email = (u.TryGetProperty("mail", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null)
                         ?? (u.TryGetProperty("userPrincipalName", out var up) ? up.GetString() : null);
                if (string.IsNullOrWhiteSpace(email)) continue;
                list.Add(new DirectoryUser
                {
                    DisplayName = u.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
                    Email = email,
                    JobTitle = u.TryGetProperty("jobTitle", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null,
                });
            }
        return list;
    }

    private async Task<string?> ResolveUserAsync(HttpClient client, string identifier, CancellationToken ct)
    {
        using var res = await client.GetAsync($"{Graph}/users/{Uri.EscapeDataString(identifier)}?$select=id", ct);
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw Consent(res);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
    }

    /// <summary>Current member object-ids, or <c>null</c> if the group no longer exists (404) — the caller
    /// treats null as "group is gone" and recreates it rather than failing the apply.</summary>
    private async Task<HashSet<string>?> CurrentMemberIdsAsync(HttpClient client, string groupId, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var url = $"{Graph}/groups/{groupId}/members?$select=id&$top=999";
        while (url is not null)
        {
            using var res = await client.GetAsync(url, ct);
            if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw Consent(res);
            if (res.StatusCode == HttpStatusCode.NotFound) return null; // group deleted out-of-band / not yet propagated
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("id", out var idEl) && idEl.GetString() is { } id) ids.Add(id);
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
        return ids;
    }

    private async Task AddMemberAsync(HttpClient client, string groupId, string userId, CancellationToken ct)
    {
        var body = new Dictionary<string, string> { ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{userId}" };
        for (var attempt = 1; ; attempt++)
        {
            using var res = await client.PostAsJsonAsync($"{Graph}/groups/{groupId}/members/$ref", body, ct);
            if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw Consent(res);
            if (res.IsSuccessStatusCode) return;
            var text = await res.Content.ReadAsStringAsync(ct);
            // Already a member → Graph returns 400 with a known code; treat as success (idempotent).
            if (text.Contains("already exist", StringComparison.OrdinalIgnoreCase)) return;
            // A just-created group can 404 briefly on Graph replicas — retry a few times before failing.
            if (res.StatusCode == HttpStatusCode.NotFound && attempt < 4) { await Task.Delay(1500, ct); continue; }
            res.EnsureSuccessStatusCode();
            return;
        }
    }

    private async Task RemoveMemberAsync(HttpClient client, string groupId, string userId, CancellationToken ct)
    {
        using var res = await client.DeleteAsync($"{Graph}/groups/{groupId}/members/{userId}/$ref", ct);
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw Consent(res);
        if (res.StatusCode == HttpStatusCode.NotFound) return; // already gone
        res.EnsureSuccessStatusCode();
    }

    /// <summary>Await a Graph call and translate a consent failure into a friendly, typed exception.</summary>
    private static async Task<HttpResponseMessage> Guard(Task<HttpResponseMessage> call)
    {
        var res = await call;
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw Consent(res);
        res.EnsureSuccessStatusCode();
        return res;
    }

    private static EntraConsentRequiredException Consent(HttpResponseMessage _)
        => new("One-time approval needed: an administrator must approve AgentPermit's permission to manage groups in Microsoft Entra.");
}
