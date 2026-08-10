using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Policies;
using AgentPermit.Gateway.Registry;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// The enforcement-posture gate: the runtime mechanism behind "secure-by-default warm-up". These cover
/// the full truth table, and in particular the load-bearing invariant — a HIGH/CRITICAL-consequence
/// action is enforced even while the agent is in OBSERVE/WARN warm-up, so onboarding a fleet can never
/// leave the vault open.
/// </summary>
public sealed class EnforcementGateTests
{
    private readonly EnforcementGate _gate = new();

    private static PolicyEvaluationResult Computed(string decision) => new()
    {
        Decision = decision,
        PolicyCode = "Test.Policy",
        PolicyVersion = "1.0.0",
        ReasonCode = decision switch
        {
            Decision.Allow => ReasonCode.PolicyAllow,
            Decision.Deny => ReasonCode.PolicyDeny,
            _ => ReasonCode.PolicyRequireApproval
        },
        Reason = $"computed {decision}",
        TraceJson = "{}"
    };

    private static AgentInfo Agent(string? posture) => new() { AgentId = "test-agent", EnforcementPosture = posture };

    // ---- The invariant: high-consequence is ALWAYS enforced, even mid-warm-up ----

    [Theory]
    [InlineData(EnforcementPosture.Observe, ConsequenceTier.High)]
    [InlineData(EnforcementPosture.Warn, ConsequenceTier.High)]
    [InlineData(EnforcementPosture.Observe, ConsequenceTier.Critical)]
    [InlineData(EnforcementPosture.Warn, ConsequenceTier.Critical)]
    public void HighConsequence_Deny_IsAlwaysEnforced_RegardlessOfWarmup(string posture, string tier)
    {
        var e = _gate.Apply(Computed(Decision.Deny), Agent(posture), tier);

        Assert.Equal(Decision.Deny, e.Decision);          // the block stands
        Assert.Equal(EnforcementMode.Enforced, e.Mode);
        Assert.False(e.Downgraded);
    }

    [Fact]
    public void HighConsequence_RequireApproval_IsEnforced_InObserve()
    {
        var e = _gate.Apply(Computed(Decision.RequireApproval), Agent(EnforcementPosture.Observe), ConsequenceTier.High);

        Assert.Equal(Decision.RequireApproval, e.Decision);
        Assert.Equal(EnforcementMode.Enforced, e.Mode);
        Assert.False(e.Downgraded);
    }

    // ---- Warm-up only downgrades LOW/MEDIUM ----

    [Fact]
    public void LowConsequence_Deny_InObserve_IsDowngradedToAllow_AndRecorded()
    {
        var e = _gate.Apply(Computed(Decision.Deny), Agent(EnforcementPosture.Observe), ConsequenceTier.Low);

        Assert.Equal(Decision.Allow, e.Decision);          // let through during warm-up
        Assert.Equal(Decision.Deny, e.ComputedDecision);   // but we recorded what we WOULD have done
        Assert.Equal(EnforcementMode.Observed, e.Mode);
        Assert.True(e.Downgraded);
        Assert.False(string.IsNullOrWhiteSpace(e.Note));
    }

    [Fact]
    public void LowConsequence_RequireApproval_InObserve_IsDowngraded()
    {
        var e = _gate.Apply(Computed(Decision.RequireApproval), Agent(EnforcementPosture.Observe), ConsequenceTier.Low);

        Assert.Equal(Decision.Allow, e.Decision);
        Assert.Equal(Decision.RequireApproval, e.ComputedDecision);
        Assert.Equal(EnforcementMode.Observed, e.Mode);
        Assert.True(e.Downgraded);
    }

    [Fact]
    public void MediumConsequence_Deny_InWarn_IsDowngraded_AndWarned()
    {
        var e = _gate.Apply(Computed(Decision.Deny), Agent(EnforcementPosture.Warn), ConsequenceTier.Medium);

        Assert.Equal(Decision.Allow, e.Decision);
        Assert.Equal(EnforcementMode.Warned, e.Mode);
        Assert.True(e.Downgraded);
    }

    // ---- ENFORCE posture binds everything ----

    [Fact]
    public void EnforcePosture_Deny_LowConsequence_IsBinding()
    {
        var e = _gate.Apply(Computed(Decision.Deny), Agent(EnforcementPosture.Enforce), ConsequenceTier.Low);

        Assert.Equal(Decision.Deny, e.Decision);
        Assert.Equal(EnforcementMode.Enforced, e.Mode);
        Assert.False(e.Downgraded);
    }

    // ---- Secure-by-default: unset posture => ENFORCE, unset consequence => HIGH ----

    [Fact]
    public void UnsetPosture_DefaultsToEnforce_Deny_IsBinding()
    {
        var e = _gate.Apply(Computed(Decision.Deny), Agent(posture: null), ConsequenceTier.Low);

        Assert.Equal(Decision.Deny, e.Decision);
        Assert.Equal(EnforcementPosture.Enforce, e.Posture);
        Assert.False(e.Downgraded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NONSENSE")]
    public void UnknownConsequence_DefaultsToHigh_Deny_IsEnforced_EvenInObserve(string tier)
    {
        var e = _gate.Apply(Computed(Decision.Deny), Agent(EnforcementPosture.Observe), tier);

        Assert.Equal(Decision.Deny, e.Decision);
        Assert.Equal(ConsequenceTier.High, e.ConsequenceTier);
        Assert.False(e.Downgraded);
    }

    // ---- ALLOW is always allowed ----

    [Fact]
    public void Allow_IsAlwaysAllowed_EvenInObserve()
    {
        var e = _gate.Apply(Computed(Decision.Allow), Agent(EnforcementPosture.Observe), ConsequenceTier.Low);

        Assert.Equal(Decision.Allow, e.Decision);
        Assert.Equal(EnforcementMode.Enforced, e.Mode);
        Assert.False(e.Downgraded);
    }
}
