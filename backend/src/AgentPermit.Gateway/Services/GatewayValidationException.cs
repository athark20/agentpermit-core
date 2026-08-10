namespace AgentPermit.Gateway.Services;

/// <summary>
/// Thrown for malformed requests that never reach policy evaluation (missing
/// required envelope fields, bad correlation id). The controller maps this to
/// HTTP 400. Business rejections (unregistered agent, unknown action, policy
/// DENY) are NOT exceptions — they are audited decisions returned with 200.
/// </summary>
public sealed class GatewayValidationException(string message) : Exception(message);
