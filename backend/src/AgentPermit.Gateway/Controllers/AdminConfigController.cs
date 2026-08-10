using System.Text.Json;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Generic CRUD for control-plane configuration modules — environments, role assignments, policy
/// assignments, approval matrices, platform settings, retention. Each record is the authored JSON
/// document. Entra-gated and audited. One controller serves every config surface in the admin UI.
/// </summary>
[ApiController]
[Route("api/admin/config")]
public sealed class AdminConfigController(
    IConfigStore store, AdminAuthorizationService admin, AuditService audit) : ControllerBase
{
    // Allow-list of configuration types the admin console manages.
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "environment", "role-assignment", "policy-assignment", "approval-matrix", "platform-setting",
        "retention-policy", "alert-rule", "release"
    };

    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);
    private static NotFoundException BadType() => new("That configuration type is not managed here.") { Title = "Unknown configuration type" };

    [HttpGet("{type}")]
    public async Task<IActionResult> List(string type, CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct)) return Forbidden();
        if (!Allowed.Contains(type)) throw BadType();
        var rows = await store.ListAsync(type, ct);
        return Ok(rows.Select(r => new
        {
            id = r.EntityId,
            updatedUtc = r.UpdatedUtc,
            value = JsonSerializer.Deserialize<JsonElement>(r.Json)
        }));
    }

    [HttpPut("{type}/{id}")]
    public async Task<IActionResult> Upsert(string type, string id, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        if (!Allowed.Contains(type)) throw BadType();
        if (body.ValueKind != JsonValueKind.Object)
            throw new ValidationException("The request body must be a JSON object.") { Title = "Body must be a JSON object" };

        var json = body.GetRawText();
        await store.UpsertAsync(type, id, json, ct);
        await audit.WriteAsync(HttpContext, $"{type}.upsert", "Config", $"{type}/{id}", $"Saved {type} '{id}'");
        return Ok(new { id, value = body });
    }

    [HttpDelete("{type}/{id}")]
    public async Task<IActionResult> Delete(string type, string id, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        if (!Allowed.Contains(type)) throw BadType();
        var removed = await store.DeleteAsync(type, id, ct);
        if (!removed) throw new NotFoundException("No such configuration record exists.") { Title = "Not found" };
        await audit.WriteAsync(HttpContext, $"{type}.delete", "Config", $"{type}/{id}", $"Deleted {type} '{id}'");
        return NoContent();
    }
}
