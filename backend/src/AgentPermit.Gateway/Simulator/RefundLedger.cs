using System.Collections.Concurrent;
using AgentPermit.Gateway.Contracts;

namespace AgentPermit.Gateway.Simulator;

/// <summary>
/// The simulated external target system's own store. It records a refund exactly
/// once per execution id (idempotent), independent of the gateway's audit tables.
/// Real target systems are external; this stands in for one during the vertical slice.
/// </summary>
public interface IRefundLedger
{
    SimulatorRefundResponse Record(SimulatorRefundRequest request);
    int Count { get; }
    bool TryGet(Guid executionId, out SimulatorRefundResponse response);
}

public sealed class InMemoryRefundLedger : IRefundLedger
{
    private readonly ConcurrentDictionary<Guid, SimulatorRefundResponse> _byExecution = new();
    private int _sequence;

    public int Count => _byExecution.Count;

    public SimulatorRefundResponse Record(SimulatorRefundRequest request)
    {
        var wasNew = false;
        var stored = _byExecution.GetOrAdd(request.ExecutionId, _ =>
        {
            wasNew = true;
            var seq = Interlocked.Increment(ref _sequence);
            return new SimulatorRefundResponse
            {
                RefundId = $"RF-{seq:D6}",
                Status = "Settled",
                Amount = request.Amount,
                Currency = request.Currency,
                SettledAtUtc = DateTimeOffset.UtcNow,
                Duplicate = false
            };
        });

        // Idempotent: a repeated executionId returns the original refund, flagged duplicate.
        if (wasNew) return stored;
        return new SimulatorRefundResponse
        {
            RefundId = stored.RefundId,
            Status = stored.Status,
            Amount = stored.Amount,
            Currency = stored.Currency,
            SettledAtUtc = stored.SettledAtUtc,
            Duplicate = true
        };
    }

    public bool TryGet(Guid executionId, out SimulatorRefundResponse response)
        => _byExecution.TryGetValue(executionId, out response!);
}
