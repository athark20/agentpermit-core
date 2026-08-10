using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Services;

namespace AgentPermit.Gateway.Adapters;

/// <summary>
/// Adapter #1: the refund simulator (target system REFUND_SIMULATOR). Maps the action parameters
/// to the simulator's contract and calls it over the authenticated internal channel.
/// </summary>
public sealed class SimulatorRefundAdapter(ISimulatorClient simulator, ILogger<SimulatorRefundAdapter> logger)
    : ActionAdapterBase
{
    public const string Code = "REFUND_SIMULATOR";
    public override string AdapterCode => Code;

    public override async Task<AdapterResult> ExecuteAsync(AdapterRequest request, CancellationToken ct)
    {
        try
        {
            var p = request.Parameters;
            var refund = new SimulatorRefundRequest
            {
                ExecutionId = request.ExecutionId,
                ActionRequestId = request.ActionRequestId,
                CustomerId = Str(p, "customerId"),
                Amount = Num(p, "amount"),
                Currency = Str(p, "currency"),
                Reason = TryStr(p, "reason")
            };
            var response = await simulator.IssueRefundAsync(refund, ct);
            return AdapterResult.Ok(response.RefundId, JsonSerializer.Serialize(response));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "REFUND_SIMULATOR adapter failed for execution {ExecutionId}.", request.ExecutionId);
            return AdapterResult.Fail("TARGET_CALL_FAILED", ex.Message);
        }
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
    private static string? TryStr(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
}
