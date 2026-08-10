namespace AgentPermit.Gateway.Simulator;

/// <summary>
/// Shared secret gate for the simulated target system. The refund simulator ONLY
/// honours calls carrying this header with the configured key — which only the
/// execution service knows. This is what makes "direct target-system access fails"
/// (MVP test 11) true: an agent hitting /api/simulator/refunds without the key is rejected.
/// </summary>
public static class ExecutionAuth
{
    public const string HeaderName = "X-Execution-Key";
}
