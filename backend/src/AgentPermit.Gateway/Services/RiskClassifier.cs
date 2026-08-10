using AgentPermit.Gateway.ControlPlane;
using AgentPermit.Gateway.Domain;

namespace AgentPermit.Gateway.Services;

/// <summary>A classification result with its contributing reasons (explainable — a CISO can defend it).</summary>
public sealed class RiskAssessment
{
    public required string Tier { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>
/// Deterministic, explainable risk classification of a target system into a consequence tier. This is the
/// import-time "danger is known up front" step: a payout/refund/PII system is HIGH the moment it is
/// registered, so its actions are enforced from day one and never eligible for a warm-up downgrade.
///
/// Keyword signals over the target's code / adapter / name / owner: any HIGH signal wins; else MEDIUM; else
/// LOW; else — when nothing is recognized — HIGH (secure by default: an unclassified target fails closed).
/// This seeds a human-reviewable value; an explicit consequenceTier on the record always overrides it.
/// </summary>
public sealed class RiskClassifier
{
    public RiskAssessment ClassifyTarget(TargetSystemRecord target)
    {
        var text = string.Join(' ', new[]
        {
            target.TargetSystemCode, target.AdapterType, target.DisplayName, target.OperationalOwner
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var (tier, hits) = RiskKeywords.Classify(text);
        return tier is null
            ? new RiskAssessment
            {
                Tier = ConsequenceTier.High,
                Reasons = new[] { "No recognized signal — defaulted to HIGH (secure by default; needs human review)." }
            }
            : new RiskAssessment { Tier = tier, Reasons = new[] { $"Matched {tier} signal(s): {string.Join(", ", hits)}." } };
    }
}
