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
