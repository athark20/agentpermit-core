using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPermit.Gateway.Services.PowerPlatform;

/// <summary>
/// A single Microsoft tenant + Power Platform (Dataverse) connection for discovering Copilot Studio
/// agents. App-only (client credentials) against the Dataverse Web API; the app is registered as a
/// Dataverse application user in that tenant's environment. A customer may run several tenants
/// (e.g. Dev / UAT / Prod), each a SEPARATE Entra directory needing its own app registration and
/// admin consent — hence one connection per tenant.
/// </summary>
public sealed class PowerPlatformConnection
{
    /// <summary>Stable short key used to select this connection, e.g. "dev", "uat", "prod".</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>Human label shown in the console, e.g. "Prod tenant" or "Accura Bank — Prod".</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>Dataverse environment URL, e.g. https://orgXXXXXXXX.crmN.dynamics.com.</summary>
    public string EnvironmentUrl { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    [JsonIgnore] public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(EnvironmentUrl) && !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    [JsonIgnore] public string Org => EnvironmentUrl.TrimEnd('/');

    /// <summary>Normalised key: explicit Key, else a slug of EnvironmentName, else "primary".</summary>
    [JsonIgnore] public string ResolvedKey => Slug(!string.IsNullOrWhiteSpace(Key) ? Key
        : !string.IsNullOrWhiteSpace(EnvironmentName) ? EnvironmentName : "primary");

    /// <summary>Best display label: explicit Label, else EnvironmentName, else the key.</summary>
    [JsonIgnore] public string ResolvedLabel => !string.IsNullOrWhiteSpace(Label) ? Label
        : !string.IsNullOrWhiteSpace(EnvironmentName) ? EnvironmentName : ResolvedKey;

    internal static string Slug(string s)
    {
        var chars = (s ?? "").Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-');
        var slug = new string(chars.ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}

/// <summary>
/// Connection settings bound from the <c>PowerPlatform</c> config section. Supports either a single
/// flat connection (legacy env vars <c>PowerPlatform__EnvironmentUrl</c> …) OR a list under
/// <c>PowerPlatform:Connections</c> for multiple tenants. The flat fields, when set, are treated as
/// one connection so existing single-tenant deployments keep working unchanged.
/// </summary>
public sealed class PowerPlatformOptions
{
    public string EnvironmentUrl { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Zero or more explicit tenant connections. When present these take precedence over the flat fields.</summary>
    public List<PowerPlatformConnection> Connections { get; set; } = new();

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(EnvironmentUrl) && !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public string Org => EnvironmentUrl.TrimEnd('/');
}

/// <summary>
/// Resolves the effective set of tenant connections from <see cref="PowerPlatformOptions"/> and lets
/// callers select one by key. Built once at startup.
/// </summary>
public sealed class PowerPlatformConnectionSet
{
    public IReadOnlyList<PowerPlatformConnection> Connections { get; }

    public PowerPlatformConnectionSet(PowerPlatformOptions o)
    {
        var list = new List<PowerPlatformConnection>();
        if (o.Connections is { Count: > 0 })
            list.AddRange(o.Connections);
        else if (o.IsConfigured)
            list.Add(new PowerPlatformConnection
            {
                Key = "primary",
                Label = string.IsNullOrWhiteSpace(o.EnvironmentName) ? "Primary tenant" : o.EnvironmentName,
                EnvironmentUrl = o.EnvironmentUrl, EnvironmentName = o.EnvironmentName,
                TenantId = o.TenantId, ClientId = o.ClientId, ClientSecret = o.ClientSecret,
            });
        Connections = list;
    }

    public PowerPlatformConnection? Primary => Connections.Count > 0 ? Connections[0] : null;

    /// <summary>Select a connection by key; null key returns the primary; an unknown key returns null.</summary>
    public PowerPlatformConnection? ByKey(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? Primary
            : Connections.FirstOrDefault(c => string.Equals(c.ResolvedKey, PowerPlatformConnection.Slug(key), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A Copilot Studio agent discovered in the connected environment.</summary>
public sealed class DiscoveredAgent
{
    public string BotId { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;       // Published | Draft
    public bool IsManaged { get; set; }
    public DateTimeOffset? CreatedOn { get; set; }
    public int TopicsCount { get; set; }
    public int ComponentsCount { get; set; }
    public string[] ConnectionReferences { get; set; } = Array.Empty<string>();
    public string AuthMethod { get; set; } = "Entra (via connector)";
    /// <summary>The agent's own Microsoft Entra Agent ID (app id), auto-detected from Entra; null if none.</summary>
    public string? EntraAgentId { get; set; }
    /// <summary>True once this agent is already in the AgentPermit registry.</summary>
    public bool AlreadyImported { get; set; }
}

public sealed class ConnectionStatus
{
    public string? Key { get; set; }
    public string? Label { get; set; }
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public string? EnvironmentUrl { get; set; }
    public string? EnvironmentName { get; set; }
    public string? OrganizationId { get; set; }
    public string? Message { get; set; }
}

public sealed class CopilotDiscoveryService(HttpClient http, TenantConnectionResolver resolver, ILogger<CopilotDiscoveryService> logger)
{
    // Token cache keyed by "<connectionKey>|<scope>" — tenants must never share tokens.
    private readonly Dictionary<string, (string token, DateTimeOffset exp)> _tokens = new();

    private async Task<string> TokenAsync(PowerPlatformConnection conn, string scope, CancellationToken ct)
    {
        var cacheKey = $"{conn.ResolvedKey}|{scope}";
        if (_tokens.TryGetValue(cacheKey, out var t) && DateTimeOffset.UtcNow < t.exp) return t.token;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = conn.ClientId,
            ["client_secret"] = conn.ClientSecret,
            ["scope"] = scope,
        });
        using var res = await http.PostAsync(
            $"https://login.microsoftonline.com/{conn.TenantId}/oauth2/v2.0/token", form, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        _tokens[cacheKey] = (token, DateTimeOffset.UtcNow.AddMinutes(45));
        return token;
    }

    private async Task<JsonElement> GetAsync(PowerPlatformConnection conn, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{conn.Org}/api/data/v9.2/{path}");
        req.Headers.Authorization = new("Bearer", await TokenAsync(conn, $"{conn.Org}/.default", ct));
        req.Headers.Add("OData-MaxVersion", "4.0");
        req.Headers.Add("OData-Version", "4.0");
        req.Headers.Add("Prefer", "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    /// <summary>Validate the connection for a specific tenant (null key = primary).</summary>
    public async Task<ConnectionStatus> ValidateAsync(string? key, CancellationToken ct)
    {
        var conn = await resolver.ByKeyAsync(key, ct);
        if (conn is null)
            return new ConnectionStatus { Key = key, Configured = false, Connected = false,
                Message = string.IsNullOrWhiteSpace(key) ? "No Power Platform tenant connected." : $"Tenant '{key}' is not connected." };
        return await TestConnectionAsync(conn, ct);
    }

    /// <summary>Validate an explicit connection (e.g. one an admin is about to save) without persisting it.</summary>
    public async Task<ConnectionStatus> TestConnectionAsync(PowerPlatformConnection conn, CancellationToken ct)
    {
        if (!conn.IsConfigured)
            return new ConnectionStatus { Key = conn.ResolvedKey, Label = conn.ResolvedLabel, Configured = false, Connected = false,
                Message = $"Tenant '{conn.ResolvedLabel}' is missing its connection details." };
        try
        {
            var who = await GetAsync(conn, "WhoAmI", ct);
            return new ConnectionStatus
            {
                Key = conn.ResolvedKey, Label = conn.ResolvedLabel,
                Configured = true, Connected = true,
                EnvironmentUrl = conn.Org, EnvironmentName = conn.EnvironmentName,
                OrganizationId = who.TryGetProperty("OrganizationId", out var o) ? o.GetString() : null,
                Message = "Connected."
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Power Platform connection validation failed for tenant {Tenant}.", conn.ResolvedKey);
            return new ConnectionStatus { Key = conn.ResolvedKey, Label = conn.ResolvedLabel, Configured = true, Connected = false, EnvironmentUrl = conn.Org, Message = FriendlyError(ex) };
        }
    }

    /// <summary>
    /// Fetch recent Copilot Studio conversation transcripts from Dataverse and parse them into compact,
    /// graph-friendly step lists. Optionally filter to conversations where a given AgentPermit agentId
    /// was governed. This is the reasoning layer of monitoring — no per-agent App Insights setup needed.
    /// </summary>
    public async Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(string? tenantKey, string? agentId, int take, CancellationToken ct)
    {
        var conn = await resolver.ByKeyAsync(tenantKey, ct);
        if (conn is null || !conn.IsConfigured) return Array.Empty<ConversationSummary>();

        take = Math.Clamp(take <= 0 ? 20 : take, 1, 100);
        var path = "conversationtranscripts"
            + "?$select=content,conversationstarttime,createdon,name,_bot_conversationtranscriptid_value"
            + $"&$orderby=createdon desc&$top={take}";
        JsonElement page;
        try { page = await GetAsync(conn, path, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not read conversation transcripts."); return Array.Empty<ConversationSummary>(); }

        var result = new List<ConversationSummary>();
        foreach (var row in page.GetProperty("value").EnumerateArray())
        {
            var id = row.TryGetProperty("conversationtranscriptid", out var cid) ? cid.GetString() ?? "" : "";
            var botName = row.TryGetProperty("_bot_conversationtranscriptid_value@OData.Community.Display.V1.FormattedValue", out var bn) ? bn.GetString() : null;
            var start = row.TryGetProperty("conversationstarttime", out var st) && st.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(st.GetString(), out var dt) ? dt : (DateTimeOffset?)null;
            var contentStr = row.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(contentStr)) continue;

            ConversationSummary summary;
            try { using var doc = JsonDocument.Parse(contentStr); summary = ConversationParser.Parse(id, botName, start, doc.RootElement); }
            catch { continue; }

            // Only surface conversations with governed activity. When an agent filter is given, keep matches
            // AND conversations whose agentId couldn't be parsed (older transcripts) rather than hiding them.
            if (!summary.HasGovernance) continue;
            if (!string.IsNullOrWhiteSpace(agentId) && summary.AgentId is not null
                && !string.Equals(summary.AgentId, agentId, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(summary);
        }
        return result;
    }

    // Turn raw HTTP failures into something an admin can act on.
    private static string FriendlyError(Exception ex)
    {
        var m = ex.Message;
        // A 400/401 from the token endpoint means the tenant id / client id / secret don't line up.
        if (m.Contains("400") || m.Contains("401") || m.Contains("invalid_client") || m.Contains("invalid_request") || m.Contains("AADSTS"))
            return "Authentication failed — check the tenant id, client id and client secret (and that the secret hasn’t expired).";
        if (m.Contains("403")) return "Authenticated, but not authorised — add this app as a Dataverse application user with a security role in that environment.";
        if (m.Contains("404")) return "Environment URL not found — check the Dataverse URL for this tenant.";
        return m;
    }

    /// <summary>Discover Copilot Studio agents (bots) in a specific tenant (null key = primary).</summary>
    public async Task<IReadOnlyList<DiscoveredAgent>> DiscoverAsync(string? key, CancellationToken ct)
    {
        var conn = await resolver.ByKeyAsync(key, ct);
        if (conn is null || !conn.IsConfigured) return Array.Empty<DiscoveredAgent>();

        var bots = await GetAsync(conn, "bots?$select=botid,name,schemaname,statecode,createdon,ismanaged,_ownerid_value&$orderby=name", ct);
        var connRefs = await SafeConnectionReferencesAsync(conn, ct);
        var identityMap = await SafeAgentIdentityMapAsync(conn, ct);

        var result = new List<DiscoveredAgent>();
        foreach (var b in bots.GetProperty("value").EnumerateArray())
        {
            var botId = b.GetProperty("botid").GetString()!;
            var name = b.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var comps = await SafeComponentCountsAsync(conn, botId, ct);
            result.Add(new DiscoveredAgent
            {
                BotId = botId,
                Name = name,
                SchemaName = b.TryGetProperty("schemaname", out var s) ? s.GetString() ?? "" : "",
                State = b.TryGetProperty("statecode", out var st) && st.GetInt32() == 0 ? "Published" : "Draft",
                IsManaged = b.TryGetProperty("ismanaged", out var im) && im.GetBoolean(),
                CreatedOn = b.TryGetProperty("createdon", out var c) && c.GetDateTimeOffset() is var dt ? dt : null,
                Owner = b.TryGetProperty("_ownerid_value@OData.Community.Display.V1.FormattedValue", out var ow) ? ow.GetString() ?? "" : "",
                TopicsCount = comps.topics,
                ComponentsCount = comps.total,
                ConnectionReferences = connRefs,
                EntraAgentId = identityMap.GetValueOrDefault(name.Trim().ToLowerInvariant()),
            });
        }
        return result;
    }

    private async Task<(int topics, int total)> SafeComponentCountsAsync(PowerPlatformConnection conn, string botId, CancellationToken ct)
    {
        try
        {
            var comps = await GetAsync(conn, $"botcomponents?$filter=_parentbotid_value eq {botId}&$select=componenttype", ct);
            var arr = comps.GetProperty("value");
            var total = arr.GetArrayLength();
            var topics = arr.EnumerateArray().Count(e => e.TryGetProperty("componenttype", out var t) && t.GetInt32() == 0);
            return (topics, total);
        }
        catch { return (0, 0); }
    }

    // Each Copilot Studio agent gets its own Microsoft Entra Agent ID (a service principal named
    // "<agent> (Microsoft Copilot Studio)"). Map agent name -> its Entra Agent ID (app id) via Graph.
    private async Task<Dictionary<string, string>> SafeAgentIdentityMapAsync(PowerPlatformConnection conn, CancellationToken ct)
    {
        const string suffix = " (Microsoft Copilot Studio)";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var url = "https://graph.microsoft.com/v1.0/servicePrincipals"
                + "?$filter=" + Uri.EscapeDataString("endswith(displayName,'(Microsoft Copilot Studio)')")
                + "&$select=displayName,appId&$count=true&$top=999";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new("Bearer", await TokenAsync(conn, "https://graph.microsoft.com/.default", ct));
            req.Headers.Add("ConsistencyLevel", "eventual");
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            foreach (var sp in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var dn = sp.TryGetProperty("displayName", out var d) ? d.GetString() : null;
                var appId = sp.TryGetProperty("appId", out var a) ? a.GetString() : null;
                if (dn is null || appId is null || !dn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var name = dn[..^suffix.Length].Trim();
                map[name] = appId;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read agent identities from Entra (Graph). Manual binding will be required.");
        }
        return map;
    }

    private async Task<string[]> SafeConnectionReferencesAsync(PowerPlatformConnection conn, CancellationToken ct)
    {
        try
        {
            var refs = await GetAsync(conn, "connectionreferences?$select=connectionreferencedisplayname", ct);
            return refs.GetProperty("value").EnumerateArray()
                .Select(e => e.TryGetProperty("connectionreferencedisplayname", out var d) ? d.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
}
