using System.Text.Json;

namespace AgentPermit.Gateway.Adapters;

/// <summary>
/// The sandbox adapter (adapter type SANDBOX). Target systems that have no real downstream connection yet
/// (CRM, HRIS, ITSM, treasury and the other demo systems) point at this adapter: it records the action as
/// executed, mints a sandbox resource id and says plainly that no real system was called. Everything
/// upstream of execution — identity, policy, limits, approvals, receipts — runs exactly as it would for a
/// real target, so a full governed flow can be demonstrated and tested without a live back end.
/// </summary>
public sealed class SandboxActionAdapter(ILogger<SandboxActionAdapter> logger) : ActionAdapterBase
{
    public const string Code = "SANDBOX";
    public override string AdapterCode => Code;

    public override Task<AdapterResult> ExecuteAsync(AdapterRequest request, CancellationToken ct)
    {
        var resourceId = "SBX-" + request.ActionRequestId.ToString("N")[..12].ToUpperInvariant();
        var resultJson = JsonSerializer.Serialize(new
        {
            sandbox = true,
            actionId = request.ActionId,
            targetSystem = request.TargetSystem,
            resourceId,
            note = "Executed against the AgentPermit sandbox — no real downstream system was called."
        });
        logger.LogInformation("Sandbox execute {Action} on {Target} -> {ResourceId}", request.ActionId, request.TargetSystem, resourceId);
        return Task.FromResult(AdapterResult.Ok(resourceId, resultJson));
    }
}
