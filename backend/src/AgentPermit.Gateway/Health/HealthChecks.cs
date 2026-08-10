using System.Text.Json;
using AgentPermit.Gateway.Adapters;
using AgentPermit.Gateway.Policies;
using AgentPermit.Gateway.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentPermit.Gateway.Health;

/// <summary>Readiness: a policy snapshot is loaded and cached.</summary>
public sealed class PolicySnapshotHealthCheck(IPolicySnapshotProvider snapshots) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        var s = snapshots.Current;
        var data = new Dictionary<string, object> { ["version"] = s.Version, ["rules"] = s.Rules.Count, ["hash"] = snapshots.ActiveHash };
        return Task.FromResult(s.Rules.Count > 0
            ? HealthCheckResult.Healthy($"snapshot {s.Version} ({s.Rules.Count} rules)", data)
            : HealthCheckResult.Degraded("policy snapshot has no rules", data: data));
    }
}

/// <summary>Readiness: at least one target-system adapter is registered.</summary>
public sealed class AdaptersHealthCheck(IActionAdapterResolver adapters) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        var targets = adapters.RegisteredTargets;
        var data = new Dictionary<string, object> { ["targets"] = string.Join(",", targets) };
        return Task.FromResult(targets.Count > 0
            ? HealthCheckResult.Healthy($"{targets.Count} adapter(s)", data)
            : HealthCheckResult.Unhealthy("no adapters registered", data: data));
    }
}

/// <summary>Dependency: reports whether receipt signing is configured.</summary>
public sealed class SigningHealthCheck(IReceiptSigner signer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
        => Task.FromResult(signer.Enabled
            ? HealthCheckResult.Healthy("receipt signing enabled")
            : HealthCheckResult.Degraded("receipt signing disabled (unsigned receipts)"));
}

/// <summary>Dependency: probes SQL when the SqlServer provider is configured (deps endpoint only).</summary>
public sealed class SqlDependencyHealthCheck(IConfiguration config) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        var provider = config["Persistence:Provider"] ?? "InMemory";
        if (!string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            return HealthCheckResult.Healthy("in-memory persistence");

        var cs = config["Persistence:SqlConnectionString"];
        if (string.IsNullOrWhiteSpace(cs))
            return HealthCheckResult.Unhealthy("SqlServer selected but no connection string");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(cts.Token);
            await using var cmd = new SqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(cts.Token);
            return HealthCheckResult.Healthy("SQL reachable");
        }
        catch (Exception ex)
        {
            // Serverless SQL may be resuming — degraded rather than a hard failure.
            return HealthCheckResult.Degraded($"SQL not immediately reachable: {ex.Message}");
        }
    }
}

/// <summary>Writes a detailed JSON health report for the /health/deps endpoint.</summary>
public static class HealthReport
{
    public static Task Write(HttpContext ctx, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data
            })
        };
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
