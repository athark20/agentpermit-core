using System.Text.Json.Serialization;

namespace AgentPermit.Gateway.Contracts;

/// <summary>
/// Request body for the simulated target system POST /api/simulator/refunds.
/// Only the execution service calls this (authenticated with the internal
/// execution key); agents must never reach it directly.
/// </summary>
public sealed class SimulatorRefundRequest
{
    /// <summary>The execution id — used by the simulator to stay idempotent.</summary>
    [JsonPropertyName("executionId")]
    public Guid ExecutionId { get; set; }

    [JsonPropertyName("actionRequestId")]
    public Guid ActionRequestId { get; set; }

    [JsonPropertyName("customerId")]
    public string CustomerId { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public sealed class SimulatorRefundResponse
{
    [JsonPropertyName("refundId")]
    public string RefundId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("settledAtUtc")]
    public DateTimeOffset SettledAtUtc { get; set; }

    /// <summary>True when this call replayed an already-processed execution id.</summary>
    [JsonPropertyName("duplicate")]
    public bool Duplicate { get; set; }
}
