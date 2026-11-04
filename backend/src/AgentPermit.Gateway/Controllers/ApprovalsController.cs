using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Approver-facing endpoints for REQUIRE_APPROVAL requests. Called by the console and (to read) by the
/// approval workflow that posts the Teams card, not by the Copilot agent. Persona-gated: any workspace
/// member may view; only approvers/admins may decide. The approver recorded on a decision is the person
/// AgentPermit authenticated — their own Microsoft Entra ID sign-in — never a name taken from the body.
/// The shared admin key identifies a service, not a person: it may view approvals, but it can only decide
/// one when <see cref="TeamsOptions.AllowWorkflowDecisions"/> is on (legacy), and the responder the
/// workflow reports is then recorded as unverified.
/// </summary>
[ApiController]
[Route("api/approvals")]
public sealed class ApprovalsController(
    ApprovalService approvals, AccessDirectoryService directory, AuditService audit, TeamsOptions teams,
    ILogger<ApprovalsController> logger) : ControllerBase
{
    private const string WorkflowReportedNote =
        "The approver was reported by the approval workflow and was not verified by AgentPermit.";

    private async Task RequireMemberAsync(CancellationToken ct)
    {
        if (directory.DevOpen || directory.IsServiceCaller(HttpContext)) return;
        var roles = await directory.RolesForAsync(HttpContext, ct);
        if (roles.Count == 0)
            throw new ForbiddenActionException("Only workspace members can view approvals. Ask your administrator for access.")
            { Title = "No access yet" };
    }

    /// <summary>
    /// Who is deciding, and whether AgentPermit verified it. Entra path: the signed-in person, who must hold
    /// the Approver or Admin role (the body is ignored). Shared-key path: refused unless the legacy workflow
    /// mode is on, in which case the responder the workflow reports is used and flagged as unverified.
    /// </summary>
    private async Task<(string ApproverId, bool ReportedByWorkflow)> RequireApproverAsync(
        string? bodyApproverId, CancellationToken ct)
    {
        if (directory.DevOpen)
            return (directory.ResolveApproverIdentity(HttpContext, bodyApproverId), false);

        if (directory.IsServiceCaller(HttpContext))
        {
            if (!teams.AllowWorkflowDecisions)
                throw new ForbiddenActionException(
                    "Approvals must be made by a person signed in with their own work account. Open the request in AgentPermit to approve or decline it.")
                { Title = "Sign in to decide" };

            var reported = directory.ResolveApproverIdentity(HttpContext, bodyApproverId);
            logger.LogWarning(
                "Approval decision made with the shared admin key (Teams:AllowWorkflowDecisions is on). The approver '{Approver}' was reported by the workflow and is not verified.",
                reported);
            return (reported, true);
        }

        var roles = await directory.RolesForAsync(HttpContext, ct);
        if (!roles.Contains("admin") && !roles.Contains("approver"))
            throw new ForbiddenActionException("Only people with the Approver role can approve or decline. Ask your administrator for the Approver role.")
            { Title = "Approver role needed" };
        return (directory.ResolveApproverIdentity(HttpContext, bodyApproverId), false);
    }

    [HttpGet("pending")]
    [ProducesResponseType(typeof(IReadOnlyList<ApprovalSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPending(CancellationToken ct)
    {
        await RequireMemberAsync(ct);
        return Ok(await approvals.GetPendingAsync(ct));
    }

    [HttpGet("{approvalRequestId:guid}")]
    [ProducesResponseType(typeof(ApprovalSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid approvalRequestId, CancellationToken ct)
    {
        await RequireMemberAsync(ct);
        var approval = await approvals.GetAsync(approvalRequestId, ct);
        if (approval is null) throw new NotFoundException("No approval request exists with that id.") { Title = "Approval not found" };
        return Ok(approval);
    }

    [HttpPost("{approvalRequestId:guid}/approve")]
    [ProducesResponseType(typeof(ApprovalDecisionResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve(
        Guid approvalRequestId, [FromBody] ApprovalDecisionRequestDto? body, CancellationToken ct)
    {
        var dto = body ?? new ApprovalDecisionRequestDto();
        // Authorization (and identity binding) happens BEFORE the execution try/catch so a 403 is
        // never masked as a 502.
        var (approverId, reportedByWorkflow) = await RequireApproverAsync(dto.ApproverId, ct);
        dto.ApproverId = approverId;
        try
        {
            var result = await approvals.ApproveAsync(approvalRequestId, dto, ct);
            if (reportedByWorkflow) result.Message = $"{result.Message} {WorkflowReportedNote}";
            await audit.WriteAsync(HttpContext, "approval.approve", "Approval", approvalRequestId.ToString(),
                $"Approved by {Describe(dto.ApproverId, reportedByWorkflow)} — outcome {result.Outcome}");
            return MapOutcome(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Approval {ApprovalId} failed during execution.", approvalRequestId);
            return Problem(title: "Execution failed", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    [HttpPost("{approvalRequestId:guid}/reject")]
    [ProducesResponseType(typeof(ApprovalDecisionResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(
        Guid approvalRequestId, [FromBody] ApprovalDecisionRequestDto? body, CancellationToken ct)
    {
        var dto = body ?? new ApprovalDecisionRequestDto();
        var (approverId, reportedByWorkflow) = await RequireApproverAsync(dto.ApproverId, ct);
        dto.ApproverId = approverId;
        var result = await approvals.RejectAsync(approvalRequestId, dto, ct);
        if (reportedByWorkflow) result.Message = $"{result.Message} {WorkflowReportedNote}";
        await audit.WriteAsync(HttpContext, "approval.reject", "Approval", approvalRequestId.ToString(),
            $"Rejected by {Describe(dto.ApproverId, reportedByWorkflow)} — outcome {result.Outcome}");
        return MapOutcome(result);
    }

    private static string Describe(string? approverId, bool reportedByWorkflow)
        => reportedByWorkflow
            ? $"{approverId} (reported by the approval workflow with the shared key; not verified)"
            : approverId ?? "unknown";

    private IActionResult MapOutcome(ApprovalDecisionResponseDto result) => result.Outcome switch
    {
        ApprovalOutcome.Executed or ApprovalOutcome.Rejected => Ok(result),
        // Counted, but more approvers are needed: accepted, nothing has run yet.
        ApprovalOutcome.ApprovalRecorded => StatusCode(StatusCodes.Status202Accepted, result),
        ApprovalOutcome.NotFound => StatusCode(StatusCodes.Status404NotFound, result),
        ApprovalOutcome.AlreadyDecided or ApprovalOutcome.Expired or ApprovalOutcome.PayloadChanged
            or ApprovalOutcome.AlreadyApprovedByYou
            => StatusCode(StatusCodes.Status409Conflict, result),
        ApprovalOutcome.ApproverRoleRequired or ApprovalOutcome.SeparationOfDuties
            => StatusCode(StatusCodes.Status403Forbidden, result),
        _ => StatusCode(StatusCodes.Status502BadGateway, result)
    };
}
