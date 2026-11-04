using System.Text.Json;
using AgentPermit.Gateway.Registry;

namespace AgentPermit.Gateway.Policies;

/// <summary>Everything the policy engine needs to evaluate a request (beyond the raw payload).</summary>
public sealed class PolicyEvaluationContext
{
    public required ActionDefinition Action { get; init; }
    public required JsonElement Parameters { get; init; }
    public required string AgentId { get; init; }
    public string? RootAgentId { get; init; }
    public string? InitiatingUserId { get; init; }

    /// <summary>
    /// Pre-computed windowed aggregates keyed by <see cref="PolicyAggregate.Key"/>, supplied by
    /// <see cref="PolicyAggregateProvider"/> for every aggregate condition the active snapshot has for this
    /// action. Keeps the engine itself synchronous and deterministic; null when the snapshot uses none.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? Aggregates { get; init; }
}

/// <summary>The outcome of evaluating a request against policy.</summary>
public sealed class PolicyEvaluationResult
{
    public required string Decision { get; init; }        // ALLOW | DENY | REQUIRE_APPROVAL
    public required string PolicyCode { get; init; }
    public required string PolicyVersion { get; init; }
    public required string ReasonCode { get; init; }
    public required string Reason { get; init; }
    public string? MatchedRuleId { get; init; }
    public string[] RequiredApproverRoles { get; init; } = Array.Empty<string>();

    /// <summary>For REQUIRE_APPROVAL: how many different people must approve (the matched rule's requiredApprovals, default 1).</summary>
    public int RequiredApprovals { get; init; } = 1;
    public required string TraceJson { get; init; }
}

/// <summary>
/// Deterministic policy evaluation. The runtime implementation
/// (<see cref="ConfigurablePolicyEngine"/>) evaluates a versioned, immutable policy
/// snapshot — thresholds are data, not code.
/// </summary>
public interface IPolicyEngine
{
    PolicyEvaluationResult Evaluate(PolicyEvaluationContext context);
}
