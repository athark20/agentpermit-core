using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPermit.Gateway.Contracts;

/// <summary>
/// Inbound gateway request. Mirrors schemas/ActionRequest.schema.json.
/// This is the ONLY way an agent may ask the platform to do something; the agent
/// never talks to a target system directly.
/// </summary>
public sealed class ActionRequestDto
{
    [JsonPropertyName("actionId")]
    public string? ActionId { get; set; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    /// <summary>
    /// The CALLING agent's own Microsoft Entra Agent ID (its "badge"). Carried in the body so a shared
    /// connector can identify which specific agent is calling. Trusted only when the request arrives from
    /// a registered AgentPermit connector app; ignored otherwise. When present, <see cref="AgentId"/> is
    /// optional and is derived from the registered agent bound to this identity.
    /// </summary>
    [JsonPropertyName("agentIdentity")]
    public string? AgentIdentity { get; set; }

    [JsonPropertyName("rootAgentId")]
    public string? RootAgentId { get; set; }

    [JsonPropertyName("initiatingUserId")]
    public string? InitiatingUserId { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    /// <summary>Action-specific payload (e.g. customerId/amount/currency/reason for Refund.Issue).</summary>
    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }

    [JsonPropertyName("businessContext")]
    public JsonElement? BusinessContext { get; set; }

    [JsonPropertyName("delegationChain")]
    public List<DelegationStepDto>? DelegationChain { get; set; }
}

public sealed class DelegationStepDto
{
    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }
}
