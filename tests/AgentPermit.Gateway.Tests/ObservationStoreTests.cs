using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Observations;
using AgentPermit.Gateway.Registry;
using AgentPermit.Gateway.Services;

namespace AgentPermit.Gateway.Tests;

/// <summary>The observation store aggregation and the evidence-based rollout recommendation it feeds.</summary>
public sealed class ObservationStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static ObservationRecord Obs(string agent, string effective, bool wouldBlock, DateTimeOffset when) => new()
    {
        OccurredUtc = when, AgentId = agent, ActionId = "Refund.Issue",
        ComputedDecision = wouldBlock ? Decision.Deny : effective, EffectiveDecision = effective,
        EnforcementMode = wouldBlock ? EnforcementMode.Observed : EnforcementMode.Enforced,
        ConsequenceTier = ConsequenceTier.Low, WouldHaveBlocked = wouldBlock
    };

    [Fact]
    public async Task Stats_CountAndWindow()
    {
        var store = new InMemoryObservationStore();
        await store.AppendAsync(Obs("a1", "ALLOW", false, Now.AddDays(-1)), default);
        await store.AppendAsync(Obs("a1", "ALLOW", true, Now.AddDays(-2)), default);      // would-have-blocked
        await store.AppendAsync(Obs("a1", "ALLOW", false, Now.AddDays(-30)), default);    // outside window
        await store.AppendAsync(Obs("a2", "ALLOW", false, Now.AddDays(-1)), default);     // other agent

        var stats = await store.GetAgentStatsAsync("a1", Now.AddDays(-7), default);

        Assert.Equal(2, stats.Total);              // window excludes the 30-day-old one
        Assert.Equal(1, stats.WouldHaveBlocked);
        Assert.Equal(2, stats.EffectiveAllow);
    }

    [Fact]
    public async Task ListForAgent_ReturnsNewestFirst()
    {
        var store = new InMemoryObservationStore();
        await store.AppendAsync(Obs("a1", "ALLOW", false, Now.AddDays(-3)), default);
        await store.AppendAsync(Obs("a1", "DENY", false, Now.AddDays(-1)), default);

        var recent = await store.ListForAgentAsync("a1", 10, default);

        Assert.Equal(2, recent.Count);
        Assert.Equal("DENY", recent[0].EffectiveDecision); // newest first
    }

    // ---- Evidence-based rollout recommendation ----

    private readonly RolloutController _rollout = new(new RolloutOptions
    {
        MaxWarmupDays = 14, ObservationWindowDays = 7, MinObservationsToPromote = 20
    });

    private static AgentInfo Observing() => new()
    {
        AgentId = "a", DisplayName = "A", EnforcementPosture = EnforcementPosture.Observe, UpdatedUtc = Now.AddDays(-1)
    };

    private static AgentObservationStats Stats(int total, int wouldBlock) => new()
    {
        AgentId = "a", Total = total, WouldHaveBlocked = wouldBlock,
        EffectiveAllow = total, EffectiveDeny = 0, EffectiveApproval = 0
    };

    [Fact]
    public void CleanTraffic_OverThreshold_RecommendsPromotion()
    {
        var s = _rollout.Assess(Observing(), Now, Stats(total: 40, wouldBlock: 0));
        Assert.Equal(EnforcementPosture.Warn, s.Recommendation);
        Assert.Equal(0, s.WouldHaveBlockedInWindow);
        Assert.Equal(40, s.ObservationsInWindow);
    }

    [Fact]
    public void AnyWouldHaveBlocks_HoldsForReview()
    {
        var s = _rollout.Assess(Observing(), Now, Stats(total: 40, wouldBlock: 3));
        Assert.Null(s.Recommendation);                                  // hold — needs review
        Assert.Contains(s.Reasons, r => r.Contains("would-have-been-block"));
    }

    [Fact]
    public void ThinTraffic_HoldsForMoreObservation()
    {
        var s = _rollout.Assess(Observing(), Now, Stats(total: 3, wouldBlock: 0));
        Assert.Null(s.Recommendation);                                  // hold — not enough traffic
        Assert.Contains(s.Reasons, r => r.Contains("keep observing"));
    }
}
