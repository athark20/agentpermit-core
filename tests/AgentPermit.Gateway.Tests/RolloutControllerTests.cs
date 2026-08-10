using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Registry;
using AgentPermit.Gateway.Services;

namespace AgentPermit.Gateway.Tests;

/// <summary>The rollout ladder (OBSERVE→WARN→ENFORCE + rollback) and stale-warm-up drift detection.</summary>
public sealed class RolloutControllerTests
{
    private readonly RolloutController _rollout = new(new RolloutOptions { MaxWarmupDays = 14 });
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static AgentInfo Agent(string? posture, DateTimeOffset? updated = null) => new()
    {
        AgentId = "a", DisplayName = "A", EnforcementPosture = posture, UpdatedUtc = updated ?? Now
    };

    // ---- Ladder ----

    [Theory]
    [InlineData(EnforcementPosture.Observe, EnforcementPosture.Warn)]
    [InlineData(EnforcementPosture.Warn, EnforcementPosture.Enforce)]
    [InlineData(EnforcementPosture.Enforce, EnforcementPosture.Enforce)] // saturates
    public void Promote_MovesUpTheLadder(string from, string expected)
        => Assert.Equal(expected, _rollout.Next(from));

    [Theory]
    [InlineData(EnforcementPosture.Enforce, EnforcementPosture.Warn)]
    [InlineData(EnforcementPosture.Warn, EnforcementPosture.Observe)]
    [InlineData(EnforcementPosture.Observe, EnforcementPosture.Observe)] // saturates
    public void Demote_MovesDownTheLadder(string from, string expected)
        => Assert.Equal(expected, _rollout.Previous(from));

    [Fact]
    public void UnsetPosture_IsTreatedAsEnforce()
    {
        Assert.Equal(EnforcementPosture.Enforce, _rollout.Next(null));       // already enforced
        Assert.Equal(EnforcementPosture.Warn, _rollout.Previous(null));      // rolls back from enforce
    }

    // ---- Assessment + drift ----

    [Fact]
    public void FreshObserve_IsWarmingUp_NoDrift_RecommendsWarn()
    {
        var s = _rollout.Assess(Agent(EnforcementPosture.Observe, Now.AddDays(-2)), Now);

        Assert.True(s.WarmingUp);
        Assert.False(s.FullyEnforced);
        Assert.Equal(RolloutDrift.None, s.Drift);
        Assert.Equal(EnforcementPosture.Warn, s.Recommendation);
    }

    [Fact]
    public void StaleObserve_FlagsDrift_RecommendsPromotion()
    {
        var s = _rollout.Assess(Agent(EnforcementPosture.Observe, Now.AddDays(-30)), Now);

        Assert.Equal(RolloutDrift.StaleWarmup, s.Drift);
        Assert.Equal(EnforcementPosture.Warn, s.Recommendation);
        Assert.Equal(30, s.DaysSinceUpdated);
    }

    [Fact]
    public void StaleWarn_FlagsDrift_RecommendsEnforce()
    {
        var s = _rollout.Assess(Agent(EnforcementPosture.Warn, Now.AddDays(-20)), Now);

        Assert.Equal(RolloutDrift.StaleWarmup, s.Drift);
        Assert.Equal(EnforcementPosture.Enforce, s.Recommendation);
    }

    [Fact]
    public void Enforce_IsFullyEnforced_NoDrift_NoRecommendation()
    {
        var s = _rollout.Assess(Agent(EnforcementPosture.Enforce, Now.AddDays(-90)), Now);

        Assert.True(s.FullyEnforced);
        Assert.False(s.WarmingUp);
        Assert.Equal(RolloutDrift.None, s.Drift);
        Assert.Null(s.Recommendation);
    }
}
