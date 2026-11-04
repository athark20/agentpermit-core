using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Boots the gateway in-memory. The execution service's HTTP call to the simulator
/// is routed back into this same TestServer, so the real authenticated HTTP hop
/// (execution service -> simulator, with the internal key) is exercised end to end.
/// A fresh instance per test gives isolated in-memory state.
/// </summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    public const string ExecutionKey = "test-execution-key";

    /// <summary>The Entra client id bound to the seeded copilot-refund-demo agent (appsettings.json).</summary>
    public const string BoundClientId = "11111111-1111-1111-1111-111111111111";

    /// <summary>Override the approval expiry window (e.g. -1 to make approvals born expired).</summary>
    public int? ApprovalExpiryHours { get; init; }

    /// <summary>Turn on strict identity binding + APIM-origin enforcement for a test.</summary>
    public bool EnforceIdentityBinding { get; init; }

    /// <summary>Shared secret used as the expected X-APIM-Origin value when enforcing.</summary>
    public string? ApimSharedSecret { get; init; }

    /// <summary>Replace the receipt signer (e.g. an in-memory RSA signer for verification tests).</summary>
    public AgentPermit.Gateway.Services.IReceiptSigner? SignerOverride { get; init; }

    /// <summary>Configure an admin API key (gates policy publishing).</summary>
    public string? AdminApiKey { get; init; }

    /// <summary>
    /// Turn on console sign-in (AdminAuth), so a person's Entra ID token is accepted as in production. The REAL
    /// JwtBearer handler runs; only its key material is swapped for <see cref="TestTokens"/>' key. Mint person
    /// tokens for audience <see cref="ConsoleAudience"/>.
    /// </summary>
    public bool ConsoleSignIn { get; init; }

    /// <summary>Audience of a person's console token when <see cref="ConsoleSignIn"/> is on.</summary>
    public const string ConsoleAudience = "api://agentpermit-console";

    /// <summary>Legacy: let the shared admin key approve or reject (Teams:AllowWorkflowDecisions).</summary>
    public bool AllowWorkflowDecisions { get; init; }

    /// <summary>Connector client ids trusted to carry a per-agent badge (X-Agent-Identity / body agentIdentity).</summary>
    public string[]? TrustedConnectorClientIds { get; init; }

    /// <summary>Lower the observations-to-promote threshold so rollout evidence tests don't need bulk traffic.</summary>
    public int? MinObservationsToPromote { get; init; }

    /// <summary>Lower the velocity circuit-breaker limit so the breaker trips within a short test.</summary>
    public int? CircuitBreakerMax { get; init; }

    /// <summary>Lower the deny-velocity breaker limit so a probing agent is quarantined within a short test.</summary>
    public int? DenyBreakerMax { get; init; }

    /// <summary>Turn automatic quarantine off (deny-velocity trips then deny only the current request).</summary>
    public bool? DenyBreakerAutoQuarantine { get; init; }

    /// <summary>Fail closed when a rule names an approver role for which no approver group is defined.</summary>
    public bool RequireNamedApproverGroups { get; init; }

    /// <summary>Mint signed DECISION receipts for DENY / REQUIRE_APPROVAL (default true).</summary>
    public bool? SignDecisions { get; init; }

    /// <summary>
    /// Turn on gateway-side agent-token validation. The REAL JwtBearer handler runs; only its key material is
    /// swapped for <see cref="TestTokens"/>' symmetric key, so no OIDC discovery is attempted.
    /// </summary>
    public bool AgentAuth { get; init; }

    /// <summary>Deny action requests that carry no valid agent token (implies <see cref="AgentAuth"/>).</summary>
    public bool AgentAuthRequired { get; init; }

    /// <summary>Stop honouring a body/header-asserted agent badge (one connection per agent).</summary>
    public bool? AllowBodyAssertedAgentIdentity { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Persistence:Provider", "InMemory");
        builder.UseSetting("Simulator:ExecutionKey", ExecutionKey);
        builder.UseSetting("Simulator:BaseUrl", "http://localhost");
        // Isolate the policy publish store per test so published snapshots don't leak between tests.
        builder.UseSetting("Policy:PublishDir",
            Path.Combine(Path.GetTempPath(), "agentpermit-test-" + Guid.NewGuid().ToString("N")));
        if (ApprovalExpiryHours is { } hours)
            builder.UseSetting("Approval:ExpiryHours", hours.ToString());
        if (EnforceIdentityBinding)
            builder.UseSetting("Security:EnforceIdentityBinding", "true");
        if (ApimSharedSecret is not null)
            builder.UseSetting("Security:ApimSharedSecret", ApimSharedSecret);
        if (AdminApiKey is not null)
            builder.UseSetting("Admin:ApiKey", AdminApiKey);
        if (ConsoleSignIn)
        {
            builder.UseSetting("AdminAuth:Authority", TestTokens.Authority);
            builder.UseSetting("AdminAuth:Audience", ConsoleAudience);
        }
        if (AllowWorkflowDecisions)
            builder.UseSetting("Teams:AllowWorkflowDecisions", "true");
        if (TrustedConnectorClientIds is not null)
            for (var i = 0; i < TrustedConnectorClientIds.Length; i++)
                builder.UseSetting($"Security:TrustedConnectorClientIds:{i}", TrustedConnectorClientIds[i]);
        if (MinObservationsToPromote is { } min)
            builder.UseSetting("Rollout:MinObservationsToPromote", min.ToString());
        if (CircuitBreakerMax is { } cb)
            builder.UseSetting("CircuitBreaker:MaxExecutionsPerWindow", cb.ToString());
        if (DenyBreakerMax is { } db)
            builder.UseSetting("DenyBreaker:MaxDenialsPerWindow", db.ToString());
        if (DenyBreakerAutoQuarantine is { } aq)
            builder.UseSetting("DenyBreaker:AutoQuarantine", aq ? "true" : "false");
        if (RequireNamedApproverGroups)
            builder.UseSetting("Approval:RequireNamedApproverGroups", "true");
        if (SignDecisions is { } sd)
            builder.UseSetting("Receipts:SignDecisions", sd ? "true" : "false");
        if (AllowBodyAssertedAgentIdentity is { } allowBadge)
            builder.UseSetting("Security:AllowBodyAssertedAgentIdentity", allowBadge ? "true" : "false");
        if (AgentAuth || AgentAuthRequired)
        {
            builder.UseSetting("Security:AgentAuth:Authority", TestTokens.Authority);
            builder.UseSetting("Security:AgentAuth:Audience", TestTokens.Audience);
            if (AgentAuthRequired) builder.UseSetting("Security:AgentAuth:Required", "true");
        }

        builder.ConfigureTestServices(services =>
        {
            // Re-register the typed simulator client so its HTTP calls hit the TestServer.
            services.AddHttpClient<ISimulatorClient, HttpSimulatorClient>((_, client) =>
                    client.BaseAddress = new Uri("http://localhost"))
                .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());

            if (SignerOverride is not null)
                services.AddSingleton(SignerOverride);

            // Keep the real JwtBearer handler (so claim extraction, audience/issuer/lifetime checks and the
            // failure path are all genuinely exercised) but validate against a local symmetric key instead of
            // reaching Entra for discovery.
            if (AgentAuth || AgentAuthRequired)
                services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                    AgentPermit.Gateway.Services.AgentAuthOptions.Scheme, o =>
                    {
                        o.Authority = null;
                        o.MetadataAddress = null;
                        o.RequireHttpsMetadata = false;
                        o.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
                        o.TokenValidationParameters.ValidateIssuerSigningKey = true;
                        o.TokenValidationParameters.ValidIssuer = TestTokens.Issuer;
                        o.TokenValidationParameters.ValidateIssuer = true;
                        o.TokenValidationParameters.ValidAudience = TestTokens.Audience;
                        o.TokenValidationParameters.ValidateAudience = true;
                        o.TokenValidationParameters.ValidateLifetime = true;
                        o.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(5);
                    });

            // Same for the console (person) scheme. Dropping the configuration manager means no OIDC discovery
            // is attempted whichever order the post-configure steps run in.
            if (ConsoleSignIn)
                services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                    Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme, o =>
                    {
                        o.Authority = null;
                        o.MetadataAddress = null;
                        o.ConfigurationManager = null;
                        o.RequireHttpsMetadata = false;
                        o.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
                        o.TokenValidationParameters.ValidateIssuerSigningKey = true;
                        o.TokenValidationParameters.ValidIssuer = TestTokens.Issuer;
                        o.TokenValidationParameters.ValidAudience = ConsoleAudience;
                        o.TokenValidationParameters.ValidateLifetime = true;
                    });
        });
    }
}
