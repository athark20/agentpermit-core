using System.Text.Json;
using AgentPermit.Gateway.Common;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Services;

/// <summary>
/// Human-in-the-loop for REQUIRE_APPROVAL requests. Approving runs the frozen,
/// hash-verified request through the execution service (which itself prevents
/// duplicate execution); rejecting stops it. Expiry and payload-tamper are
/// enforced before anything executes.
/// </summary>
public sealed class ApprovalService(
    IAgentPermitStore store,
    ExecutionService execution,
    ILogger<ApprovalService> logger)
{
    public async Task<IReadOnlyList<ApprovalSummaryDto>> GetPendingAsync(CancellationToken ct)
    {
        var pending = await store.GetPendingApprovalsAsync(ct);
        var list = new List<ApprovalSummaryDto>(pending.Count);
        foreach (var a in pending)
        {
            var request = await store.GetActionRequestAsync(a.ActionRequestId, ct);
            if (request is not null) list.Add(ToSummary(a, request));
        }
        return list;
    }

    public async Task<ApprovalSummaryDto?> GetAsync(Guid approvalRequestId, CancellationToken ct)
    {
        var approval = await store.GetApprovalAsync(approvalRequestId, ct);
        if (approval is null) return null;
        var request = await store.GetActionRequestAsync(approval.ActionRequestId, ct);
        return request is null ? null : ToSummary(approval, request);
    }

    public async Task<ApprovalDecisionResponseDto> ApproveAsync(
        Guid approvalRequestId, ApprovalDecisionRequestDto body, CancellationToken ct)
    {
        var (approval, request, guard) = await LoadAndValidateAsync(approvalRequestId, ct);
        if (guard is not null) return guard;

        // Tamper protection: the payload being executed must be exactly what was approved.
        var currentHash = CanonicalJson.Hash(ParsePayload(request!));
        var frozenHash = approval!.PayloadHash;
        var expected = body.ExpectedPayloadHash;

        if (!HashEquals(currentHash, frozenHash) ||
            !HashEquals(request!.RequestPayloadHash, frozenHash) ||
            (expected is not null && !HashEquals(expected, frozenHash)))
        {
            logger.LogWarning("Approval {ApprovalId} refused: payload changed since approval was requested.", approvalRequestId);
            return Result(approval, ApprovalOutcome.PayloadChanged, ApprovalStatus.Pending,
                "The request payload has changed since approval was requested; execution refused.");
        }

        // Record the approval decision and move the request to APPROVED.
        approval.Status = ApprovalStatus.Approved;
        approval.ApproverId = body.ApproverId;
        approval.RespondedUtc = DateTimeOffset.UtcNow;
        approval.ResponseComment = body.Comment;
        await store.UpdateApprovalAsync(approval, ct);

        if (request!.Status == RequestState.AwaitingApproval)
        {
            await store.UpdateActionRequestStatusAsync(request.ActionRequestId, RequestState.Approved, ct);
            request.Status = RequestState.Approved;
        }

        // Execute the frozen request. ExecutionService re-verifies the hash and prevents
        // duplicate execution, so a repeated approve returns the same receipt.
        var decision = await store.GetLatestPolicyDecisionAsync(request.ActionRequestId, ct);
        var outcome = await execution.ExecuteAsync(
            request, ParsePayload(request), decision?.Decision ?? Decision.RequireApproval,
            decision?.PolicyVersion ?? "unknown", ct, approvalIds: new[] { approval.ApprovalRequestId });

        var response = Result(approval, ApprovalOutcome.Executed, RequestState.Succeeded,
            outcome.AlreadyExecuted ? "Already executed; returning the existing receipt." : "Approved and executed.");
        response.Receipt = ReceiptService.ToDto(outcome.Receipt, request.RequestPayloadHash);
        return response;
    }

    public async Task<ApprovalDecisionResponseDto> RejectAsync(
        Guid approvalRequestId, ApprovalDecisionRequestDto body, CancellationToken ct)
    {
        var (approval, request, guard) = await LoadAndValidateAsync(approvalRequestId, ct);
        if (guard is not null) return guard;

        approval!.Status = ApprovalStatus.Rejected;
        approval.ApproverId = body.ApproverId;
        approval.RespondedUtc = DateTimeOffset.UtcNow;
        approval.ResponseComment = body.Comment;
        await store.UpdateApprovalAsync(approval, ct);
        RequestStateMachine.EnsureCanTransition(request!.Status, RequestState.Rejected);
        await store.UpdateActionRequestStatusAsync(request.ActionRequestId, RequestState.Rejected, ct);

        logger.LogInformation("Approval {ApprovalId} rejected; request {RequestId} will not execute.",
            approvalRequestId, request.ActionRequestId);
        return Result(approval, ApprovalOutcome.Rejected, RequestState.Rejected, "Rejected; not executed.");
    }

    /// <summary>Shared load + status/expiry checks. Returns a guard response if the decision can't proceed.</summary>
    private async Task<(ApprovalRequestRecord?, ActionRequestRecord?, ApprovalDecisionResponseDto?)>
        LoadAndValidateAsync(Guid approvalRequestId, CancellationToken ct)
    {
        var approval = await store.GetApprovalAsync(approvalRequestId, ct);
        if (approval is null)
            return (null, null, NotFound(approvalRequestId));

        if (approval.Status != ApprovalStatus.Pending)
            return (approval, null, Result(approval, ApprovalOutcome.AlreadyDecided, approval.Status,
                $"This approval was already {approval.Status.ToLowerInvariant()}."));

        if (approval.ExpiresUtc is { } expires && expires < DateTimeOffset.UtcNow)
        {
            approval.Status = ApprovalStatus.Expired;
            await store.UpdateApprovalAsync(approval, ct);
            if (RequestStateMachine.CanTransition(RequestState.AwaitingApproval, RequestState.Expired))
                await store.UpdateActionRequestStatusAsync(approval.ActionRequestId, RequestState.Expired, ct);
            return (approval, null, Result(approval, ApprovalOutcome.Expired, RequestState.Expired,
                "This approval has expired and can no longer be actioned."));
        }

        var request = await store.GetActionRequestAsync(approval.ActionRequestId, ct);
        if (request is null)
            return (approval, null, Result(approval, ApprovalOutcome.NotFound, approval.Status,
                "The underlying action request was not found."));

        return (approval, request, null);
    }

    private static ApprovalSummaryDto ToSummary(ApprovalRequestRecord a, ActionRequestRecord r)
    {
        var p = ParsePayload(r);
        return new ApprovalSummaryDto
        {
            ApprovalRequestId = a.ApprovalRequestId,
            ActionRequestId = a.ActionRequestId,
            ActionId = r.ActionId,
            AgentId = r.ExecutingAgentId,
            Amount = TryDecimal(p, "amount"),
            Currency = TryString(p, "currency"),
            CustomerId = TryString(p, "customerId"),
            Reason = TryString(p, "reason"),
            Status = a.Status,
            ApproverRole = a.ApproverRole,
            PayloadHash = a.PayloadHash,
            RequestedAtUtc = a.RequestedUtc,
            ExpiresAtUtc = a.ExpiresUtc
        };
    }

    private static ApprovalDecisionResponseDto Result(
        ApprovalRequestRecord approval, string outcome, string status, string message) => new()
    {
        ApprovalRequestId = approval.ApprovalRequestId,
        ActionRequestId = approval.ActionRequestId,
        Outcome = outcome,
        Status = status,
        Message = message
    };

    private static ApprovalDecisionResponseDto NotFound(Guid id) => new()
    {
        ApprovalRequestId = id,
        Outcome = ApprovalOutcome.NotFound,
        Status = ApprovalOutcome.NotFound,
        Message = "Approval request not found."
    };

    private static JsonElement ParsePayload(ActionRequestRecord r)
    {
        using var doc = JsonDocument.Parse(r.RequestPayloadJson);
        return doc.RootElement.Clone();
    }

    private static bool HashEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? TryString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static decimal? TryDecimal(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : null;
}
