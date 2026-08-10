using System.Net.Http.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Simulator;

namespace AgentPermit.Gateway.Services;

/// <summary>Configuration for reaching the simulated target system.</summary>
public sealed class SimulatorOptions
{
    /// <summary>Base URL of the simulator. For the single-host slice this is the gateway itself.</summary>
    public string BaseUrl { get; set; } = "http://localhost";

    /// <summary>Relative path of the refund endpoint.</summary>
    public string RefundPath { get; set; } = "/api/simulator/refunds";

    /// <summary>Shared secret proving the caller is the execution service.</summary>
    public string ExecutionKey { get; set; } = string.Empty;
}

public interface ISimulatorClient
{
    Task<SimulatorRefundResponse> IssueRefundAsync(SimulatorRefundRequest request, CancellationToken ct);
}

/// <summary>
/// Calls the refund simulator over HTTP with the internal execution key. Using a
/// real HTTP hop (not an in-process call) is deliberate: it exercises the same
/// authenticated path in tests that the deployed execution service uses.
/// </summary>
public sealed class HttpSimulatorClient(HttpClient http, SimulatorOptions options) : ISimulatorClient
{
    public async Task<SimulatorRefundResponse> IssueRefundAsync(SimulatorRefundRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, options.RefundPath);
        message.Headers.Add(ExecutionAuth.HeaderName, options.ExecutionKey);
        message.Content = JsonContent.Create(request);

        using var response = await http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SimulatorRefundResponse>(cancellationToken: ct)
               ?? throw new InvalidOperationException("Simulator returned an empty response.");
    }
}
