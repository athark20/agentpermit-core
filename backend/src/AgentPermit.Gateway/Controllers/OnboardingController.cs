using System.Text;
using System.Text.Json.Serialization;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Registry;
using AgentPermit.Gateway.Services;
using AgentPermit.Gateway.Services.PowerPlatform;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Onboarding: connect a Power Platform environment, discover Copilot Studio agents and import them
/// into the AgentPermit registry. Entra identity binding is MANDATORY on import — an agent cannot be
/// governed without a bound workload identity. All endpoints are admin-gated.
/// </summary>
[ApiController]
[Route("api/admin/onboarding")]
public sealed class OnboardingController(
    CopilotDiscoveryService discovery,
    TenantConnectionResolver tenantResolver,
    TenantConnectionStore tenantStore,
    IAgentStore agents,
    IAgentRegistry registry,
    AuditService audit,
    IConfiguration config,
    AdminAuthorizationService admin) : ControllerBase
{
    private bool Allowed() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbid403() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    /// <summary>List the tenant connections (config + admin-added) the admin can import from. No secrets, no network call.</summary>
    [HttpGet("tenants")]
    public async Task<IActionResult> Tenants(CancellationToken ct)
    {
        if (!Allowed()) return Forbid403();
        var list = (await tenantResolver.AllAsync(ct)).Select(t => new
        {
            key = t.Connection.ResolvedKey,
            label = t.Connection.ResolvedLabel,
            environmentName = t.Connection.EnvironmentName,
            environmentUrl = t.Connection.Org,
            configured = t.Connection.IsConfigured,
            removable = t.Removable,   // admin-added ones can be removed from the console
        });
        return Ok(list);
    }

    /// <summary>Test a tenant connection's details WITHOUT saving them (client-credentials auth + Dataverse WhoAmI).</summary>
    [HttpPost("tenants/test")]
    public async Task<IActionResult> TestTenant([FromBody] ConnectTenantRequest body, CancellationToken ct)
    {
        if (!Allowed()) return Forbid403();
        return Ok(await discovery.TestConnectionAsync(body.ToConnection(), ct));
    }

    /// <summary>Add (or update) an admin-supplied tenant connection. Tests it first; persists so it works immediately.</summary>
    [HttpPost("tenants")]
    public async Task<IActionResult> SaveTenant([FromBody] ConnectTenantRequest body, CancellationToken ct)
    {
        if (!Allowed()) return Forbid403();
        var conn = body.ToConnection();
        if (!conn.IsConfigured)
            throw new ValidationException("Tenant id, environment URL, client id and client secret are all required.") { Title = "Missing connection details" };

        var status = await discovery.TestConnectionAsync(conn, ct);
        if (!status.Connected)
            throw new ValidationException(status.Message ?? "The connection could not be reached.") { Title = "Connection test failed" };

        await tenantStore.SaveAsync(conn, ct);
        await audit.WriteAsync(HttpContext, "tenant.connect", "Tenant", conn.ResolvedKey,
            $"Connected tenant '{conn.ResolvedLabel}' · env {conn.Org} · tenantId {conn.TenantId} · appId {conn.ClientId}");
        return Ok(new { key = conn.ResolvedKey, label = conn.ResolvedLabel, status });
    }

    /// <summary>Remove an admin-added tenant connection (config-file tenants cannot be removed here).</summary>
    [HttpDelete("tenants/{key}")]
    public async Task<IActionResult> RemoveTenant(string key, CancellationToken ct)
    {
        if (!Allowed()) return Forbid403();
        if (!await tenantResolver.IsRemovableAsync(key, ct))
            throw new ValidationException("This tenant is set in the gateway configuration and can't be removed from the console.") { Title = "Can't remove this tenant" };
        var removed = await tenantStore.DeleteAsync(key, ct);
        if (removed) await audit.WriteAsync(HttpContext, "tenant.disconnect", "Tenant", PowerPlatformConnection.Slug(key), $"Removed tenant connection '{key}'");
        return Ok(new { removed });
    }

    /// <summary>Is a Power Platform tenant connected, and does the connection work? Pass ?tenant=key for a specific tenant.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] string? tenant, CancellationToken ct)
        => Allowed() ? Ok(await discovery.ValidateAsync(tenant, ct)) : Forbid403();

    /// <summary>Discover Copilot Studio agents in a tenant (?tenant=key; default = primary). Marks ones already imported.</summary>
    [HttpGet("agents")]
    public async Task<IActionResult> Agents([FromQuery] string? tenant, CancellationToken ct)
    {
        if (!Allowed()) return Forbid403();
        var found = await discovery.DiscoverAsync(tenant, ct);
        var existing = (await agents.ListAsync(ct)).Select(a => a.AgentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = found.Select(a =>
        {
            a.AlreadyImported = existing.Contains(Slug(a.Name));
            return new { suggestedAgentId = Slug(a.Name), agent = a };
        });
        return Ok(result);
    }

    /// <summary>
    /// Connection facts a maker needs to wire the AgentPermit connector in Copilot Studio: the APIM
    /// front-door base URL, the two operations, and the identity header. No secrets.
    /// </summary>
    [HttpGet("integration")]
    public IActionResult Integration()
    {
        if (!Allowed()) return Forbid403();
        // Prefer an explicit integration URL; otherwise reuse the APIM front-door base used for Teams links.
        var baseUrl = config["Integration:ConnectorBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = config["Teams:ApprovalsBaseUrl"];
        // App Insights connection string (ingestion/write-only) a maker pastes into Copilot Studio so the
        // agent's conversation telemetry lands in the SAME workspace as AgentPermit's decision spans.
        var aiConn = config["APPLICATIONINSIGHTS_CONNECTION_STRING"] ?? config["ApplicationInsights:ConnectionString"];
        return Ok(new
        {
            connectorBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/'),
            identityHeader = config["Security:IdentityHeader"] ?? "X-Agent-Client-Id",
            requestAction = new { method = "POST", path = "/api/actions/request" },
            getActionStatus = new { method = "GET", path = "/api/actions/{actionRequestId}" },
            appInsightsConnectionString = string.IsNullOrWhiteSpace(aiConn) ? null : aiConn,
        });
    }

    /// <summary>
    /// Recent Copilot Studio conversations (the agent's reasoning + tool calls, with AgentPermit calls
    /// flagged) parsed from Dataverse transcripts — no per-agent App Insights setup needed. Optional
    /// ?agentId= filter and ?tenant= key; ?take= caps results.
    /// </summary>
    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations([FromQuery] string? agentId, [FromQuery] string? tenant, [FromQuery] int take, CancellationToken ct)
    {
        if (!Allowed()) return Forbid403();
        var convos = await discovery.GetConversationsAsync(tenant, agentId, take, ct);
        return Ok(convos);
    }

    /// <summary>Import selected agents into the registry. Each MUST carry a bound Entra identity.</summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportRequest body, CancellationToken ct)
    {
        if (!Allowed()) return Forbid403();
        if (body.Agents is not { Count: > 0 })
            throw new ValidationException("Select at least one agent to import.") { Title = "No agents selected" };

        var imported = new List<string>();
        var rejected = new List<object>();
        foreach (var a in body.Agents)
        {
            var agentId = string.IsNullOrWhiteSpace(a.AgentId) ? Slug(a.Name) : a.AgentId.Trim();
            // MANDATORY Entra identity binding — refuse to govern an agent without a workload identity.
            if (string.IsNullOrWhiteSpace(a.EntraClientId))
            {
                rejected.Add(new { agentId, reason = "Entra identity binding is required." });
                continue;
            }
            await agents.UpsertAsync(new AgentInfo
            {
                AgentId = agentId,
                DisplayName = string.IsNullOrWhiteSpace(a.Name) ? agentId : a.Name,
                EntraClientId = a.EntraClientId.Trim(),
                Type = "Copilot",
                Owner = a.BusinessOwner,
                Environment = string.IsNullOrWhiteSpace(a.Environment) ? "copilot-studio" : a.Environment,
                RiskTier = string.IsNullOrWhiteSpace(a.RiskTier) ? "Medium" : a.RiskTier,
                Enabled = true,
            }, ct);
            await audit.WriteAsync(HttpContext, "agent.import", "Agent", agentId,
                $"Imported from Copilot Studio (bot {a.BotId}) · tenant {(string.IsNullOrWhiteSpace(a.Environment) ? "primary" : a.Environment)} · owner {a.BusinessOwner}/{a.TechnicalOwner} · risk {a.RiskTier} · bound {a.EntraClientId}");
            imported.Add(agentId);
        }
        registry.Invalidate();
        return Ok(new { imported, rejected });
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    public sealed class ImportRequest
    {
        [JsonPropertyName("agents")] public List<ImportAgent> Agents { get; set; } = new();
    }
    public sealed class ImportAgent
    {
        [JsonPropertyName("botId")] public string BotId { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("agentId")] public string? AgentId { get; set; }
        [JsonPropertyName("entraClientId")] public string? EntraClientId { get; set; }
        [JsonPropertyName("businessOwner")] public string? BusinessOwner { get; set; }
        [JsonPropertyName("technicalOwner")] public string? TechnicalOwner { get; set; }
        [JsonPropertyName("riskTier")] public string? RiskTier { get; set; }
        [JsonPropertyName("environment")] public string? Environment { get; set; }
    }

    /// <summary>Details an admin enters in the console to connect a tenant. The secret is used to test/save; never returned.</summary>
    public sealed class ConnectTenantRequest
    {
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
        [JsonPropertyName("environmentName")] public string? EnvironmentName { get; set; }
        [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
        [JsonPropertyName("clientId")] public string? ClientId { get; set; }
        [JsonPropertyName("clientSecret")] public string? ClientSecret { get; set; }

        public PowerPlatformConnection ToConnection() => new()
        {
            Key = (Key ?? "").Trim(),
            Label = (Label ?? "").Trim(),
            EnvironmentUrl = (EnvironmentUrl ?? "").Trim(),
            EnvironmentName = string.IsNullOrWhiteSpace(EnvironmentName) ? (Label ?? "").Trim() : EnvironmentName.Trim(),
            TenantId = (TenantId ?? "").Trim(),
            ClientId = (ClientId ?? "").Trim(),
            ClientSecret = (ClientSecret ?? "").Trim(),
        };
    }
}
