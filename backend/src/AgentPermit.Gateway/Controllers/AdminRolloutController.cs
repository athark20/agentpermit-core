using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Observations;
using AgentPermit.Gateway.Registry;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Admin control of agent rollout posture (OBSERVE → WARN → ENFORCE and rollback), with a fleet overview
/// that surfaces which agents are still in warm-up and which have drifted (stale warm-up). Recommendations
/// are evidence-based from the observation trail. All admin-gated and audited.
/// </summary>
[ApiController]
[Route("api/admin/rollout")]
public sealed class AdminRolloutController(
    IAgentStore store, IAgentRegistry registry, RolloutController rollout, IObservationStore observations,
    RolloutOptions options, AdminAuthorizationService admin, AuditService audit) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    private async Task<RolloutStatus> AssessAsync(AgentInfo agent, DateTimeOffset now, CancellationToken ct)
    {
        var stats = await observations.GetAgentStatsAsync(agent.AgentId, now.AddDays(-options.ObservationWindowDays), ct);
        return rollout.Assess(agent, now, stats);
    }

    /// <summary>Fleet rollout overview — every agent's posture, warm-up state, drift, and evidence.</summary>
    [HttpGet]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct)) return Forbidden();
        var now = DateTimeOffset.UtcNow;
        var agents = await store.ListAsync(ct);
        var statuses = new List<RolloutStatus>();
        foreach (var a in agents) statuses.Add(await AssessAsync(a, now, ct));
        return Ok(new
        {
            total = statuses.Count,
            enforced = statuses.Count(s => s.FullyEnforced),
            warmingUp = statuses.Count(s => s.WarmingUp),
            drifted = statuses.Count(s => s.Drift != RolloutDrift.None),
            agents = statuses
        });
    }

    [HttpGet("{agentId}")]
    public async Task<IActionResult> Get(string agentId, CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct)) return Forbidden();
        var agent = await store.GetAsync(agentId, ct);
        if (agent is null) throw new NotFoundException("No agent is registered with that id.") { Title = "Agent not found" };
        return Ok(await AssessAsync(agent, DateTimeOffset.UtcNow, ct));
    }

    /// <summary>Run one evidence-based auto-promotion pass now (also runs on a schedule when enabled).
    /// Promotes every warming-up agent whose recommendation is to advance; returns what changed.</summary>
    [HttpPost("auto-promote")]
    public async Task<IActionResult> AutoPromote([FromServices] IServiceProvider services, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var promoted = await AutoPromotionService.RunOnceAsync(services, ct);
        return Ok(new { promoted = promoted.Select(p => new { agentId = p.AgentId, from = p.From, to = p.To }) });
    }

    /// <summary>Advance the agent one step toward binding enforcement (OBSERVE→WARN→ENFORCE).</summary>
    [HttpPost("{agentId}/promote")]
    public Task<IActionResult> Promote(string agentId, CancellationToken ct)
        => MoveAsync(agentId, rollout.Next, "rollout.promote", ct);

    /// <summary>Roll the agent one step back toward observation (ENFORCE→WARN→OBSERVE).</summary>
    [HttpPost("{agentId}/demote")]
    public Task<IActionResult> Demote(string agentId, CancellationToken ct)
        => MoveAsync(agentId, rollout.Previous, "rollout.demote", ct);

    private async Task<IActionResult> MoveAsync(string agentId, Func<string?, string> move, string auditAction, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var agent = await store.GetAsync(agentId, ct);
        if (agent is null) throw new NotFoundException("No agent is registered with that id.") { Title = "Agent not found" };

        var from = EnforcementPosture.Normalize(agent.EnforcementPosture);
        var to = move(from);
        if (string.Equals(from, to, StringComparison.Ordinal))
            return Ok(new { unchanged = true, status = await AssessAsync(agent, DateTimeOffset.UtcNow, ct) });

        agent.EnforcementPosture = to;
        await store.UpsertAsync(agent, ct);
        registry.Invalidate();
        await audit.WriteAsync(HttpContext, auditAction, "Agent", agentId, $"Rollout posture {from} -> {to}");
        return Ok(await AssessAsync(agent, DateTimeOffset.UtcNow, ct));
    }
}
