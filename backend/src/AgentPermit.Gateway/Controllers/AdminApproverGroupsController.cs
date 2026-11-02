using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Admin CRUD for approver groups: the named sets of people a rule can require for sign-off
/// (<c>approverRoles: ["FinanceController"]</c>). Admin-gated and audited; any workspace member may read.
/// </summary>
[ApiController]
[Route("api/admin/approver-groups")]
public sealed class AdminApproverGroupsController(
    ApproverGroupDirectory groups, AdminAuthorizationService admin, AuditService audit) : ControllerBase
{
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct)) return Forbidden();
        return Ok(await groups.ListAsync(ct));
    }

    [HttpGet("{name}")]
    public async Task<IActionResult> Get(string name, CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct)) return Forbidden();
        var g = await groups.GetAsync(name, ct);
        return g is null ? Problem(title: "Approver group not found", statusCode: StatusCodes.Status404NotFound) : Ok(g);
    }

    /// <summary>Create or replace a group. Body: <c>{ "description": "...", "members": [{ "email": "...", "displayName": "..." }] }</c>.</summary>
    [HttpPut("{name}")]
    public async Task<IActionResult> Upsert(string name, [FromBody] ApproverGroup body, CancellationToken ct)
    {
        if (!admin.IsAuthorized(HttpContext)) return Forbidden();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            return Problem(title: "Invalid group name", statusCode: StatusCodes.Status400BadRequest);
        var saved = await groups.UpsertAsync(name, body ?? new ApproverGroup(), ct);
        await audit.WriteAsync(HttpContext, "approver-group.upsert", "ApproverGroup", saved.Name,
            $"{saved.Members.Count} member(s)");
        return Ok(saved);
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        if (!admin.IsAuthorized(HttpContext)) return Forbidden();
        var removed = await groups.DeleteAsync(name, ct);
        if (!removed) return Problem(title: "Approver group not found", statusCode: StatusCodes.Status404NotFound);
        await audit.WriteAsync(HttpContext, "approver-group.delete", "ApproverGroup", name, "Deleted");
        return NoContent();
    }
}
