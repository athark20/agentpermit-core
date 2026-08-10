using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Observations;
using AgentPermit.Gateway.Registry;

namespace AgentPermit.Gateway.Services;

/// <summary>Options bound from "Rollout".</summary>
public sealed class RolloutOptions
{
    /// <summary>An agent left in OBSERVE/WARN warm-up longer than this is flagged as drift (stale — a
    /// silent gap where low/medium blocks are being observed but never enforced). Default 14 days.</summary>
    public int MaxWarmupDays { get; set; } = 14;

    /// <summary>How many recent days of observations count toward a promotion recommendation. Default 7.</summary>
    public int ObservationWindowDays { get; set; } = 7;

    /// <summary>Minimum clean observations in the window before recommending promotion. Default 20.</summary>
    public int MinObservationsToPromote { get; set; } = 20;
}

/// <summary>Drift signals for an agent's rollout state.</summary>
public static class RolloutDrift
{
    public const string None = "NONE";
    /// <summary>In OBSERVE/WARN past the max warm-up window — should be promoted or reviewed.</summary>
    public const string StaleWarmup = "STALE_WARMUP";
}

/// <summary>An agent's rollout posture, its warm-up state, drift, and a recommended next step.</summary>
public sealed class RolloutStatus
{
    public required string AgentId { get; init; }
    public required string DisplayName { get; init; }
    public required string Posture { get; init; }
    public required bool WarmingUp { get; init; }
    public required bool FullyEnforced { get; init; }
    public int? DaysSinceUpdated { get; init; }
    public required string Drift { get; init; }

    /// <summary>Suggested next posture, or null when the recommendation is to hold (needs review / more traffic).</summary>
    public string? Recommendation { get; init; }

    /// <summary>Observation evidence used for the recommendation (null when no stats were supplied).</summary>
    public int? ObservationsInWindow { get; init; }
    public int? WouldHaveBlockedInWindow { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>
/// Drives an agent along the rollout ladder OBSERVE → WARN → ENFORCE (with rollback), and assesses drift.
/// This is the operational half of "secure-by-default warm-up": warm-up is a deliberate, temporary state,
/// so an agent stuck in it past the window is surfaced as a gap to close — the posture gate keeps
/// high-consequence actions enforced throughout, but low/medium blocks stay merely observed until promoted.
/// </summary>
public sealed class RolloutController(RolloutOptions options)
{
    // Ascending bindingness. Promotion moves right; rollback moves left. Both saturate at the ends.
    private static readonly string[] Ladder =
        { EnforcementPosture.Observe, EnforcementPosture.Warn, EnforcementPosture.Enforce };

    private static int IndexOf(string? posture)
    {
        var p = EnforcementPosture.Normalize(posture);
        var i = Array.IndexOf(Ladder, p);
        return i < 0 ? Ladder.Length - 1 : i; // unknown normalizes to ENFORCE (last)
    }

    /// <summary>The next more-binding posture (saturates at ENFORCE).</summary>
    public string Next(string? posture) => Ladder[Math.Min(IndexOf(posture) + 1, Ladder.Length - 1)];

    /// <summary>The previous less-binding posture (saturates at OBSERVE) — a deliberate rollback.</summary>
    public string Previous(string? posture) => Ladder[Math.Max(IndexOf(posture) - 1, 0)];

    /// <summary>
    /// Assess an agent's rollout state, drift, and recommended next step. When <paramref name="stats"/> is
    /// supplied the recommendation is evidence-based: promote only after enough clean warm-up traffic with
    /// zero would-have-been-blocks; hold (recommendation null) when blocks were observed or traffic is thin.
    /// Without stats it falls back to a posture-only recommendation.
    /// </summary>
    public RolloutStatus Assess(AgentInfo agent, DateTimeOffset now, AgentObservationStats? stats = null)
    {
        var posture = EnforcementPosture.Normalize(agent.EnforcementPosture);
        var warmingUp = posture is EnforcementPosture.Observe or EnforcementPosture.Warn;
        var fullyEnforced = posture == EnforcementPosture.Enforce;

        int? daysSinceUpdated = agent.UpdatedUtc == default ? null : Math.Max(0, (int)(now - agent.UpdatedUtc).TotalDays);
        var stale = warmingUp && daysSinceUpdated is { } d && d >= options.MaxWarmupDays;

        var reasons = new List<string>();
        string? rec = null;

        if (fullyEnforced)
        {
            reasons.Add("Fully enforced — all computed decisions are binding.");
        }
        else
        {
            // Recommendation logic (only meaningful while warming up).
            if (stats is null)
            {
                // No evidence available — posture-only suggestion.
                rec = Next(posture);
                reasons.Add(posture == EnforcementPosture.Observe
                    ? "Observing — computed blocks on low/medium actions are recorded, not applied. Promote to WARN when confident."
                    : "Warning — same as observe plus owner alerts. Promote to ENFORCE to make decisions binding.");
            }
            else if (stats.WouldHaveBlocked > 0)
            {
                rec = null; // hold — real would-have-been-blocks need human review, not auto-promotion.
                reasons.Add($"{stats.WouldHaveBlocked} would-have-been-block(s) in the last {options.ObservationWindowDays}d — review these before promoting.");
            }
            else if (stats.Total >= options.MinObservationsToPromote)
            {
                rec = Next(posture);
                reasons.Add($"{stats.Total} observations in the last {options.ObservationWindowDays}d with 0 would-have-been-blocks — safe to promote to {rec}.");
            }
            else
            {
                rec = null; // hold — not enough traffic to be confident yet.
                reasons.Add($"Only {stats.Total}/{options.MinObservationsToPromote} observations in the last {options.ObservationWindowDays}d — keep observing before promoting.");
            }

            if (stale)
                reasons.Add($"Stale: in {posture} warm-up for {daysSinceUpdated}d (> {options.MaxWarmupDays}d limit) — close this gap.");
        }
        // High-consequence is always enforced regardless — make that explicit so warm-up isn't misread as "open".
        if (warmingUp) reasons.Add("High/critical-consequence actions remain enforced during warm-up.");

        return new RolloutStatus
        {
            AgentId = agent.AgentId,
            DisplayName = agent.DisplayName,
            Posture = posture,
            WarmingUp = warmingUp,
            FullyEnforced = fullyEnforced,
            DaysSinceUpdated = daysSinceUpdated,
            Drift = stale ? RolloutDrift.StaleWarmup : RolloutDrift.None,
            Recommendation = rec,
            ObservationsInWindow = stats?.Total,
            WouldHaveBlockedInWindow = stats?.WouldHaveBlocked,
            Reasons = reasons
        };
    }
}
