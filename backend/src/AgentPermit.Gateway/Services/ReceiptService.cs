using System.Text.Json;
using AgentPermit.Gateway.Common;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Services;

/// <summary>Inputs needed to mint a receipt (keeps the call site readable).</summary>
public sealed class ReceiptInput
{
    public required ActionRequestRecord Request { get; init; }
    public required string Decision { get; init; }
    public required string PolicyVersion { get; init; }
    public required string ExecutionStatus { get; init; }
    public string? TargetResourceId { get; init; }
    public IReadOnlyList<Guid> ApprovalIds { get; init; } = Array.Empty<Guid>();
}

/// <summary>
/// Builds tamper-evident, hash-chained action receipts. The document shape follows
/// schemas/ActionReceipt.schema.json; each receipt's hash incorporates the previous
/// receipt's hash, so any retroactive edit breaks the chain.
/// </summary>
public sealed class ReceiptService(IAgentPermitStore store, IReceiptSigner signer)
{
    public async Task<ActionReceiptRecord> CreateAsync(ReceiptInput input, CancellationToken ct)
    {
        var previousHash = await store.GetLatestReceiptHashAsync(ct);
        var receiptId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var req = input.Request;

        // Canonical, order-independent content. previousReceiptHash is folded in so the
        // hash chains to its predecessor.
        var content = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["receiptId"] = receiptId,
            ["actionRequestId"] = req.ActionRequestId,
            ["agentId"] = req.ExecutingAgentId,
            ["rootAgentId"] = req.RootAgentId,
            ["initiatingUserId"] = req.InitiatingUserId,
            ["actionId"] = req.ActionId,
            ["decision"] = input.Decision,
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

        var record = new ActionReceiptRecord
        {
            ActionReceiptId = receiptId,
            ActionRequestId = req.ActionRequestId,
            ReceiptNumber = $"RCPT-{receiptId:N}"[..21].ToUpperInvariant(),
            ReceiptJson = receiptJson,
            ReceiptHash = receiptHash,
            PreviousReceiptHash = previousHash,
            SignatureValue = signature?.Value,
            CreatedUtc = createdAt
        };

        await store.InsertReceiptAsync(record, ct);
        return record;
    }

    public static ReceiptDto ToDto(ActionReceiptRecord r, string payloadHash) => new()
    {
        ReceiptId = r.ActionReceiptId,
        ReceiptNumber = r.ReceiptNumber,
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
