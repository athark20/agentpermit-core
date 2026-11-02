using AgentPermit.Gateway.Adapters;
using AgentPermit.Gateway.ControlPlane;
using AgentPermit.Gateway.Health;
using AgentPermit.Gateway.Observations;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Policies;
using AgentPermit.Gateway.Registry;
using AgentPermit.Gateway.Services;
using AgentPermit.Gateway.Simulator;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// --- OpenTelemetry → Azure Monitor (App Insights): auto-captures request/dependency traces and, via
// BuildResponse, tags each request with the governance decision (decision/reasonCode/matchedRule/…).
// W3C trace context is honoured by default, so AgentPermit spans stitch into the Copilot Studio
// conversation's trace when the connector forwards traceparent. No-op when the connection string is unset. ---
var appInsightsConn = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    ?? builder.Configuration["ApplicationInsights:ConnectionString"];
if (!string.IsNullOrWhiteSpace(appInsightsConn))
    builder.Services.AddOpenTelemetry().UseAzureMonitor(o => o.ConnectionString = appInsightsConn);

// --- Single, consistent RFC 7807 error contract for ALL API errors ---
// ProblemDetails is the base for both the [ApiController] auto-400s and our GlobalExceptionHandler.
// Every response gets a traceId so the frontend can quote a support reference.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions.TryAdd("traceId",
            System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);
    };
});
// Converts UNHANDLED exceptions into problem+json (domain exceptions -> precise reason; anything
// else -> safe generic 500). Never weakens existing controller Problem(...)/status-code behaviour.
builder.Services.AddExceptionHandler<AgentPermit.Gateway.Middleware.GlobalExceptionHandler>();

// --- Operational health checks ---
builder.Services.AddHealthChecks()
    .AddCheck<PolicySnapshotHealthCheck>("policy-snapshot", tags: new[] { "ready" })
    .AddCheck<AdaptersHealthCheck>("adapters", tags: new[] { "ready" })
    .AddCheck<SigningHealthCheck>("receipt-signing", tags: new[] { "deps" })
    .AddCheck<SqlDependencyHealthCheck>("sql", tags: new[] { "deps" });

// --- Options bound from configuration ---
builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SimulatorOptions>>().Value);

var approvalOptions = builder.Configuration.GetSection("Approval").Get<ApprovalOptions>() ?? new ApprovalOptions();
builder.Services.AddSingleton(approvalOptions);

var policySnapshotOptions = builder.Configuration.GetSection("Policy:Snapshot").Get<PolicySnapshotOptions>() ?? new PolicySnapshotOptions();
builder.Services.AddSingleton(policySnapshotOptions);

var teamsOptions = builder.Configuration.GetSection("Teams").Get<TeamsOptions>() ?? new TeamsOptions();
builder.Services.AddSingleton(teamsOptions);

var securityOptions = builder.Configuration.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();
builder.Services.AddSingleton(securityOptions);

var adminOptions = builder.Configuration.GetSection("Admin").Get<AdminOptions>() ?? new AdminOptions();
builder.Services.AddSingleton(adminOptions);

// --- Tier/entitlement enforcement: a signed licence → what this deployment may do. Enforce=false (dev) = all on.
// Never disables enforcement of existing policy; a lapsed licence only locks management. ---
var licenseOptions = builder.Configuration.GetSection("License").Get<AgentPermit.Gateway.Licensing.LicenseOptions>()
    ?? new AgentPermit.Gateway.Licensing.LicenseOptions();
builder.Services.AddSingleton(licenseOptions);
builder.Services.AddSingleton<AgentPermit.Gateway.Licensing.LicenseService>(sp =>
    new AgentPermit.Gateway.Licensing.LicenseService(licenseOptions,
        sp.GetRequiredService<IConfigStore>(),
        sp.GetRequiredService<ILogger<AgentPermit.Gateway.Licensing.LicenseService>>()));
builder.Services.AddSingleton<AgentPermit.Gateway.Licensing.IEntitlementsProvider>(sp =>
    sp.GetRequiredService<AgentPermit.Gateway.Licensing.LicenseService>());

// --- Admin control-plane auth: Entra ID (RBAC via group/app-role) fronting our own admin UI ---
var adminAuthOptions = builder.Configuration.GetSection("AdminAuth").Get<AdminAuthOptions>() ?? new AdminAuthOptions();
builder.Services.AddSingleton(adminAuthOptions);
builder.Services.AddSingleton<AccessDirectoryService>();   // persona resolution (Roles & access → who can do what)
builder.Services.AddSingleton<ApproverGroupDirectory>();   // named approver groups (who may sign off on WHAT)
builder.Services.AddSingleton<AdminAuthorizationService>();
if (adminAuthOptions.IsConfigured)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = adminAuthOptions.Authority;
            options.Audience = adminAuthOptions.Audience;
            // Keep original AAD claim names ("roles", "groups") instead of remapping to legacy URIs.
            options.MapInboundClaims = false;
            var aud = adminAuthOptions.Audience!;
            options.TokenValidationParameters.ValidAudiences = aud.StartsWith("api://", StringComparison.Ordinal)
                ? new[] { aud, aud["api://".Length..] }
                : new[] { aud, $"api://{aud}" };
            var issuers = adminAuthOptions.ValidIssuers();
            if (issuers.Length > 0) options.TokenValidationParameters.ValidIssuers = issuers;
            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = ctx =>
                {
                    ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("AdminJwtBearer").LogWarning(ctx.Exception, "Admin token validation failed");
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization();
}
if (adminAuthOptions.AllowedCorsOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddPolicy("admin-ui", p =>
        p.WithOrigins(adminAuthOptions.AllowedCorsOrigins).AllowAnyHeader().AllowAnyMethod()));
}

// --- Registries (seeded from configuration; backed by Dataverse in production) ---
var agentOptions = builder.Configuration.GetSection("Registry").Get<AgentRegistryOptions>() ?? new AgentRegistryOptions();
builder.Services.AddSingleton(agentOptions);
// Agent Registry is data now: a store (SQL/InMemory) with a cached read-through registry. Configured
// agents seed the store when it is empty (see post-build seed), so config still bootstraps a fresh env.
builder.Services.AddSingleton<IAgentRegistry, CachedAgentRegistry>();

var actionOptions = builder.Configuration.GetSection("Registry").Get<ActionRegistryOptions>() ?? new ActionRegistryOptions();
builder.Services.AddSingleton(actionOptions);
builder.Services.AddSingleton<IActionRegistry>(sp =>
    new FileActionRegistry(sp.GetRequiredService<ActionRegistryOptions>(), AppContext.BaseDirectory));

// --- Policy engine: seed snapshot + a runtime-PUBLISHABLE, signed, immutable snapshot the engine enforces ---
// Durable store: Azure Blob (survives restarts) when configured, else local file (dev).
var policyBlobUri = builder.Configuration["Policy:PublishBlobUri"];
if (!string.IsNullOrWhiteSpace(policyBlobUri))
{
    builder.Services.AddSingleton<IPolicySnapshotStore>(new BlobPolicySnapshotStore(policyBlobUri));
}
else
{
    var policyPublishDir = builder.Configuration["Policy:PublishDir"] ?? Path.Combine(AppContext.BaseDirectory, "published");
    builder.Services.AddSingleton<IPolicySnapshotStore>(new FilePolicySnapshotStore(policyPublishDir));
}
builder.Services.AddSingleton<IPublishablePolicySnapshotProvider>(sp => new PublishablePolicySnapshotProvider(
    sp.GetRequiredService<IPolicySnapshotStore>(),
    FilePolicySnapshotProvider.LoadFromFile(sp.GetRequiredService<PolicySnapshotOptions>(), AppContext.BaseDirectory),
    sp.GetRequiredService<IReceiptSigner>(),
    sp.GetRequiredService<ILogger<PublishablePolicySnapshotProvider>>()));
builder.Services.AddSingleton<IPolicySnapshotProvider>(sp => sp.GetRequiredService<IPublishablePolicySnapshotProvider>());
builder.Services.AddSingleton<IPolicyPublisher, PolicyPublisher>();
builder.Services.AddSingleton<IPolicyEngine, ConfigurablePolicyEngine>();
// Applies each agent's rollout posture (OBSERVE/WARN/ENFORCE) to the computed decision, enforcing the
// invariant that HIGH/CRITICAL-consequence actions are always binding regardless of warm-up.
builder.Services.AddSingleton<EnforcementGate>();
// Import-time consequence classification of target systems (danger is known up front).
builder.Services.AddSingleton<RiskClassifier>();
// Versioned, data-driven connector risk catalog (connector -> consequence tier + categories).
builder.Services.AddSingleton(new ConnectorRiskCatalog(
    Path.Combine(AppContext.BaseDirectory, "seed", "connector-risk-catalog.json")));
// DLP-as-code generator: connectors + risk catalog -> a reviewable Power Platform DLP policy.
builder.Services.AddSingleton(builder.Configuration.GetSection("Dlp").Get<DlpOptions>() ?? new DlpOptions());
builder.Services.AddSingleton<DlpPolicyGenerator>();
// Auto-promotion: graduates warming-up agents when the evidence-based recommendation says it's safe.
// Off by default (explicit opt-in); only ever increases enforcement.
builder.Services.AddSingleton(builder.Configuration.GetSection("AutoPromote").Get<AutoPromotionOptions>() ?? new AutoPromotionOptions());
builder.Services.AddHostedService<AutoPromotionService>();
// Rollout controller: drives agents along OBSERVE -> WARN -> ENFORCE and flags stale warm-ups (drift).
var rolloutOptions = builder.Configuration.GetSection("Rollout").Get<RolloutOptions>() ?? new RolloutOptions();
builder.Services.AddSingleton(rolloutOptions);
builder.Services.AddSingleton<RolloutController>();

// Observation feeds: the gateway push source is always on (free); external pull feeds (Purview audit,
// Flow run history) are OFF by default and enabled per customer, so no external cost is incurred implicitly.
var feedOptions = builder.Configuration.GetSection("Observations").Get<ObservationFeedOptions>() ?? new ObservationFeedOptions();
builder.Services.AddSingleton(feedOptions);
builder.Services.AddSingleton(feedOptions.Purview);
builder.Services.AddSingleton(feedOptions.FlowRun);
builder.Services.AddSingleton<FeedStatusTracker>();
builder.Services.AddSingleton<ObservationCursorStore>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IObservationFeed, PurviewAuditFeed>();
builder.Services.AddSingleton<IObservationFeed, FlowRunFeed>();
builder.Services.AddHostedService<ObservationIngestionService>();

// Always-on safety net: global kill switch (emergency freeze) + per-agent velocity circuit breaker.
// Independent of policy and warm-up — a runaway or an emergency stop is a hard deny.
var breakerOptions = builder.Configuration.GetSection("CircuitBreaker").Get<CircuitBreakerOptions>() ?? new CircuitBreakerOptions();
var killSwitchOptions = builder.Configuration.GetSection("KillSwitch").Get<KillSwitchOptions>() ?? new KillSwitchOptions();
// Deny-velocity breaker + per-agent quarantine: an agent refused too often in a short window is probing a
// block, not doing business. Trip -> sticky quarantine (AGENT_QUARANTINED) until an admin lifts it.
var denyBreakerOptions = builder.Configuration.GetSection("DenyBreaker").Get<DenyBreakerOptions>() ?? new DenyBreakerOptions();
builder.Services.AddSingleton(breakerOptions);
builder.Services.AddSingleton(killSwitchOptions);
builder.Services.AddSingleton(denyBreakerOptions);
builder.Services.AddSingleton<KillSwitchService>();
builder.Services.AddSingleton<QuarantineService>();
builder.Services.AddSingleton<CircuitBreaker>();
builder.Services.AddSingleton<DenyVelocityBreaker>();
builder.Services.AddSingleton<SafetyGate>();

// --- Persistence: in-memory for local/smoke, Azure SQL when configured ---
var provider = builder.Configuration["Persistence:Provider"] ?? "InMemory";
if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration["Persistence:SqlConnectionString"]
        ?? throw new InvalidOperationException("Persistence:SqlConnectionString is required when Provider=SqlServer.");
    builder.Services.AddSingleton<IAgentPermitStore>(new SqlAgentPermitStore(connectionString));
    builder.Services.AddSingleton<ITargetSystemStore>(new SqlTargetSystemStore(connectionString));
    builder.Services.AddSingleton<IAgentStore>(new SqlAgentStore(connectionString));
    builder.Services.AddSingleton<IAuditStore>(new SqlAuditStore(connectionString));
    builder.Services.AddSingleton<IConfigStore>(new SqlConfigStore(connectionString));
    builder.Services.AddSingleton<IObservationStore>(new SqlObservationStore(connectionString));
}
else
{
    builder.Services.AddSingleton<IAgentPermitStore, InMemoryAgentPermitStore>();
    builder.Services.AddSingleton<ITargetSystemStore, InMemoryTargetSystemStore>();
    builder.Services.AddSingleton<IAgentStore, InMemoryAgentStore>();
    builder.Services.AddSingleton<IAuditStore, InMemoryAuditStore>();
    builder.Services.AddSingleton<IConfigStore, InMemoryConfigStore>();
    builder.Services.AddSingleton<IObservationStore, InMemoryObservationStore>();
}
builder.Services.AddSingleton<AuditService>();

// --- Power Platform / Copilot Studio discovery (onboarding) ---
var ppOptions = builder.Configuration.GetSection("PowerPlatform").Get<AgentPermit.Gateway.Services.PowerPlatform.PowerPlatformOptions>()
    ?? new AgentPermit.Gateway.Services.PowerPlatform.PowerPlatformOptions();
builder.Services.AddSingleton(ppOptions);
// Effective set of tenant connections (one flat connection, or several under PowerPlatform:Connections),
// merged at call time with admin-added connections persisted in the config store.
builder.Services.AddSingleton<AgentPermit.Gateway.Services.PowerPlatform.PowerPlatformConnectionSet>();
builder.Services.AddSingleton<AgentPermit.Gateway.Services.PowerPlatform.TenantConnectionStore>();
builder.Services.AddSingleton<AgentPermit.Gateway.Services.PowerPlatform.TenantConnectionResolver>();
builder.Services.AddHttpClient<AgentPermit.Gateway.Services.PowerPlatform.CopilotDiscoveryService>();

// --- MCP (Model Context Protocol) front-door: exposes governed actions as MCP tools over JSON-RPC.
// tools/call routes through the same ActionRequestService pipeline (policy/identity/idempotency/receipts). ---
builder.Services.AddSingleton<AgentPermit.Gateway.Mcp.McpToolCatalog>();

// --- Roles & access: push AgentPermit roles OUT to Entra security groups (one-way, gated on consent) ---
var entraProvisioningOptions = builder.Configuration.GetSection("EntraProvisioning").Get<EntraProvisioningOptions>() ?? new EntraProvisioningOptions();
builder.Services.AddSingleton(entraProvisioningOptions);
builder.Services.AddSingleton<EntraProvisioningService>();

// Target System Registry (cached read-through over the store).
builder.Services.AddSingleton<ITargetSystemRegistry, CachedTargetSystemRegistry>();

// --- Simulated target system storage (always in-memory; it stands in for an external system) ---
builder.Services.AddSingleton<IRefundLedger, InMemoryRefundLedger>();

// --- Receipt signing: Key Vault asymmetric key when configured, else unsigned ---
var receiptOptions = builder.Configuration.GetSection("Receipts").Get<ReceiptOptions>() ?? new ReceiptOptions();
builder.Services.AddSingleton(receiptOptions);
if (!string.IsNullOrWhiteSpace(receiptOptions.SigningKeyId))
    builder.Services.AddSingleton<IReceiptSigner>(new KeyVaultReceiptSigner(receiptOptions.SigningKeyId));
else
    builder.Services.AddSingleton<IReceiptSigner, NullReceiptSigner>();

// --- Target-system adapters (pluggable execution; connection config comes from the Target System Registry) ---
builder.Services.AddScoped<IActionAdapter, SimulatorRefundAdapter>();
builder.Services.AddHttpClient<RestActionAdapter>();
builder.Services.AddHttpClient<DelegatedActionAdapter>();
builder.Services.AddScoped<IActionAdapter>(sp => sp.GetRequiredService<RestActionAdapter>());
builder.Services.AddScoped<IActionAdapter>(sp => sp.GetRequiredService<DelegatedActionAdapter>());
// Finance/AP business system (real Azure SQL). Serves the FINANCE_READ + FINANCE_AP targets; holds the
// only downstream DB credential. Registered even when the connection is unset (returns a clear
// TARGET_NOT_CONFIGURED at execute time) so the adapter always resolves.
var businessSqlConn = builder.Configuration["Persistence:BusinessSqlConnectionString"];
builder.Services.AddScoped<IActionAdapter>(sp => new SqlFinanceAdapter(businessSqlConn, sp.GetRequiredService<ILogger<SqlFinanceAdapter>>()));
builder.Services.AddScoped<IActionAdapterResolver, ActionAdapterResolver>();

// --- Core services ---
builder.Services.AddScoped<ReceiptService>();
builder.Services.AddScoped<ExecutionService>();
builder.Services.AddScoped<ActionRequestService>();
builder.Services.AddScoped<ApprovalService>();

// --- Teams Adaptive Card notifier (best-effort; no-op when webhook not configured) ---
builder.Services.AddHttpClient<ITeamsNotifier, HttpTeamsNotifier>();

// --- Typed HTTP client the execution service uses to reach the simulator ---
builder.Services.AddHttpClient<ISimulatorClient, HttpSimulatorClient>((sp, client) =>
{
    var opt = sp.GetRequiredService<IOptions<SimulatorOptions>>().Value;
    client.BaseAddress = new Uri(opt.BaseUrl);
});

var app = builder.Build();

// Global error handling FIRST so every downstream fault (auth, cors, controllers) is rendered as a
// single consistent problem+json contract. UseStatusCodePages ensures raw 401/403/404 with no body
// (e.g. from auth middleware) still return a problem+json shape.
app.UseExceptionHandler();
app.UseStatusCodePages();

// Seed the Target System Registry with the built-in refund simulator when empty (best-effort).
try
{
    var tsStore = app.Services.GetRequiredService<ITargetSystemStore>();
    if ((await tsStore.ListAsync(CancellationToken.None)).Count == 0)
    {
        await tsStore.UpsertAsync(new TargetSystemRecord
        {
            TargetSystemCode = "REFUND_SIMULATOR",
            DisplayName = "Refund Simulator",
            AdapterType = "REFUND_SIMULATOR",
            AuthMode = "Internal",
            ConsequenceTier = "HIGH", // financial target — money movement is always enforced
            Enabled = true,
            AllowedActions = new[] { "Refund.Issue" }
        }, CancellationToken.None);
        app.Services.GetRequiredService<ITargetSystemRegistry>().Invalidate();
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Target system seed skipped (store not reachable at startup).");
}

// Seed the Agent Registry from configured agents when the store is empty (bootstraps a fresh env).
try
{
    var agentStore = app.Services.GetRequiredService<IAgentStore>();
    if ((await agentStore.ListAsync(CancellationToken.None)).Count == 0 && agentOptions.Agents.Count > 0)
    {
        foreach (var a in agentOptions.Agents)
            await agentStore.UpsertAsync(a, CancellationToken.None);
        app.Services.GetRequiredService<IAgentRegistry>().Invalidate();
        app.Logger.LogInformation("Seeded {Count} agent(s) into the registry from configuration.", agentOptions.Agents.Count);
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Agent seed skipped (store not reachable at startup).");
}

// Seed control-plane config defaults (environments, retention, platform settings) when empty.
try
{
    var cfg = app.Services.GetRequiredService<IConfigStore>();
    async Task SeedAsync(string type, params (string id, object value)[] items)
    {
        if ((await cfg.ListAsync(type, CancellationToken.None)).Count != 0) return;
        foreach (var (id, value) in items)
            await cfg.UpsertAsync(type, id, System.Text.Json.JsonSerializer.Serialize(value), CancellationToken.None);
    }
    await SeedAsync("environment",
        ("DEV", new { id = "DEV", displayName = "Development", description = "Cheap dev profile", isProduction = false, ordinal = 1 }),
        ("UAT", new { id = "UAT", displayName = "User Acceptance", description = "Pre-production validation", isProduction = false, ordinal = 2 }),
        ("PROD", new { id = "PROD", displayName = "Production", description = "Live agent actions", isProduction = true, ordinal = 3 }));
    await SeedAsync("retention-policy",
        ("action-requests", new { id = "action-requests", dataClass = "Action Requests", retentionDays = 365, legalHold = false }),
        ("receipts", new { id = "receipts", dataClass = "Receipts (evidence)", retentionDays = 2555, legalHold = false }),
        ("audit-events", new { id = "audit-events", dataClass = "Audit Events", retentionDays = 2555, legalHold = false }),
        ("approvals", new { id = "approvals", dataClass = "Approvals", retentionDays = 365, legalHold = false }));
    await SeedAsync("platform-setting",
        ("customer-name", new { id = "customer-name", key = "Customer name", value = "AgentPermit DEV" }),
        ("time-zone", new { id = "time-zone", key = "Time zone", value = "UTC" }),
        ("teams-notifications", new { id = "teams-notifications", key = "Teams approval notifications", value = "Configured via Teams webhook" }));
    await SeedAsync("alert-rule",
        ("approval-backlog", new { id = "approval-backlog", name = "Approval backlog high", metric = "approvalBacklog", @operator = ">", threshold = 5, severity = "Warning", enabled = true }),
        ("execution-failures", new { id = "execution-failures", name = "Execution failures present", metric = "executionFailures", @operator = ">", threshold = 0, severity = "Warning", enabled = true }),
        ("unhealthy-targets", new { id = "unhealthy-targets", name = "Target system unavailable", metric = "unhealthyTargets", @operator = ">", threshold = 0, severity = "Critical", enabled = true }));
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Config seed skipped (store not reachable at startup).");
}

// Load any hot-applied licence persisted in the config store (overrides the bootstrap config token).
try { await app.Services.GetRequiredService<AgentPermit.Gateway.Licensing.LicenseService>().InitAsync(CancellationToken.None); }
catch (Exception ex) { app.Logger.LogWarning(ex, "Licence init skipped."); }

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (adminAuthOptions.AllowedCorsOrigins.Length > 0) app.UseCors("admin-ui");
if (adminAuthOptions.IsConfigured)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapControllers();

// Liveness (process up), readiness (snapshot + adapters loaded), and detailed dependency report.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapHealthChecks("/health/deps", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => true, ResponseWriter = HealthReport.Write });

app.Run();

// Exposed so the test project's WebApplicationFactory<Program> can host the app.
public partial class Program;
