using System.Text.Json;
using AgentPermit.Gateway.ControlPlane;
using AgentPermit.Gateway.Adapters;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Operations + governance read surface for the admin console: exceptions, integration health,
/// delegation chains, security posture, audit trail, alerts and the dead-letter queue. Entra-gated.
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminOpsController(
    IAgentPermitStore store,
    ITargetSystemRegistry targets,
    IActionAdapterResolver adapters,
    IAuditStore audit,
    IConfigStore config,
    AuditService auditWriter,
    SecurityOptions security,
    AdminAuthOptions adminAuth,
    IReceiptSigner signer,
    AdminAuthorizationService auth) : ControllerBase
{
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    private IActionResult? Guard()
        => auth.IsAuthorized(HttpContext) ? null
            : Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    // Read-only surfaces are open to any workspace member; writes keep the admin Guard().
    private async Task<IActionResult?> MemberGuardAsync(CancellationToken ct)
        => await auth.IsMemberAsync(HttpContext, ct) ? null
            : Problem(title: "Workspace access required", detail: "Ask your administrator for a role in AgentPermit.",
                statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Requests and executions that need attention: failures and errors.</summary>
    [HttpGet("exceptions")]
    public async Task<IActionResult> Exceptions([FromQuery] int take, CancellationToken ct)
    {
        if (await MemberGuardAsync(ct) is { } forbidden) return forbidden;
        var n = take <= 0 ? 200 : take;
        var execs = await store.ListRecentExecutionsAsync(n, ct);
        var reqs = await store.ListRecentActionRequestsAsync(n, ct);

        var items = execs
            .Where(e => !string.Equals(e.Status, "Succeeded", StringComparison.OrdinalIgnoreCase)
                        && (e.ErrorCode != null || e.Status == "Failed"))
            .Select(e => new ExceptionItem
            {
                Kind = "ExecutionFailure", OccurredUtc = e.CompletedUtc ?? e.StartedUtc,
                Reference = e.ActionRequestId.ToString(), Detail = e.AdapterCode,
                Code = e.ErrorCode, Message = e.ErrorMessage
            })
            .Concat(reqs
                .Where(r => MetricsBuilder.Canonical(r.Status) == "FAILED")
                .Select(r => new ExceptionItem
                {
                    Kind = "RequestFailure", OccurredUtc = r.UpdatedUtc,
                    Reference = r.ActionRequestId.ToString(), Detail = r.ActionId,
                    Code = "FAILED", Message = null
                }))
            .OrderByDescending(i => i.OccurredUtc)
            .Take(n)
            .ToList();

        return Ok(items);
    }

    /// <summary>Live health of each target system's adapter.</summary>
    [HttpGet("integrations")]
    public async Task<IActionResult> Integrations(CancellationToken ct)
    {
        if (await MemberGuardAsync(ct) is { } forbidden) return forbidden;
        var results = new List<object>();
        foreach (var t in targets.All())
        {
            bool healthy; string? message;
            if (adapters.TryResolve(t.AdapterType, out var adapter))
            {
                try { var h = await adapter.CheckHealthAsync(ct); healthy = h.Healthy; message = h.Message; }
                catch (Exception ex) { healthy = false; message = ex.Message; }
            }
            else { healthy = false; message = $"No adapter registered for type '{t.AdapterType}'."; }

            results.Add(new
            {
                t.TargetSystemCode, t.DisplayName, t.AdapterType, t.Enabled,
                status = !t.Enabled ? "Disabled" : healthy ? "Healthy" : "Unavailable",
                message, checkedAtUtc = DateTimeOffset.UtcNow
            });
        }
        return Ok(results);
    }

    /// <summary>Delegation relationships derived from real requests (root → executing agent).</summary>
    [HttpGet("delegation")]
    public async Task<IActionResult> Delegation(CancellationToken ct)
    {
        if (await MemberGuardAsync(ct) is { } forbidden) return forbidden;
        var reqs = await store.ListRecentActionRequestsAsync(1000, ct);
        var edges = reqs
            .Where(r => !string.IsNullOrWhiteSpace(r.RootAgentId)
                        && !string.Equals(r.RootAgentId, r.ExecutingAgentId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => (Root: r.RootAgentId!, Executing: r.ExecutingAgentId))
            .Select(g => new
            {
                rootAgentId = g.Key.Root,
                executingAgentId = g.Key.Executing,
                requests = g.Count(),
                lastSeenUtc = g.Max(r => r.CreatedUtc),
                escalation = true // executing differs from root — flag for review
            })
            .OrderByDescending(e => e.lastSeenUtc)
            .ToList();
        return Ok(edges);
    }

    /// <summary>Current security posture of the control plane (read-only view).</summary>
    [HttpGet("security")]
    public async Task<IActionResult> Security(CancellationToken ct)
        => await MemberGuardAsync(ct) ?? Ok(new
        {
            identityBindingEnforced = security.EnforceIdentityBinding,
            identityHeader = security.IdentityHeader,
            apimOriginHeader = security.ApimOriginHeader,
            apimOriginSecretConfigured = !string.IsNullOrEmpty(security.ApimSharedSecret),
            receiptSigningEnabled = signer.Enabled,
            adminEntraConfigured = adminAuth.IsConfigured,
            adminAppRole = adminAuth.AdminAppRole,
            adminGroupConfigured = !string.IsNullOrWhiteSpace(adminAuth.AdminGroupId),
            allowedCorsOrigins = adminAuth.AllowedCorsOrigins,
        });

    /// <summary>The administrative audit trail (newest first).</summary>
    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] int take, CancellationToken ct)
    {
        if (await MemberGuardAsync(ct) is { } forbidden) return forbidden;
        return Ok(await audit.ListRecentAsync(take, ct));
    }

    /// <summary>Evaluate configured alert rules against the current platform state.</summary>
    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts(CancellationToken ct)
    {
        if (await MemberGuardAsync(ct) is { } forbidden) return forbidden;

        var rules = await config.ListAsync("alert-rule", ct);
        var metrics = await store.GetMetricsAsync(ct);
        var pending = await store.GetPendingApprovalsAsync(ct);
        var execs = await store.ListRecentExecutionsAsync(500, ct);
        var failedExecs = execs.Count(e => string.Equals(e.Status, "Failed", StringComparison.OrdinalIgnoreCase));

        var unhealthy = 0;
        foreach (var t in targets.All().Where(t => t.Enabled))
        {
            if (!adapters.TryResolve(t.AdapterType, out var adapter)) { unhealthy++; continue; }
            try { if (!(await adapter.CheckHealthAsync(ct)).Healthy) unhealthy++; }
            catch { unhealthy++; }
        }

        double Current(string? metric) => metric switch
        {
            "approvalBacklog" => pending.Count,
            "executionFailures" => failedExecs,
            "unhealthyTargets" => unhealthy,
            "deniedToday" => metrics.TodayDenied,
            "failedToday" => metrics.TodayFailed,
            _ => 0
        };

        var evaluated = rules.Select(r =>
        {
            var d = JsonSerializer.Deserialize<AlertRule>(r.Json, J) ?? new AlertRule();
            var current = Current(d.Metric);
            var firing = d.Enabled && d.Operator switch
            {
                ">" => current > d.Threshold,
                ">=" => current >= d.Threshold,
                "<" => current < d.Threshold,
                "<=" => current <= d.Threshold,
                _ => false
            };
            return new { d.Id, d.Name, d.Metric, d.Operator, d.Threshold, d.Severity, d.Enabled, currentValue = current, firing };
        }).OrderByDescending(a => a.firing).ToList();

        return Ok(evaluated);
    }

    /// <summary>Failed executions captured for triage (durable dead-letter view), with triage state.</summary>
    [HttpGet("dead-letter")]
    public async Task<IActionResult> DeadLetter(CancellationToken ct)
    {
        if (await MemberGuardAsync(ct) is { } forbidden) return forbidden;
        var execs = await store.ListRecentExecutionsAsync(500, ct);
        var failed = execs.Where(e => string.Equals(e.Status, "Failed", StringComparison.OrdinalIgnoreCase)).ToList();
        var triage = (await config.ListAsync("dead-letter-triage", ct))
            .ToDictionary(c => c.EntityId, c => c.Json, StringComparer.OrdinalIgnoreCase);

        var items = failed.Select(e =>
        {
            var status = "Pending";
            if (triage.TryGetValue(e.ExecutionId.ToString(), out var json))
            {
                try { status = JsonSerializer.Deserialize<TriageState>(json, J)?.Status ?? "Pending"; } catch { }
            }
            return new
            {
                executionId = e.ExecutionId, actionRequestId = e.ActionRequestId, adapterCode = e.AdapterCode,
                errorCode = e.ErrorCode, errorMessage = e.ErrorMessage, startedUtc = e.StartedUtc, status
            };
        }).OrderByDescending(i => i.startedUtc).ToList();

        return Ok(items);
    }

    /// <summary>Triage a dead-letter item: replay (requeue for retry) or discard.</summary>
    [HttpPost("dead-letter/{executionId}/{decision}")]
    public async Task<IActionResult> TriageDeadLetter(string executionId, string decision, CancellationToken ct)
    {
        if (Guard() is { } forbidden) return forbidden;
        var status = decision.ToLowerInvariant() switch
        {
            "replay" => "Replayed",
            "discard" => "Discarded",
            _ => null
        };
        if (status is null) throw new ValidationException("Choose either 'replay' or 'discard'.") { Title = "Unknown action" };

        await config.UpsertAsync("dead-letter-triage", executionId,
            JsonSerializer.Serialize(new TriageState { Status = status }), ct);
        await auditWriter.WriteAsync(HttpContext, $"dead-letter.{decision.ToLowerInvariant()}", "DeadLetter", executionId,
            $"Dead-letter {executionId} → {status}");
        return Ok(new { executionId, status });
    }

    private sealed class AlertRule
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Metric { get; set; } = "";
        public string Operator { get; set; } = ">";
        public double Threshold { get; set; }
        public string Severity { get; set; } = "Warning";
        public bool Enabled { get; set; } = true;
    }

    private sealed class TriageState { public string Status { get; set; } = "Pending"; }

    private sealed class ExceptionItem
    {
        public string Kind { get; set; } = "";
        public DateTimeOffset OccurredUtc { get; set; }
        public string Reference { get; set; } = "";
        public string? Detail { get; set; }
        public string? Code { get; set; }
        public string? Message { get; set; }
    }
}
