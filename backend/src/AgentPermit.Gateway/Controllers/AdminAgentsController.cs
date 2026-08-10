using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Registry;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Admin management of the Agent Registry. Registering an agent (data) is how an Entra workload
/// identity becomes allowed to request actions — the gateway authorizes every request against the
/// agent's bound Entra client id, never the body-supplied agentId.
/// </summary>
[ApiController]
[Route("api/admin/agents")]
public sealed class AdminAgentsController(
    IAgentStore store, IAgentRegistry registry, AdminAuthorizationService admin,
    AuditService audit, AgentPermit.Gateway.Licensing.IEntitlementsProvider entitlements) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => await admin.IsMemberAsync(HttpContext, ct) ? Ok(await store.ListAsync(ct)) : Forbidden();

    [HttpGet("{agentId}")]
    public async Task<IActionResult> Get(string agentId, CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct)) return Forbidden();
        var a = await store.GetAsync(agentId, ct);
        if (a is null) throw new NotFoundException("No agent is registered with that id.") { Title = "Agent not found" };
        return Ok(a);
    }

    [HttpPut("{agentId}")]
    public async Task<IActionResult> Upsert(string agentId, [FromBody] AgentInfo body, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var ent = entitlements.Current;
        if (ent.ManagementLocked)
            throw new LicenceRequiredException("Your AgentPermit licence is missing or expired. Existing governance keeps running; renew to make changes.");
        if (string.IsNullOrWhiteSpace(body.DisplayName))
            throw new ValidationException("A display name is required.",
                new Dictionary<string, string[]> { ["displayName"] = new[] { "A display name is required." } });
        // Tier cap: only counts toward the limit when registering a NEW agent.
        if (await store.GetAsync(agentId, ct) is null && (await store.ListAsync(ct)).Count >= ent.MaxAgents)
            throw new LicenceRequiredException($"Your {ent.Edition} plan governs up to {ent.MaxAgents} agents. Contact sales to add more.") { Title = "Agent limit reached" };
        body.AgentId = agentId;
        await store.UpsertAsync(body, ct);
        registry.Invalidate();
        await audit.WriteAsync(HttpContext, "agent.upsert", "Agent", agentId,
            $"Upserted agent '{agentId}' (entra {body.EntraClientId}, enabled {body.Enabled})");
        return Ok(body);
    }

    [HttpDelete("{agentId}")]
    public async Task<IActionResult> Delete(string agentId, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var removed = await store.DeleteAsync(agentId, ct);
        registry.Invalidate();
        if (!removed) throw new NotFoundException("No agent is registered with that id.") { Title = "Agent not found" };
        await audit.WriteAsync(HttpContext, "agent.delete", "Agent", agentId, $"Deleted agent '{agentId}'");
        return NoContent();
    }
}
