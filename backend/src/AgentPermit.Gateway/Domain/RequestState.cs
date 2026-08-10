namespace AgentPermit.Gateway.Domain;

/// <summary>
/// Canonical request lifecycle states. A request moves through validated transitions only —
/// it can never jump (e.g. straight from a policy decision to SUCCEEDED without executing).
/// </summary>
public static class RequestState
{
    public const string Received = "RECEIVED";
    public const string Validated = "VALIDATED";
    public const string PolicyEvaluated = "POLICY_EVALUATED";
    public const string Denied = "DENIED";
    public const string AwaitingApproval = "AWAITING_APPROVAL";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string ExecutionPending = "EXECUTION_PENDING";
    public const string Executing = "EXECUTING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>Validates request state transitions; throws on an illegal move.</summary>
public static class RequestStateMachine
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> Allowed =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [RequestState.Received] = new() { RequestState.Validated, RequestState.Denied },
            [RequestState.Validated] = new() { RequestState.PolicyEvaluated, RequestState.Denied },
            [RequestState.PolicyEvaluated] = new() { RequestState.Denied, RequestState.AwaitingApproval, RequestState.ExecutionPending },
            [RequestState.AwaitingApproval] = new() { RequestState.Approved, RequestState.Rejected, RequestState.Expired, RequestState.Cancelled },
            [RequestState.Approved] = new() { RequestState.ExecutionPending, RequestState.Executing },
            [RequestState.ExecutionPending] = new() { RequestState.Executing, RequestState.Cancelled },
            [RequestState.Executing] = new() { RequestState.Succeeded, RequestState.Failed },
            // Terminal states (no outbound transitions):
            [RequestState.Denied] = new(),
            [RequestState.Rejected] = new(),
            [RequestState.Succeeded] = new(),
            [RequestState.Failed] = new(),
            [RequestState.Expired] = new(),
            [RequestState.Cancelled] = new()
        };

    public static bool CanTransition(string from, string to)
        => Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureCanTransition(string from, string to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Illegal request state transition: {from} -> {to}.");
    }

    /// <summary>The state a freshly persisted request starts in, given its policy decision.</summary>
    public static string InitialFor(string decision) => decision switch
    {
        Decision.Allow => RequestState.ExecutionPending,
        Decision.RequireApproval => RequestState.AwaitingApproval,
        _ => RequestState.Denied
    };
}
