namespace AgentPermit.Gateway.Domain;

/// <summary>
/// The three deterministic policy outcomes. These string values are the contract
/// shared with the Policy / PolicyDecision / ActionReceipt JSON schemas.
/// </summary>
public static class Decision
{
    public const string Allow = "ALLOW";
    public const string Deny = "DENY";
    public const string RequireApproval = "REQUIRE_APPROVAL";
}

/// <summary>Status persisted on dbo.Execution.</summary>
public static class ExecutionStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string NotExecuted = "NotExecuted";
}

/// <summary>
/// What a receipt attests. An EXECUTION receipt records that the gateway ran an allowed/approved action
/// against its target; a DECISION receipt records a DENY or REQUIRE_APPROVAL verdict at the boundary — the
/// evidence of what an agent <em>tried</em>, signed and chained exactly like an execution.
/// </summary>
public static class ReceiptKind
{
    public const string Execution = "EXECUTION";
    public const string Decision = "DECISION";
}

/// <summary>Status persisted on dbo.ApprovalRequest.</summary>
public static class ApprovalStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
}

/// <summary>Stable reason codes returned to callers and written to the audit trail.</summary>
public static class ReasonCode
{
    public const string AgentNotRegistered = "AGENT_NOT_REGISTERED";
    public const string IdentityRequired = "IDENTITY_REQUIRED";
    public const string AgentIdentityMismatch = "AGENT_IDENTITY_MISMATCH";
    public const string NotFromApim = "NOT_FROM_APIM";

    /// <summary>Agent-token validation is configured and the presented token was missing or rejected.</summary>
    public const string TokenInvalid = "TOKEN_INVALID";

    /// <summary>The body declared an initiatingUserId that disagrees with the delegated token's own user.</summary>
    public const string InitiatingUserMismatch = "INITIATING_USER_MISMATCH";
    public const string UnknownAction = "UNKNOWN_ACTION";
    public const string InvalidParameters = "INVALID_PARAMETERS";
    public const string IdempotencyPayloadMismatch = "IDEMPOTENCY_PAYLOAD_MISMATCH";
    public const string PolicyAllow = "POLICY_ALLOW";
    public const string PolicyDeny = "POLICY_DENY";
    public const string PolicyRequireApproval = "POLICY_REQUIRE_APPROVAL";

    /// <summary>The global kill switch is engaged — all action requests are frozen.</summary>
    public const string SystemFrozen = "SYSTEM_FROZEN";

    /// <summary>An always-on velocity circuit breaker tripped for this agent (runaway protection).</summary>
    public const string CircuitBreaker = "CIRCUIT_BREAKER";

    /// <summary>
    /// The agent is quarantined: every action request is denied until an admin lifts it. Set automatically
    /// when the deny-velocity breaker trips (an agent being told "no" over and over is probing, not working),
    /// or manually by an admin.
    /// </summary>
    public const string AgentQuarantined = "AGENT_QUARANTINED";
}
