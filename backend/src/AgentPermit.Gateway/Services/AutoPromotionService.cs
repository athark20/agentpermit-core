using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Observations;
using AgentPermit.Gateway.Registry;
using Microsoft.Extensions.Hosting;

namespace AgentPermit.Gateway.Services;

public sealed class AutoPromotionOptions
{
    /// <summary>Off by default — auto-promotion changes enforcement behavior, so it is an explicit opt-in
    /// even though it only ever moves in the safe direction (more enforcement).</summary>
    public bool Enabled { get; set; }

    public int IntervalSeconds { get; set; } = 3600;
}

/// <summary>
/// Periodically graduates warming-up agents along the rollout ladder when the EVIDENCE-BASED recommendation
/// says it's safe (enough clean traffic in the window with zero would-have-been-blocks). Only ever promotes
/// (never demotes), so it can only increase enforcement. Off by default; every auto-promotion is audited.
/// </summary>
public sealed class AutoPromotionService(
    IServiceProvider services,
    AutoPromotionOptions options,
    ILogger<AutoPromotionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Auto-promotion is disabled.");
            return;
        }
        var interval = TimeSpan.FromSeconds(Math.Max(60, options.IntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(services, stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Auto-promotion pass failed (isolated)."); }
            try { await Task.Delay(interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>One evaluation pass — testable in isolation. Returns the agents promoted.</summary>
    public static async Task<IReadOnlyList<(string AgentId, string From, string To)>> RunOnceAsync(
        IServiceProvider services, CancellationToken ct)
    {
        var store = services.GetRequiredService<IAgentStore>();
        var registry = services.GetRequiredService<IAgentRegistry>();
        var rollout = services.GetRequiredService<RolloutController>();
        var observations = services.GetRequiredService<IObservationStore>();
        var rolloutOptions = services.GetRequiredService<RolloutOptions>();
        var audit = services.GetRequiredService<AuditService>();

        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-rolloutOptions.ObservationWindowDays);
        var promoted = new List<(string, string, string)>();

        foreach (var agent in await store.ListAsync(ct))
        {
            var posture = EnforcementPosture.Normalize(agent.EnforcementPosture);
            if (posture is not (EnforcementPosture.Observe or EnforcementPosture.Warn)) continue;

            var stats = await observations.GetAgentStatsAsync(agent.AgentId, since, ct);
            var status = rollout.Assess(agent, now, stats);
            var next = rollout.Next(posture);

            // Promote only when the evidence-based recommendation is exactly the next rung.
            if (!string.Equals(status.Recommendation, next, StringComparison.Ordinal)) continue;

            agent.EnforcementPosture = next;
            await store.UpsertAsync(agent, ct);
            registry.Invalidate();
            await audit.WriteSystemAsync("rollout.auto-promote", "Agent", agent.AgentId,
                $"Auto-promoted {posture} -> {next} ({stats.Total} obs, 0 would-have-been-blocks in {rolloutOptions.ObservationWindowDays}d)", ct);
            promoted.Add((agent.AgentId, posture, next));
        }
        return promoted;
    }
}
