using System.Diagnostics;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Health + infrastructure snapshot for the Dashboard: are the moving parts up, and how is the app itself
/// running (memory, uptime). Plain enough to show a non-technical owner "is anything broken right now".
/// </summary>
[ApiController]
[Route("api/admin/infra")]
public sealed class AdminInfraController(
    HealthCheckService health, AdminAuthorizationService admin) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct)) return Problem(title: "Workspace access required", statusCode: StatusCodes.Status403Forbidden);

        var report = await health.CheckHealthAsync(ct);
        var checks = report.Entries.Select(e => new
        {
            name = e.Key,
            healthy = e.Value.Status == HealthStatus.Healthy,
            status = e.Value.Status.ToString(),
            detail = e.Value.Description ?? e.Value.Exception?.Message,
        }).ToList();

        var p = Process.GetCurrentProcess();
        var uptime = DateTime.UtcNow - p.StartTime.ToUniversalTime();

        return Ok(new
        {
            healthy = report.Status == HealthStatus.Healthy,
            status = report.Status.ToString(),
            checks,
            infrastructure = new
            {
                memoryMb = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 0),
                managedMemoryMb = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 0),
                processors = Environment.ProcessorCount,
                uptimeHours = Math.Round(uptime.TotalHours, 1),
                machine = Environment.MachineName,
                dotnet = Environment.Version.ToString(),
            }
        });
    }
}
