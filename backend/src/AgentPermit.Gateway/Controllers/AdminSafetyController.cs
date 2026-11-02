using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Admin control of the always-on safety net: the global kill switch (emergency freeze of ALL action
/// requests), per-agent quarantine, and the two velocity breakers' configuration. Admin-gated and audited.
/// </summary>
[ApiController]
[Route("api/admin/safety")]
public sealed class AdminSafetyController(
    KillSwitchService killSwitch, QuarantineService quarantine,
    CircuitBreakerOptions breaker, DenyBreakerOptions denyBreaker,
    AdminAuthorizationService admin, AuditService audit) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Current freeze state, quarantined agents and breaker limits.</summary>
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct)) return Forbidden();
        return Ok(new
        {
            killSwitch = await killSwitch.GetAsync(ct),
            quarantined = await quarantine.ListAsync(ct),
            circuitBreaker = new { breaker.Enabled, breaker.MaxExecutionsPerWindow, breaker.WindowSeconds },
            denyBreaker = new { denyBreaker.Enabled, denyBreaker.MaxDenialsPerWindow, denyBreaker.WindowSeconds, denyBreaker.AutoQuarantine }
        });
    }

    /// <summary>Engage the kill switch — every action request is denied SYSTEM_FROZEN until unfrozen.</summary>
    [HttpPost("freeze")]
    public async Task<IActionResult> Freeze([FromBody] FreezeRequest? body, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var actor = User?.Identity?.Name;
        await killSwitch.SetAsync(true, body?.Reason ?? "Manual freeze", actor, ct);
        await audit.WriteAsync(HttpContext, "killswitch.freeze", "System", "global", body?.Reason ?? "Manual freeze");
        return Ok(await killSwitch.GetAsync(ct));
    }

    /// <summary>Lift the kill switch.</summary>
    [HttpPost("unfreeze")]
    public async Task<IActionResult> Unfreeze(CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        await killSwitch.SetAsync(false, null, User?.Identity?.Name, ct);
        await audit.WriteAsync(HttpContext, "killswitch.unfreeze", "System", "global", "Freeze lifted");
        return Ok(await killSwitch.GetAsync(ct));
    }

    /// <summary>All agents currently quarantined (automatic or manual).</summary>
    [HttpGet("quarantine")]
    public async Task<IActionResult> Quarantined(CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct)) return Forbidden();
        return Ok(await quarantine.ListAsync(ct));
    }

    /// <summary>Quarantine one agent manually — every request from it is denied AGENT_QUARANTINED until lifted.</summary>
    [HttpPost("quarantine/{agentId}")]
    public async Task<IActionResult> Quarantine(string agentId, [FromBody] FreezeRequest? body, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var actor = User?.Identity?.Name ?? "admin";
        var state = await quarantine.QuarantineAsync(agentId, body?.Reason ?? "Manual quarantine", actor, null, ct);
        await audit.WriteAsync(HttpContext, "quarantine.set", "Agent", agentId, body?.Reason ?? "Manual quarantine");
        return Ok(state);
    }

    /// <summary>Lift an agent's quarantine (automatic or manual). Audited.</summary>
    [HttpPost("quarantine/{agentId}/lift")]
    public async Task<IActionResult> LiftQuarantine(string agentId, [FromBody] FreezeRequest? body, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var lifted = await quarantine.LiftAsync(agentId, ct);
        if (!lifted)
            return Problem(title: "Agent is not quarantined", statusCode: StatusCodes.Status404NotFound);
        await audit.WriteAsync(HttpContext, "quarantine.lift", "Agent", agentId, body?.Reason ?? "Quarantine lifted");
        return Ok(new { agentId, lifted = true });
    }

    public sealed class FreezeRequest { public string? Reason { get; set; } }
}
