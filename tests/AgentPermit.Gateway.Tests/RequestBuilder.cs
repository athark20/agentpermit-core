namespace AgentPermit.Gateway.Tests;

/// <summary>Convenience builders for action request bodies used by the smoke tests.</summary>
public static class RequestBuilder
{
    public const string RegisteredAgentId = "copilot-refund-demo";
    public const string RefundActionId = "Refund.Issue";

    public static object Refund(
        decimal amount,
        string agentId = RegisteredAgentId,
        string actionId = RefundActionId,
        string? idempotencyKey = null,
        string customerId = "CUST-1001",
        string currency = "AED",
        string reason = "Service failure")
        => new
        {
            actionId,
            agentId,
            correlationId = Guid.NewGuid().ToString(),
            idempotencyKey = idempotencyKey ?? $"idem-{Guid.NewGuid():N}",
            parameters = new { customerId, amount, currency, reason }
        };

    /// <summary>
    /// A refund body that carries the calling agent's own Entra Agent ID (badge) in the body, the way a
    /// shared connector does. <paramref name="agentId"/> is omitted from the payload entirely when null.
    /// </summary>
    public static Dictionary<string, object?> RefundBadge(
        decimal amount,
        string agentIdentity,
        string? agentId = null,
        string actionId = RefundActionId,
        string? idempotencyKey = null,
        string customerId = "CUST-1001",
        string currency = "AED",
        string reason = "Service failure")
    {
        var body = new Dictionary<string, object?>
        {
            ["actionId"] = actionId,
            ["agentIdentity"] = agentIdentity,
            ["correlationId"] = Guid.NewGuid().ToString(),
            ["idempotencyKey"] = idempotencyKey ?? $"idem-{Guid.NewGuid():N}",
            ["parameters"] = new { customerId, amount, currency, reason }
        };
        if (agentId is not null) body["agentId"] = agentId;
        return body;
    }
}
