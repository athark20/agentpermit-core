using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPermit.Gateway.Common;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Registry;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Admin console read surface behind the Entra-secured control-plane: the business-action catalog,
/// the runtime request/execution stream, aggregate metrics and the signed receipt trail. All
/// endpoints are gated by <see cref="AdminAuthorizationService"/> (Entra group / app role, or the
/// interim admin key). This is what our own admin UI calls.
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminController(
    IAgentPermitStore store,
    IActionRegistry actions,
    IReceiptSigner signer,
    AdminAuthorizationService auth) : ControllerBase
{
    private static readonly string[] NonContentKeys =
        { "receiptHash", "signature", "signatureAlgorithm", "signingKeyId" };

    // Everything here is read-only, so any workspace member (viewer/approver/agent owner/admin) may see it.
    private async Task<IActionResult?> GuardAsync(CancellationToken ct)
        => await auth.IsMemberAsync(HttpContext, ct) ? null
            : Problem(title: "Workspace access required", detail: "Ask your administrator for a role in AgentPermit.",
                statusCode: StatusCodes.Status403Forbidden);

    /// <summary>The catalog of governed business actions agents can request.</summary>
    [HttpGet("actions")]
    public async Task<IActionResult> Actions(CancellationToken ct)
        => await GuardAsync(ct) ?? Ok(actions.All().Select(a => new
        {
            a.ActionId, a.DisplayName, a.Domain, a.Risk, a.ConsequenceTier, a.TargetSystem
        }));

    /// <summary>Recent action requests — the operational stream (decision + lifecycle status).</summary>
    [HttpGet("requests")]
    public async Task<IActionResult> Requests([FromQuery] int take, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } forbidden) return forbidden;
        var rows = await store.ListRecentActionRequestsAsync(take, ct);
        return Ok(rows.Select(r => new
        {
            r.ActionRequestId, r.CorrelationId, r.ExecutingAgentId, r.RootAgentId, r.InitiatingUserId,
            r.ActionId, r.Status, r.RequestPayloadHash, r.CreatedUtc, r.UpdatedUtc
        }));
    }

    /// <summary>Recent execution attempts against target systems.</summary>
    [HttpGet("executions")]
    public async Task<IActionResult> Executions([FromQuery] int take, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } forbidden) return forbidden;
        var rows = await store.ListRecentExecutionsAsync(take, ct);
        return Ok(rows.Select(e => new
        {
            e.ExecutionId, e.ActionRequestId, e.AdapterCode, e.TargetResourceId, e.Status,
            e.AttemptNumber, e.StartedUtc, e.CompletedUtc, e.ErrorCode, e.ErrorMessage
        }));
    }

    /// <summary>Aggregate request counts for the dashboard.</summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> Metrics(CancellationToken ct)
        => await GuardAsync(ct) ?? Ok(await store.GetMetricsAsync(ct));

    /// <summary>Per-agent activity summary over a window (default 7 days, ?hours=) — the centralized fleet trail.</summary>
    [HttpGet("trail/summary")]
    public async Task<IActionResult> TrailSummary([FromQuery] int hours, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } forbidden) return forbidden;
        var window = Math.Clamp(hours <= 0 ? 168 : hours, 1, 24 * 90);
        return Ok(await store.GetAgentTrailSummaryAsync(DateTimeOffset.UtcNow.AddHours(-window), ct));
    }

    /// <summary>Most recent signed receipts — the evidence trail the admin can inspect.</summary>
    [HttpGet("receipts")]
    public async Task<IActionResult> Receipts([FromQuery] int take, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } forbidden) return forbidden;
        var receipts = await store.ListRecentReceiptsAsync(take, ct);
        return Ok(receipts.Select(r => new
        {
            r.ActionReceiptId,
            r.ActionRequestId,
            r.ReceiptNumber,
            r.Kind,
            r.SequenceNumber,
            r.ReceiptHash,
            r.PreviousReceiptHash,
            signed = !string.IsNullOrEmpty(r.SignatureValue),
            r.CreatedUtc
        }));
    }

    /// <summary>
    /// Independently re-verify a stored receipt: recompute its canonical content hash (tamper check)
    /// and verify the Key Vault signature over that hash (authenticity check).
    /// </summary>
    [HttpGet("receipts/{actionRequestId:guid}/verify")]
    public async Task<IActionResult> Verify(Guid actionRequestId, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } forbidden) return forbidden;

        var receipt = await store.GetReceiptForRequestAsync(actionRequestId, ct);
        if (receipt is null)
            throw new NotFoundException("No receipt exists for that action request.") { Title = "Receipt not found" };

        bool? integrityOk = null;
        string? recomputedHash = null;
        try
        {
            var obj = JsonNode.Parse(receipt.ReceiptJson)!.AsObject();
            foreach (var k in NonContentKeys) obj.Remove(k);
            using var doc = JsonDocument.Parse(obj.ToJsonString());
            recomputedHash = CanonicalJson.Hash(doc.RootElement);
            integrityOk = string.Equals(recomputedHash, receipt.ReceiptHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { integrityOk = false; }

        bool? signatureOk = null;
        if (signer.Enabled && !string.IsNullOrEmpty(receipt.SignatureValue))
            signatureOk = await signer.VerifyAsync(receipt.ReceiptHash, receipt.SignatureValue, ct);

        // Chain continuity: the receipt's previousReceiptHash must be the hash of the receipt at sequence - 1
        // (genesis has none). A gap or mismatch means a receipt was removed or the chain was forked.
        bool? chainOk;
        string? expectedPreviousHash = null;
        if (receipt.SequenceNumber <= 0)
            chainOk = receipt.PreviousReceiptHash is null;
        else
        {
            var predecessor = await store.GetReceiptBySequenceAsync(receipt.SequenceNumber - 1, ct);
            expectedPreviousHash = predecessor?.ReceiptHash;
            chainOk = predecessor is not null
                      && string.Equals(predecessor.ReceiptHash, receipt.PreviousReceiptHash, StringComparison.OrdinalIgnoreCase);
        }

        return Ok(new
        {
            receipt.ReceiptNumber,
            receipt.ActionRequestId,
            receipt.Kind,
            receipt.SequenceNumber,
            storedHash = receipt.ReceiptHash,
            recomputedHash,
            integrityOk,
            signed = !string.IsNullOrEmpty(receipt.SignatureValue),
            signatureOk,
            previousReceiptHash = receipt.PreviousReceiptHash,
            expectedPreviousHash,
            chainOk,
            verified = integrityOk == true && (signatureOk ?? true) && chainOk == true
        });
    }
}
