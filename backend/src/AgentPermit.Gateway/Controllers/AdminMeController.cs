using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Who the signed-in person is and what their AgentPermit personas allow. Available to ANY
/// authenticated tenant user (not only admins) so the console can shape itself per role —
/// approvers see decision buttons, viewers get read-only, non-members get "no access".
/// </summary>
[ApiController]
[Route("api/admin/me")]
public sealed class AdminMeController(AccessDirectoryService directory, AdminAuthOptions entra) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (entra.IsConfigured && !directory.DevOpen && !directory.IsServiceCaller(HttpContext)
            && HttpContext.User?.Identity?.IsAuthenticated != true)
            return Problem(title: "Sign-in required", detail: "Sign in with your Microsoft Entra ID account to use AgentPermit.",
                statusCode: StatusCodes.Status401Unauthorized);

        var roles = await directory.RolesForAsync(HttpContext, ct);
        var (name, email) = directory.Describe(HttpContext);
        var isAdmin = roles.Contains("admin");
        return Ok(new
        {
            name,
            email,
            roles,
            can = new
            {
                approve = isAdmin || roles.Contains("approver"),
                manageAgents = isAdmin || roles.Contains("agent-owner"),
                edit = isAdmin,
                view = roles.Count > 0,
            },
        });
    }
}
