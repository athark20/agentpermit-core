using AgentPermit.Gateway.ControlPlane;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Admin management of the Target System Registry. Adding a target here (data) is how a new
/// downstream system becomes callable — no code deploy when an adapter for its type exists.
/// </summary>
[ApiController]
[Route("api/admin/targets")]
public sealed class AdminTargetsController(
    ITargetSystemStore store, ITargetSystemRegistry registry, AdminAuthorizationService admin,
    RiskClassifier classifier, AuditService audit) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => await admin.IsMemberAsync(HttpContext, ct) ? Ok(await store.ListAsync(ct)) : Forbidden();

    [HttpGet("{code}")]
    public async Task<IActionResult> Get(string code, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var t = await store.GetAsync(code, ct);
        if (t is null) throw new NotFoundException("No target system is registered with that code.") { Title = "Target not found" };
        return Ok(t);
    }

    [HttpPut("{code}")]
    public async Task<IActionResult> Upsert(string code, [FromBody] TargetSystemRecord body, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        if (string.IsNullOrWhiteSpace(body.AdapterType))
            throw new ValidationException("An adapter type is required.",
                new Dictionary<string, string[]> { ["adapterType"] = new[] { "An adapter type is required." } });
        body.TargetSystemCode = code;
        // Classify the target's consequence when the admin didn't set it explicitly (danger known up front).
        var classification = string.IsNullOrWhiteSpace(body.ConsequenceTier)
            ? classifier.ClassifyTarget(body) : null;
        if (classification is not null) body.ConsequenceTier = classification.Tier;
        await store.UpsertAsync(body, ct);
        registry.Invalidate();
        await audit.WriteAsync(HttpContext, "target.upsert", "TargetSystem", code,
            $"Upserted target '{code}' (adapter {body.AdapterType}, consequence {body.ConsequenceTier}"
            + $"{(classification is null ? "" : " [auto: " + string.Join("; ", classification.Reasons) + "]")}, enabled {body.Enabled})");
        return Ok(body);
    }

    [HttpDelete("{code}")]
    public async Task<IActionResult> Delete(string code, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var removed = await store.DeleteAsync(code, ct);
        registry.Invalidate();
        if (!removed) throw new NotFoundException("No target system is registered with that code.") { Title = "Target not found" };
        await audit.WriteAsync(HttpContext, "target.delete", "TargetSystem", code, $"Deleted target '{code}'");
        return NoContent();
    }
}
