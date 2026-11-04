using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Approver-facing endpoints for REQUIRE_APPROVAL requests. Called by the console and by the approval
/// workflow (Teams Adaptive Card via Power Automate), not by the Copilot agent. Persona-gated: any
/// workspace member may view; only approvers/admins (or the service key path) may decide. The
/// approver identity recorded on a decision is DERIVED from the authenticated caller — never trusted
/// from the body — except on the service-key path where the Teams responder is passed through.
/// </summary>
[ApiController]
[Route("api/approvals")]
public sealed class ApprovalsController(
    ApprovalService approvals, AccessDirectoryService directory, AuditService audit, ILogger<ApprovalsController> logger) : ControllerBase
{
    private async Task RequireMemberAsync(CancellationToken ct)
    {
        if (directory.DevOpen || directory.IsServiceCaller(HttpContext)) return;
        var roles = await directory.RolesForAsync(HttpContext, ct);
        if (roles.Count == 0)
            throw new ForbiddenActionException("Only workspace members can view approvals. Ask your administrator for access.")
            { Title = "No access yet" };
    }

    private async Task<string> RequireApproverAsync(string? bodyApproverId, CancellationToken ct)
    {
        if (directory.DevOpen || directory.IsServiceCaller(HttpContext))
            return directory.ResolveApproverIdentity(HttpContext, bodyApproverId);
        var roles = await directory.RolesForAsync(HttpContext, ct);
        if (!roles.Contains("admin") && !roles.Contains("approver"))
            throw new ForbiddenActionException("Only people with the Approver role can approve or decline. Ask your administrator for the Approver role.")
            { Title = "Approver role needed" };
        return directory.ResolveApproverIdentity(HttpContext, bodyApproverId);
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
        dto.ApproverId = await RequireApproverAsync(dto.ApproverId, ct);
        try
        {
            var result = await approvals.ApproveAsync(approvalRequestId, dto, ct);
            await audit.WriteAsync(HttpContext, "approval.approve", "Approval", approvalRequestId.ToString(),
                $"Approved by {dto.ApproverId} — outcome {result.Outcome}");
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
        dto.ApproverId = await RequireApproverAsync(dto.ApproverId, ct);
        var result = await approvals.RejectAsync(approvalRequestId, dto, ct);
        await audit.WriteAsync(HttpContext, "approval.reject", "Approval", approvalRequestId.ToString(),
            $"Rejected by {dto.ApproverId} — outcome {result.Outcome}");
        return MapOutcome(result);
    }

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
