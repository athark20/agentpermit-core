using AgentPermit.Gateway.Observations;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Read access to the observation trail — the evidence behind rollout decisions. Chiefly used to review
/// what an agent's warm-up WOULD have blocked before promoting it to binding enforcement. Admin-gated.
/// </summary>
[ApiController]
[Route("api/admin/observations")]
public sealed class AdminObservationsController(
    IObservationStore observations, RolloutOptions options, FeedStatusTracker feeds,
    IEnumerable<IObservationFeed> pullFeeds, AdminAuthorizationService admin) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Status of every observation feed — the always-on gateway push source plus any external
    /// pull feeds (Purview audit, Flow run history), whether enabled, last run, cursor, count, and errors.</summary>
    [HttpGet("feeds")]
    public IActionResult Feeds()
    {
        if (!Authorized()) return Forbidden();
        foreach (var f in pullFeeds) feeds.Register(f); // idempotent — deterministic regardless of the ingestion loop
        return Ok(feeds.All());
    }

    /// <summary>Recent observations for an agent plus its windowed stats.</summary>
    [HttpGet("{agentId}")]
    public async Task<IActionResult> ForAgent(string agentId, [FromQuery] int limit, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        var take = limit is > 0 and <= 500 ? limit : 100;
        var since = DateTimeOffset.UtcNow.AddDays(-options.ObservationWindowDays);
        var stats = await observations.GetAgentStatsAsync(agentId, since, ct);
        var recent = await observations.ListForAgentAsync(agentId, take, ct);
        return Ok(new { agentId, windowDays = options.ObservationWindowDays, stats, recent });
    }
}
