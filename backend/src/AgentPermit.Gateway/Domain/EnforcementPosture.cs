namespace AgentPermit.Gateway.Domain;

/// <summary>
/// Per-agent rollout stage governing whether a COMPUTED policy denial / step-up is actually binding,
/// or merely observed while an agent is warming up after onboarding. This is how AgentPermit "shadows"
/// a newly imported agent without breaking its low-risk automations — WITHOUT ever leaving a
/// high-consequence action ungoverned (see <see cref="ConsequenceTier"/> and the enforcement gate).
/// Secure by default: an unset/unknown posture is treated as <see cref="Enforce"/>. Warm-up (OBSERVE/WARN)
/// is a deliberate, opt-in state set by the onboarding controller, never the silent default.
/// </summary>
public static class EnforcementPosture
{
    /// <summary>Compute + record the decision; do NOT block LOW/MEDIUM-consequence actions (monitor mode).</summary>
    public const string Observe = "OBSERVE";

    /// <summary>Like <see cref="Observe"/>, but the would-have-been block is surfaced as an alert to the owner.</summary>
    public const string Warn = "WARN";

    /// <summary>Decisions are binding (the steady state).</summary>
    public const string Enforce = "ENFORCE";

    public static bool IsValid(string? value) => value is Observe or Warn or Enforce;

    /// <summary>Normalize to a known posture. Unset/unknown =&gt; ENFORCE (secure by default).</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Enforce;
        var v = value.Trim().ToUpperInvariant();
        return v is Observe or Warn or Enforce ? v : Enforce;
    }
}

/// <summary>How binding the effective decision was — recorded on every decision so the audit trail
/// always shows what WOULD have happened, even when a warm-up let it through.</summary>
public static class EnforcementMode
{
    /// <summary>The computed decision was applied as-is (steady state, or a high-consequence action).</summary>
    public const string Enforced = "ENFORCED";

    /// <summary>A computed block/step-up was downgraded to ALLOW and only recorded (OBSERVE warm-up).</summary>
    public const string Observed = "OBSERVED";

    /// <summary>Same as <see cref="Observed"/>, plus an alert was raised to the owner (WARN warm-up).</summary>
    public const string Warned = "WARNED";
}

/// <summary>
/// The consequence class of an action — how much damage it can do. HIGH and CRITICAL actions are NEVER
/// subject to warm-up downgrade: they are enforced from day one regardless of posture. This is the
/// invariant that makes "secure-by-default warm-up" safe — danger is a known property of the action /
/// target classified at import time, not something learned by observing.
/// </summary>
public static class ConsequenceTier
{
    /// <summary>Read-only / reversible / trivial. Eligible for warm-up downgrade.</summary>
    public const string Low = "LOW";

    /// <summary>Mutating but recoverable / non-financial. Eligible for warm-up downgrade.</summary>
    public const string Medium = "MEDIUM";

    /// <summary>Money movement, irreversible, or bulk sensitive data. ALWAYS enforced.</summary>
    public const string High = "HIGH";

    /// <summary>Catastrophic blast radius. ALWAYS enforced.</summary>
    public const string Critical = "CRITICAL";

    public static bool IsValid(string? value) => value is Low or Medium or High or Critical;

    /// <summary>High or Critical — always enforced, never warmed up.</summary>
    public static bool IsHighConsequence(string? tier) => Normalize(tier) is High or Critical;

    private static int Rank(string tier) => tier switch
    {
        Low => 0, Medium => 1, High => 2, Critical => 3, _ => 2
    };

    /// <summary>
    /// The most severe of several tiers — used to combine an action's declared consequence with its
    /// target system's, so an action can never under-declare its way below the target it hits. Blank/unknown
    /// inputs are ignored; if nothing is known the result is HIGH (secure by default).
    /// </summary>
    public static string Severest(params string?[] tiers)
    {
        string? best = null;
        var bestRank = -1;
        foreach (var t in tiers)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            var n = Normalize(t);
            if (Rank(n) > bestRank) { bestRank = Rank(n); best = n; }
        }
        return best ?? High;
    }

    /// <summary>Normalize an explicit tier. Unset/unknown =&gt; HIGH (secure by default).</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return High;
        var v = value.Trim().ToUpperInvariant();
        return v is Low or Medium or High or Critical ? v : High;
    }

    /// <summary>
    /// Best-effort classification from a free-text action risk label (the legacy <c>ActionDefinition.Risk</c>),
    /// used only when an action does not declare an explicit consequenceTier. Money-moving / irreversible =&gt;
    /// HIGH; anything unrecognized =&gt; HIGH (secure by default).
    /// </summary>
    public static string FromRiskLabel(string? risk)
    {
        if (string.IsNullOrWhiteSpace(risk)) return High;
        return risk.Trim().ToLowerInvariant() switch
        {
            "critical" => Critical,
            "high" or "financial" or "payment" or "payout" or "irreversible" or "destructive" or "phi" => High,
            "medium" or "pii" or "write" or "mutate" or "update" => Medium,
            "low" or "read" or "readonly" or "info" or "informational" or "lookup" => Low,
            _ => High
        };
    }
}
