using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentPermit.Gateway.Adapters;

/// <summary>
/// Microsoft Graph adapter (adapter type GRAPH): creates a sharing link for a document in a SharePoint /
/// OneDrive drive. The gateway holds the Graph app credentials; the agent only names the file and the
/// audience. Two safety rails live here, at execution, so they hold regardless of what the agent claims:
/// the audience must be one of organization | users | anonymous, and a document that is marked or named
/// as confidential is never shared beyond the organization.
/// </summary>
/// <remarks>
/// Parameters: <c>itemId</c> (or <c>fileId</c>) or <c>fileName</c>; optional <c>driveId</c> (falls back to
/// <see cref="GraphOptions.DefaultDriveId"/>), <c>scope</c> (organization | users | anonymous, default
/// organization), <c>linkType</c> (view | edit | embed, default view) and <c>classification</c>.
/// </remarks>
public sealed class GraphShareAdapter(HttpClient http, GraphOptions options, ILogger<GraphShareAdapter> logger)
    : ActionAdapterBase
{
    public const string Code = "GRAPH";
    public override string AdapterCode => Code;

    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    private static readonly string[] ConfidentialMarkers =
        { "privileged", "confidential", "restricted", "secret", "nda", "attorney-client", "client-memo", "clientmemo" };

    private (string? Token, DateTimeOffset Expires) _cached;

    public override async Task<AdapterResult> ExecuteAsync(AdapterRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.TenantId) || string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
            return AdapterResult.Fail("TARGET_NOT_CONFIGURED", "Microsoft Graph is not configured (Graph:TenantId/ClientId/ClientSecret).");

        var p = request.Parameters;
        var scope = (Str(p, "scope") ?? "organization").Trim().ToLowerInvariant();
        if (scope is not ("organization" or "users" or "anonymous"))
            return AdapterResult.Fail("INVALID_PARAMETERS", $"Unsupported scope '{scope}'. Use organization | users | anonymous.");

        var linkType = (Str(p, "linkType") ?? "view").Trim().ToLowerInvariant();
        if (linkType is not ("view" or "edit" or "embed")) linkType = "view";

        var driveId = Str(p, "driveId");
        if (string.IsNullOrWhiteSpace(driveId)) driveId = options.DefaultDriveId;
        if (string.IsNullOrWhiteSpace(driveId))
            return AdapterResult.Fail("INVALID_PARAMETERS", "Provide driveId, or configure Graph:DefaultDriveId.");

        string token;
        try
        {
            token = await TokenAsync(ct);
        }
        catch (Exception ex)
        {
            return AdapterResult.Fail("TARGET_UNAVAILABLE", "Graph token acquisition failed: " + ex.Message);
        }

        var fileName = Str(p, "fileName");
        var itemId = Str(p, "itemId") ?? Str(p, "fileId");
        if (string.IsNullOrWhiteSpace(itemId))
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return AdapterResult.Fail("INVALID_PARAMETERS", "Provide itemId, fileId, or fileName.");
            itemId = await ResolveItemIdAsync(token, driveId, fileName, ct);
            if (itemId is null)
                return AdapterResult.Fail("TARGET_REPORTED_FAILURE", $"No document named '{fileName}' was found in the drive.");
        }

        // Guardrail: nothing that looks confidential leaves the organization, whatever the agent says it is.
        if (scope is "anonymous" or "users")
        {
            var classification = (Str(p, "classification") ?? string.Empty).Trim().ToLowerInvariant();
            if (classification == "confidential" || LooksConfidential(fileName))
            {
                logger.LogWarning("Blocked non-internal share of an apparently-confidential document (scope={Scope}).", scope);
                return AdapterResult.Fail("BLOCKED_CONFIDENTIAL_EXPOSURE",
                    "Refused: this document appears confidential and the requested share is not organization-internal. " +
                    "AgentPermit blocks this at execution regardless of the agent's stated classification.");
            }
        }

        var requestUri = $"{GraphBase}/drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}/createLink";
        HttpResponseMessage resp;
        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(new { type = linkType, scope })
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            resp = await http.SendAsync(req, ct);
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning(ex, "Graph createLink call failed (network/timeout) for drive={Drive} item={Item}", driveId, itemId);
            return AdapterResult.Fail("TARGET_UNAVAILABLE", "Microsoft Graph was unreachable: " + ex.Message);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                return AdapterResult.Fail($"HTTP_{(int)resp.StatusCode}", body.Length <= 500 ? body : body[..500]);

            string? linkId = null, webUrl = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var id)) linkId = id.GetString();
                if (root.TryGetProperty("link", out var link) && link.TryGetProperty("webUrl", out var url)) webUrl = url.GetString();
            }
            catch (JsonException)
            {
                // Graph answered 2xx with a body we can't read: the link exists, we just can't name it.
            }

            logger.LogInformation("Graph createLink ok: scope={Scope} drive={Drive} item={Item}", scope, driveId, itemId);
            return AdapterResult.Ok(linkId ?? webUrl, body);
        }
    }

    private static bool LooksConfidential(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var lower = name.ToLowerInvariant();
        return ConfidentialMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>Finds a file by name: first as a path under the drive root, then by search. Null when neither finds it.</summary>
    private async Task<string?> ResolveItemIdAsync(string token, string driveId, string fileName, CancellationToken ct)
    {
        var drive = Uri.EscapeDataString(driveId);
        var name = Uri.EscapeDataString(fileName);
        var strategies = new[]
        {
            $"{GraphBase}/drives/{drive}/root:/{name}?$select=id",
            $"{GraphBase}/drives/{drive}/root/search(q='{name}')?$select=id,name&$top=1"
        };

        foreach (var url in strategies)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    return id.GetString();
                if (root.TryGetProperty("value", out var hits) && hits.ValueKind == JsonValueKind.Array && hits.GetArrayLength() > 0
                    && hits[0].TryGetProperty("id", out var firstId))
                    return firstId.GetString();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning(ex, "Graph item lookup failed for {Url}; trying next strategy.", url);
            }
        }
        return null;
    }

    /// <summary>Client-credentials token for Graph, cached for the life of this adapter instance.</summary>
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_cached.Token is not null && _cached.Expires > DateTimeOffset.UtcNow.AddMinutes(2))
            return _cached.Token;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["scope"] = "https://graph.microsoft.com/.default"
        });
        using var resp = await http.PostAsync($"https://login.microsoftonline.com/{options.TenantId}/oauth2/v2.0/token", form, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var token = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Token response carried no access_token.");
        _cached = (token, DateTimeOffset.UtcNow.AddMinutes(45));
        return token;
    }

    private static string? Str(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
