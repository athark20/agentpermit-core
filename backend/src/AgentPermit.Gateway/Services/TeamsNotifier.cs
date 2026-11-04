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

    /// <summary>
    /// Base URL of the AgentPermit console, e.g. <c>https://agentpermit.contoso.com</c>. Each notification
    /// carries <c>reviewUrl</c> = <c>{ConsoleBaseUrl}/#/approvals/{approvalRequestId}</c>: the approver opens
    /// it from the Teams card, signs in with Microsoft Entra ID and decides there, so the decision carries
    /// their own sign-in. Empty = no review link in the notification.
    /// </summary>
    public string ConsoleBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Legacy: let the approval workflow approve or reject with the shared admin key, reporting who responded
    /// in Teams. Off by default, because anyone holding the key could then approve as anyone. Turn it on only
    /// while an existing Power Automate flow is moved over to the review link. Decisions made this way are
    /// recorded as reported by the workflow, not verified by AgentPermit.
    /// </summary>
    public bool AllowWorkflowDecisions { get; set; } = false;

    /// <summary>The console deep link for one approval, or null when <see cref="ConsoleBaseUrl"/> is not set.</summary>
    public string? ReviewUrl(Guid approvalRequestId)
        => string.IsNullOrWhiteSpace(ConsoleBaseUrl)
            ? null
            : $"{ConsoleBaseUrl.TrimEnd('/')}/#/approvals/{approvalRequestId}";
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
    /// Clean JSON for the approval workflow (a Power Automate "When a HTTP request is received" flow) to
    /// build and post the Teams Adaptive Card. By default the card is a notification: its button opens
    /// <c>reviewUrl</c>, where the approver signs in and decides. Only in the legacy workflow mode does the
    /// flow call <c>approveUrl</c>/<c>rejectUrl</c> back itself.
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
            // The same fields as title/value pairs, ready to drop into an Adaptive Card FactSet.
            facts = Facts(p),
            initiatingUserId = request.InitiatingUserId,
            approverRole = approval.ApproverRole,
            payloadHash = approval.PayloadHash,
            requestedAtUtc = approval.RequestedUtc,
            expiresAtUtc = approval.ExpiresUtc,
            // Where the decision is made. "console" (default): the card's button opens reviewUrl and the
            // approver decides with their own Entra ID sign-in; the shared key cannot decide. "workflow"
            // (legacy, Teams:AllowWorkflowDecisions): the flow calls approveUrl/rejectUrl with the key.
            decisionMode = options.AllowWorkflowDecisions ? "workflow" : "console",
            reviewUrl = options.ReviewUrl(approval.ApprovalRequestId),
            approveUrl = $"{baseUrl}/api/approvals/{approval.ApprovalRequestId}/approve",
            rejectUrl = $"{baseUrl}/api/approvals/{approval.ApprovalRequestId}/reject"
        };
    }

    /// <summary>
    /// Every top-level payload field as a <c>{ title, value }</c> pair. Strings are shown as sent, numbers
    /// keep their exact text (15750.00 stays 15750.00) and nested values are shown as compact JSON.
    /// </summary>
    private static List<FactDto> Facts(JsonElement payload)
    {
        var facts = new List<FactDto>();
        if (payload.ValueKind != JsonValueKind.Object) return facts;
        foreach (var field in payload.EnumerateObject())
        {
            var value = field.Value.ValueKind switch
            {
                JsonValueKind.String => field.Value.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                _ => field.Value.GetRawText()
            };
            facts.Add(new FactDto(field.Name, value));
        }
        return facts;
    }

    private sealed record FactDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("title")] string Title,
        [property: System.Text.Json.Serialization.JsonPropertyName("value")] string Value);
}
