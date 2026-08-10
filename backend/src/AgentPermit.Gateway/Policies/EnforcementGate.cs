using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Registry;

namespace AgentPermit.Gateway.Policies;

/// <summary>
/// The decision actually applied after the enforcement-posture gate, alongside the policy's computed
/// decision and the mode — so every request carries evidence of what WOULD have happened even when a
/// warm-up allowed it through.
/// </summary>
public sealed class EffectiveDecision
{
    /// <summary>The binding outcome the pipeline acts on: ALLOW | DENY | REQUIRE_APPROVAL.</summary>
    public required string Decision { get; init; }

    /// <summary>What the policy engine computed, before the posture gate.</summary>
    public required string ComputedDecision { get; init; }

    /// <summary>ENFORCED | OBSERVED | WARNED.</summary>
    public required string Mode { get; init; }

    /// <summary>The agent's normalized posture at evaluation time.</summary>
    public required string Posture { get; init; }

    /// <summary>The action's normalized consequence tier.</summary>
    public required string ConsequenceTier { get; init; }

    /// <summary>True when the posture gate changed the computed decision (a warm-up downgrade).</summary>
    public bool Downgraded => !string.Equals(Decision, ComputedDecision, StringComparison.Ordinal);

    /// <summary>Human-readable explanation of a downgrade (null when enforced as computed).</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Applies an agent's rollout posture to a computed policy decision. During warm-up (OBSERVE/WARN) a
/// computed DENY or REQUIRE_APPROVAL on a LOW/MEDIUM-consequence action is downgraded to ALLOW and only
/// recorded — so onboarding a fleet does not break working low-risk automations while we learn.
///
/// The invariant that makes this safe: a HIGH/CRITICAL-consequence action (money movement, irreversible,
/// bulk sensitive data) is ALWAYS enforced, regardless of posture — warm-up can never open the vault. An
/// ALLOW is always allowed. An unset posture is ENFORCE and an unset consequence is HIGH (both secure by
/// default), so a mis-classified or newly-imported action fails closed, not open.
/// </summary>
public sealed class EnforcementGate
{
    /// <param name="consequenceTier">
    /// The action's EFFECTIVE consequence — the severest of its own declared tier and its target system's,
    /// resolved by the caller (see <c>ConsequenceTier.Severest</c>) so an action cannot under-declare its way
    /// below the target it hits.
    /// </param>
    public EffectiveDecision Apply(PolicyEvaluationResult computed, AgentInfo agent, string? consequenceTier)
    {
        var posture = EnforcementPosture.Normalize(agent.EnforcementPosture);
        var tier = ConsequenceTier.Normalize(consequenceTier);

        // The computed decision is binding when: it already permits; OR the agent is at steady-state
        // enforcement; OR the action is high-consequence (enforced even mid-warm-up). This is the invariant.
        var mustEnforce = computed.Decision == Decision.Allow
                       || posture == EnforcementPosture.Enforce
                       || ConsequenceTier.IsHighConsequence(tier);

        if (mustEnforce)
            return new EffectiveDecision
            {
                Decision = computed.Decision,
                ComputedDecision = computed.Decision,
                Mode = EnforcementMode.Enforced,
                Posture = posture,
                ConsequenceTier = tier
            };

        // Warm-up path: OBSERVE/WARN + LOW/MEDIUM consequence. Record what we WOULD have done, let it through.
        var mode = posture == EnforcementPosture.Warn ? EnforcementMode.Warned : EnforcementMode.Observed;
        return new EffectiveDecision
        {
            Decision = Decision.Allow,
            ComputedDecision = computed.Decision,
            Mode = mode,
            Posture = posture,
            ConsequenceTier = tier,
            Note = $"Would have been {computed.Decision} ({computed.ReasonCode}); {mode} because the agent is "
                 + $"in {posture} warm-up for a {tier}-consequence action."
        };
    }
}
