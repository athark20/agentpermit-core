using System.Text.Json;
using AgentPermit.Gateway.Common;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Services;

/// <summary>Receipt signing / evidence options (bound from the <c>Receipts</c> configuration section).</summary>
public sealed class ReceiptOptions
{
    /// <summary>Key Vault key id used to RS256-sign receipts; empty = unsigned (dev).</summary>
    public string SigningKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Also mint a DECISION receipt for DENY and REQUIRE_APPROVAL outcomes of a resolved agent (default on).
    /// This is the evidence of what an agent <em>tried</em>; every 2026 incident post-mortem was missing it.
    /// Turn off only if signing cost matters more than attempt evidence.
    /// </summary>
    public bool SignDecisions { get; set; } = true;
}

/// <summary>Inputs needed to mint a receipt (keeps the call site readable).</summary>
public sealed class ReceiptInput
{
    public required ActionRequestRecord Request { get; init; }
    public required string Decision { get; init; }
    public required string PolicyVersion { get; init; }
    public required string ExecutionStatus { get; init; }
    public string? TargetResourceId { get; init; }
    public IReadOnlyList<Guid> ApprovalIds { get; init; } = Array.Empty<Guid>();

    /// <summary>EXECUTION (default) or DECISION.</summary>
    public string Kind { get; init; } = ReceiptKind.Execution;

    /// <summary>Stable reason code for a DECISION receipt (e.g. POLICY_DENY, AGENT_QUARANTINED).</summary>
    public string? ReasonCode { get; init; }
}

/// <summary>
/// Builds tamper-evident, hash-chained action receipts. The document shape follows
/// schemas/ActionReceipt.schema.json; each receipt's hash incorporates the previous
/// receipt's hash and its own sequence number, so any retroactive edit — or a deleted
/// receipt between two that are presented — breaks the chain.
///
/// Chaining is serialised: within a process by a single-writer gate, across replicas by the store's
/// unique constraints (sequence number, previous hash). A writer that loses the race gets
/// <see cref="ReceiptChainConflictException"/>, re-reads the head and retries, so two gateways appending at
/// the same instant can never produce two receipts that both claim the same predecessor.
/// </summary>
public sealed class ReceiptService(IAgentPermitStore store, IReceiptSigner signer, ILogger<ReceiptService> logger)
{
    private static readonly SemaphoreSlim ChainGate = new(1, 1);
    private const int MaxAttempts = 6;

    public async Task<ActionReceiptRecord> CreateAsync(ReceiptInput input, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await ChainGate.WaitAsync(ct);
            try
            {
                var head = await store.GetReceiptChainHeadAsync(ct);
                var record = await BuildAsync(input, head, ct);
                try
                {
                    await store.InsertReceiptAsync(record, ct);
                    return record;
                }
                catch (ReceiptChainConflictException ex) when (attempt < MaxAttempts)
                {
                    // Another replica appended first. Re-read the head and chain onto the new tail.
                    logger.LogInformation("Receipt chain head moved (attempt {Attempt}); retrying: {Message}", attempt, ex.Message);
                }
            }
            finally
            {
                ChainGate.Release();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt), ct);
        }
    }

    private async Task<ActionReceiptRecord> BuildAsync(ReceiptInput input, ReceiptChainHead head, CancellationToken ct)
    {
        var previousHash = head.Hash;
        var sequence = head.Sequence + 1; // genesis receipt is 0
        var receiptId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var req = input.Request;

        // Canonical, order-independent content. previousReceiptHash and sequenceNumber are folded in so
        // the hash chains to its predecessor and pins its position.
        var content = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["receiptId"] = receiptId,
            ["kind"] = input.Kind,
            ["sequenceNumber"] = sequence,
            ["actionRequestId"] = req.ActionRequestId,
            ["agentId"] = req.ExecutingAgentId,
            ["rootAgentId"] = req.RootAgentId,
            ["initiatingUserId"] = req.InitiatingUserId,
            ["actionId"] = req.ActionId,
            ["decision"] = input.Decision,
            ["reasonCode"] = input.ReasonCode,
            ["policyVersion"] = input.PolicyVersion,
            ["payloadHash"] = req.RequestPayloadHash,
            ["approvalIds"] = input.ApprovalIds,
            ["executionStatus"] = input.ExecutionStatus,
            ["targetResourceId"] = input.TargetResourceId,
            ["previousReceiptHash"] = previousHash,
            ["createdAtUtc"] = createdAt
        };

        var canonicalContent = CanonicalJson.Serialize(ToElement(content));
        var receiptHash = CanonicalJson.HashRaw(canonicalContent);

        // Sign the content hash with the Key Vault key (private key never leaves KV).
        var signature = await signer.SignAsync(receiptHash, ct);

        // The stored document is the full receipt (schema-shaped), including its hash + signature.
        var full = new Dictionary<string, object?>(content)
        {
            ["receiptHash"] = receiptHash,
            ["signature"] = signature?.Value,
            ["signatureAlgorithm"] = signature?.Algorithm,
            ["signingKeyId"] = signature?.KeyId
        };
        var receiptJson = JsonSerializer.Serialize(full, JsonOpts);

        return new ActionReceiptRecord
        {
            ActionReceiptId = receiptId,
            ActionRequestId = req.ActionRequestId,
            ReceiptNumber = $"RCPT-{receiptId:N}"[..21].ToUpperInvariant(),
            ReceiptJson = receiptJson,
            ReceiptHash = receiptHash,
            PreviousReceiptHash = previousHash,
            SignatureValue = signature?.Value,
            CreatedUtc = createdAt,
            SequenceNumber = sequence,
            Kind = input.Kind
        };
    }

    public static ReceiptDto ToDto(ActionReceiptRecord r, string payloadHash) => new()
    {
        ReceiptId = r.ActionReceiptId,
        ReceiptNumber = r.ReceiptNumber,
        Kind = r.Kind,
        SequenceNumber = r.SequenceNumber,
        ExecutionStatus = ReadExecutionStatus(r.ReceiptJson),
        TargetResourceId = ReadTargetResourceId(r.ReceiptJson),
        PayloadHash = payloadHash,
        ReceiptHash = r.ReceiptHash,
        PreviousReceiptHash = r.PreviousReceiptHash,
        Signature = r.SignatureValue,
        SignatureAlgorithm = ReadString(r.ReceiptJson, "signatureAlgorithm"),
        SigningKeyId = ReadString(r.ReceiptJson, "signingKeyId"),
        CreatedAtUtc = r.CreatedUtc
    };

    private static string? ReadString(string json, string prop)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static string ReadExecutionStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("executionStatus", out var el) ? el.GetString() ?? "" : "";
    }

    private static string? ReadTargetResourceId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("targetResourceId", out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static JsonElement ToElement(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
