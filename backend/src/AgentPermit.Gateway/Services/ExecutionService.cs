using System.Text.Json;
using AgentPermit.Gateway.Adapters;
using AgentPermit.Gateway.Common;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Registry;

namespace AgentPermit.Gateway.Services;

public sealed class ExecutionOutcome
{
    public required ExecutionRecord Execution { get; init; }
    public required ActionReceiptRecord Receipt { get; init; }
    public bool AlreadyExecuted { get; init; }
}

/// <summary>
/// The policy ALLOWed the action, but the target system rejected it on its own business rule (e.g. vendor
/// on hold, invoice already paid). Distinct from a policy DENY — defense in depth at the target — and
/// surfaced to the caller as a clean "executed: failed" result rather than a 500.
/// </summary>
public sealed class TargetExecutionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Runs an approved/allowed action against its target system via the resolved adapter, and
/// produces a signed receipt. Guarantees: (a) the frozen payload hash is re-verified before
/// execution, and (b) an action never executes twice — the duplicate guard returns the existing receipt.
/// </summary>
public sealed class ExecutionService(
    IAgentPermitStore store,
    IActionAdapterResolver adapters,
    IActionRegistry actions,
    AgentPermit.Gateway.ControlPlane.ITargetSystemRegistry targetSystems,
    ReceiptService receipts,
    ILogger<ExecutionService> logger)
{
    public async Task<ExecutionOutcome> ExecuteAsync(
        ActionRequestRecord request, JsonElement parameters, string decision, string policyVersion,
        CancellationToken ct, IReadOnlyList<Guid>? approvalIds = null)
    {
        // (b) Duplicate-execution guard.
        var priorExecution = await store.FindSucceededExecutionAsync(request.ActionRequestId, ct);
        if (priorExecution is not null)
        {
            var existingReceipt = await store.GetReceiptForRequestAsync(request.ActionRequestId, ct)
                ?? throw new InvalidOperationException("Execution exists without a receipt.");
            logger.LogInformation("Execution already completed for request {RequestId}; returning existing receipt.",
                request.ActionRequestId);
            return new ExecutionOutcome { Execution = priorExecution, Receipt = existingReceipt, AlreadyExecuted = true };
        }

        // (a) Re-verify the payload hash (guards against a payload mutated between decision and execution).
        var recomputed = CanonicalJson.Hash(parameters);
        if (!string.Equals(recomputed, request.RequestPayloadHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Payload hash mismatch: refusing to execute a modified request.");

        // Resolve the target system → its adapter type (from the registry) → the adapter.
        var targetSystem = actions.Find(request.ActionId)?.TargetSystem ?? SimulatorRefundAdapter.Code;
        var adapterCode = targetSystems.Find(targetSystem)?.AdapterType ?? targetSystem;
        var adapter = adapters.Resolve(adapterCode);

        // Transition the request into EXECUTING (validates the move from EXECUTION_PENDING/APPROVED).
        RequestStateMachine.EnsureCanTransition(request.Status, RequestState.Executing);
        await store.UpdateActionRequestStatusAsync(request.ActionRequestId, RequestState.Executing, ct);
        request.Status = RequestState.Executing;

        var execution = new ExecutionRecord
        {
            ExecutionId = Guid.NewGuid(),
            ActionRequestId = request.ActionRequestId,
            AdapterCode = adapter.AdapterCode,
            Status = ExecutionStatus.Running,
            AttemptNumber = 1,
            StartedUtc = DateTimeOffset.UtcNow
        };
        await store.InsertExecutionAsync(execution, ct);

        var result = await adapter.ExecuteAsync(new AdapterRequest
        {
            ExecutionId = execution.ExecutionId,
            ActionRequestId = request.ActionRequestId,
            ActionId = request.ActionId,
            TargetSystem = targetSystem,
            Parameters = parameters,
            CorrelationId = request.CorrelationId
        }, ct);

        if (result.Status != AdapterExecutionStatus.Succeeded)
        {
            execution.Status = ExecutionStatus.Failed;
            execution.CompletedUtc = DateTimeOffset.UtcNow;
            execution.ErrorCode = result.ErrorCode ?? "EXECUTION_FAILED";
            execution.ErrorMessage = result.ErrorMessage;
            await store.UpdateExecutionAsync(execution, ct);
            await store.UpdateActionRequestStatusAsync(request.ActionRequestId, RequestState.Failed, ct);
            logger.LogError("Execution failed for request {RequestId} on {Target}: {Error}",
                request.ActionRequestId, targetSystem, result.ErrorMessage);
            throw new TargetExecutionException(execution.ErrorCode, result.ErrorMessage ?? "The target system rejected the action.");
        }

        execution.Status = ExecutionStatus.Succeeded;
        execution.TargetResourceId = result.TargetResourceId;
        execution.ResultJson = result.ResultJson;
        execution.CompletedUtc = DateTimeOffset.UtcNow;
        await store.UpdateExecutionAsync(execution, ct);

        var receipt = await receipts.CreateAsync(new ReceiptInput
        {
            Request = request,
            Decision = decision,
            PolicyVersion = policyVersion,
            ExecutionStatus = ExecutionStatus.Succeeded,
            TargetResourceId = result.TargetResourceId,
            ApprovalIds = approvalIds ?? Array.Empty<Guid>()
        }, ct);

        await store.UpdateActionRequestStatusAsync(request.ActionRequestId, RequestState.Succeeded, ct);

        return new ExecutionOutcome { Execution = execution, Receipt = receipt };
    }
}
