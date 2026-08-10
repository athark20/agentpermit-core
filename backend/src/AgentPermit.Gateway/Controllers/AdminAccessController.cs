using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Roles &amp; access, managed IN AgentPermit and pushed OUT to Microsoft Entra (one-way). The admin decides
/// who belongs to each of the four fixed roles here; "Apply in Entra" reconciles the matching Entra security
/// group to match. AgentPermit is the source of truth — membership is never read back from Entra as authority.
/// Entra-gated and audited. Writes to Entra require a one-time admin consent; until then, saving still works
/// and applying reports that approval is needed.
/// </summary>
[ApiController]
[Route("api/admin/access")]
public sealed class AdminAccessController(
    IConfigStore store, EntraProvisioningService entra, AdminAuthorizationService admin,
    AccessDirectoryService directory, AuditService audit) : ControllerBase
{
    private const string Type = "access-role";

    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Whether the app→Entra path is connected and consented, plus the one-time consent link.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var s = await entra.GetStatusAsync(ct);
        return Ok(s);
    }

    /// <summary>The four roles, each with the members saved in AgentPermit and its last-applied state.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var rows = new List<object>();
        foreach (var role in AccessRoles.All)
        {
            var saved = await LoadAsync(role.Key, ct);
            rows.Add(new
            {
                key = role.Key,
                name = role.Name,
                description = role.Description,
                members = saved?.Members ?? new List<Member>(),
                entraGroupId = saved?.EntraGroupId,
                lastSyncedUtc = saved?.LastSyncedUtc,
                lastAdded = saved?.LastAdded,
                lastRemoved = saved?.LastRemoved,
                lastUnresolved = saved?.LastUnresolved ?? new List<string>(),
                appliedMemberCount = saved?.LastMemberCount,
            });
        }
        return Ok(rows);
    }

    /// <summary>
    /// Type-ahead search of your Microsoft Entra directory for the people-picker. Degrades gracefully:
    /// returns an empty list (rather than an error) when the directory isn't connected/consented yet, so
    /// an admin can still add people by typing a full email address.
    /// </summary>
    [HttpGet("directory/search")]
    public async Task<IActionResult> SearchDirectory([FromQuery] string? q, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Ok(Array.Empty<object>());
        try
        {
            return Ok(await entra.SearchUsersAsync(q, ct));
        }
        catch (EntraConsentRequiredException)
        {
            return Ok(Array.Empty<object>());   // not connected yet → no suggestions; manual entry still works
        }
    }

    /// <summary>Save who belongs to a role in AgentPermit (does not touch Entra until "Apply").</summary>
    [HttpPut("{role}")]
    public async Task<IActionResult> Save(string role, [FromBody] SaveRoleRequest body, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var def = AccessRoles.Find(role);
        if (def is null) throw new NotFoundException("That role does not exist.") { Title = "Unknown role" };

        var existing = await LoadAsync(def.Key, ct);
        var doc = existing ?? new StoredRole();
        doc.Members = (body.Members ?? new List<Member>())
            .Where(m => !string.IsNullOrWhiteSpace(m.Email))
            .Select(m => new Member { Email = m.Email.Trim(), DisplayName = m.DisplayName?.Trim() ?? string.Empty })
            .GroupBy(m => m.Email, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        await SaveAsync(def.Key, doc, ct);
        directory.Invalidate();   // membership changes take effect immediately
        await audit.WriteAsync(HttpContext, "access.save", "AccessRole", def.Key,
            $"Saved {doc.Members.Count} member(s) for role '{def.Name}'");
        return Ok(new { key = def.Key, members = doc.Members });
    }

    /// <summary>Push a role's saved membership to its Entra security group (one-way reconcile).</summary>
    [HttpPost("{role}/sync")]
    public async Task<IActionResult> Sync(string role, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var def = AccessRoles.Find(role);
        if (def is null) throw new NotFoundException("That role does not exist.") { Title = "Unknown role" };

        var doc = await LoadAsync(def.Key, ct) ?? new StoredRole();
        try
        {
            var result = await entra.SyncRoleAsync(def, doc.Members.Select(m => m.Email).ToList(), ct);
            doc.EntraGroupId = result.EntraGroupId;
            doc.LastSyncedUtc = result.SyncedUtc;
            doc.LastAdded = result.Added;
            doc.LastRemoved = result.Removed;
            doc.LastUnresolved = result.Unresolved;
            doc.LastMemberCount = result.MemberCount;
            await SaveAsync(def.Key, doc, ct);
            await audit.WriteAsync(HttpContext, "access.sync", "AccessRole", def.Key,
                $"Applied role '{def.Name}' to Entra group {result.EntraGroupId}: +{result.Added} -{result.Removed}, {result.MemberCount} members");
            return Ok(result);
        }
        catch (EntraConsentRequiredException ex)
        {
            throw new ConflictException(ex.Message) { Title = "Approval needed" };
        }
    }

    /// <summary>
    /// Landing page Microsoft returns the admin's browser to after they approve (or decline) AgentPermit's
    /// Entra permissions. Anonymous by design — the browser arrives straight from Microsoft with no app token,
    /// carrying <c>admin_consent=True</c> on success or <c>error</c>/<c>error_description</c> on failure. This
    /// page only reports the outcome in plain language; the actual permission grant happened at Microsoft.
    /// </summary>
    [HttpGet("consent-complete")]
    public ContentResult ConsentComplete(
        [FromQuery(Name = "admin_consent")] string? adminConsent,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription)
    {
        var approved = string.Equals(adminConsent, "True", StringComparison.OrdinalIgnoreCase)
                       && string.IsNullOrEmpty(error);
        var html = approved
            ? ConsentPage("You're all set", "#0a7d33",
                "AgentPermit is now approved to manage roles in Microsoft Entra.",
                "You can close this tab and go back to AgentPermit. On the Roles &amp; access page, choose “Check again” and it will show <b>Connected</b>.")
            : ConsentPage("Approval didn't finish", "#b3261e",
                "AgentPermit was not approved, so it can't manage roles in Microsoft Entra yet.",
                "You can close this tab and try “Approve in Microsoft” again. If it keeps failing, an administrator may need to grant approval."
                + (string.IsNullOrWhiteSpace(error) ? "" : $"<br/><span style='color:#98a2b3'>Details: {WebUtility.HtmlEncode(error)}{(string.IsNullOrWhiteSpace(errorDescription) ? "" : " — " + WebUtility.HtmlEncode(errorDescription))}</span>"));
        return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = StatusCodes.Status200OK };
    }

    private static string ConsentPage(string heading, string accent, string lead, string body) =>
        $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"/>
        <meta name="viewport" content="width=device-width, initial-scale=1"/>
        <title>AgentPermit — {{WebUtility.HtmlEncode(heading)}}</title></head>
        <body style="margin:0;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;background:#f5f6f8;color:#1a1a1a">
          <div style="max-width:460px;margin:12vh auto;padding:32px;background:#fff;border-radius:14px;box-shadow:0 1px 3px rgba(0,0,0,.08);border-top:4px solid {{accent}}">
            <div style="font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:#98a2b3;font-weight:600">AgentPermit</div>
            <h1 style="font-size:22px;margin:10px 0 8px">{{WebUtility.HtmlEncode(heading)}}</h1>
            <p style="font-size:15px;line-height:1.55;margin:0 0 14px">{{lead}}</p>
            <p style="font-size:14px;line-height:1.55;color:#475467;margin:0">{{body}}</p>
          </div>
        </body></html>
        """;

    // ---- storage helpers ----------------------------------------------------

    private async Task<StoredRole?> LoadAsync(string key, CancellationToken ct)
    {
        var rec = await store.GetAsync(Type, key, ct);
        if (rec is null) return null;
        return JsonSerializer.Deserialize<StoredRole>(rec.Json);
    }

    private Task SaveAsync(string key, StoredRole doc, CancellationToken ct)
        => store.UpsertAsync(Type, key, JsonSerializer.Serialize(doc), ct);

    // ---- models -------------------------------------------------------------

    public sealed class Member
    {
        [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class SaveRoleRequest
    {
        [JsonPropertyName("members")] public List<Member>? Members { get; set; }
    }

    private sealed class StoredRole
    {
        [JsonPropertyName("members")] public List<Member> Members { get; set; } = new();
        [JsonPropertyName("entraGroupId")] public string? EntraGroupId { get; set; }
        [JsonPropertyName("lastSyncedUtc")] public DateTimeOffset? LastSyncedUtc { get; set; }
        [JsonPropertyName("lastAdded")] public int? LastAdded { get; set; }
        [JsonPropertyName("lastRemoved")] public int? LastRemoved { get; set; }
        [JsonPropertyName("lastUnresolved")] public List<string> LastUnresolved { get; set; } = new();
        [JsonPropertyName("lastMemberCount")] public int? LastMemberCount { get; set; }
    }
}
