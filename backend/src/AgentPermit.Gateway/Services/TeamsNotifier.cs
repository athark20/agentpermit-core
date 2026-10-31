using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Services;

public sealed class TeamsOptions
{
    /// <summary>
    /// Incoming-webhook / Power Automate "When a HTTP request is received" URL. When empty,
    /// notifications are skipped (the approval still exists and can be actioned via the API).
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>Public base URL approvers act against (APIM). Shown on the card for reference.</summary>
    public string ApprovalsBaseUrl { get; set; } = string.Empty;
}

public interface ITeamsNotifier
{
    Task NotifyApprovalRequestedAsync(ApprovalRequestRecord approval, ActionRequestRecord request, CancellationToken ct);
}

/// <summary>
/// Posts an Adaptive Card to the configured Teams webhook when a request needs approval.
/// Best-effort: a notification failure is logged but never blocks or fails the request —
/// the approval record is the source of truth and can always be actioned via the API.
/// </summary>
public sealed class HttpTeamsNotifier(HttpClient http, TeamsOptions options, ILogger<HttpTeamsNotifier> logger)
    : ITeamsNotifier
{
    public async Task NotifyApprovalRequestedAsync(
        ApprovalRequestRecord approval, ActionRequestRecord request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.WebhookUrl))
        {
            logger.LogDebug("Approval webhook not configured; skipping notification for {ApprovalId}.",
                approval.ApprovalRequestId);
            return;
        }

        try
        {
            var payload = BuildPayload(approval, request);
            using var resp = await http.PostAsJsonAsync(options.WebhookUrl, payload, ct);
            if (!resp.IsSuccessStatusCode)
                logger.LogWarning("Approval webhook returned {Status} for approval {ApprovalId}.",
                    (int)resp.StatusCode, approval.ApprovalRequestId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to post approval notification for {ApprovalId}.", approval.ApprovalRequestId);
        }
    }

    /// <summary>
    /// Clean JSON for the approval workflow (a Power Automate "When a HTTP request is received"
    /// flow) to build and post the Teams Adaptive Card and call approve/reject back.
    /// </summary>
    private object BuildPayload(ApprovalRequestRecord approval, ActionRequestRecord request)
    {
        using var doc = JsonDocument.Parse(request.RequestPayloadJson);
        var p = doc.RootElement;
        string? Str(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        decimal? Num(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

        var baseUrl = options.ApprovalsBaseUrl.TrimEnd('/');
        return new
        {
            approvalRequestId = approval.ApprovalRequestId,
            actionRequestId = approval.ActionRequestId,
            actionId = request.ActionId,
            agentId = request.ExecutingAgentId,
            customerId = Str("customerId"),
            amount = Num("amount"),
            currency = Str("currency"),
            reason = Str("reason"),
            // The COMPLETE payload, so the card can list every field the approver is signing off on
            // (a bank-detail change must show the account being approved, not just an amount).
            parameters = p.Clone(),
            initiatingUserId = request.InitiatingUserId,
            approverRole = approval.ApproverRole,
            payloadHash = approval.PayloadHash,
            requestedAtUtc = approval.RequestedUtc,
            expiresAtUtc = approval.ExpiresUtc,
            approveUrl = $"{baseUrl}/api/approvals/{approval.ApprovalRequestId}/approve",
            rejectUrl = $"{baseUrl}/api/approvals/{approval.ApprovalRequestId}/reject"
        };
    }
}
