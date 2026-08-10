using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Admin control of the always-on safety net: the global kill switch (emergency freeze of ALL action
/// requests) and the velocity circuit-breaker configuration. Admin-gated and audited.
/// </summary>
[ApiController]
[Route("api/admin/safety")]
public sealed class AdminSafetyController(
    KillSwitchService killSwitch, CircuitBreakerOptions breaker,
    AdminAuthorizationService admin, AuditService audit) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Current freeze state + circuit-breaker limits.</summary>
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct)) return Forbidden();
        return Ok(new
        {
            killSwitch = await killSwitch.GetAsync(ct),
            circuitBreaker = new { breaker.Enabled, breaker.MaxExecutionsPerWindow, breaker.WindowSeconds }
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

    public sealed class FreezeRequest { public string? Reason { get; set; } }
}
