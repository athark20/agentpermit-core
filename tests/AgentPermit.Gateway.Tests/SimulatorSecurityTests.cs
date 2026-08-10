using System.Net;
using System.Net.Http.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Simulator;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// MVP #11: the target system cannot be reached directly. Only a caller presenting
/// the internal execution key (i.e. the execution service) may record a refund.
/// </summary>
public sealed class SimulatorSecurityTests
{
    private static SimulatorRefundRequest SampleRefund() => new()
    {
        ExecutionId = Guid.NewGuid(),
        ActionRequestId = Guid.NewGuid(),
        CustomerId = "CUST-1001",
        Amount = 100m,
        Currency = "AED",
        Reason = "Direct call attempt"
    };

    [Fact]
    public async Task DirectCall_WithoutKey_IsRejected()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/simulator/refunds", SampleRefund());

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task DirectCall_WithWrongKey_IsRejected()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var message = new HttpRequestMessage(HttpMethod.Post, "/api/simulator/refunds")
        {
            Content = JsonContent.Create(SampleRefund())
        };
        message.Headers.Add(ExecutionAuth.HeaderName, "not-the-real-key");

        var resp = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task DirectCall_WithValidKey_RecordsRefund()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var message = new HttpRequestMessage(HttpMethod.Post, "/api/simulator/refunds")
        {
            Content = JsonContent.Create(SampleRefund())
        };
        message.Headers.Add(ExecutionAuth.HeaderName, GatewayFactory.ExecutionKey);

        var resp = await client.SendAsync(message);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<SimulatorRefundResponse>();

        Assert.Equal("Settled", body!.Status);
        Assert.StartsWith("RF-", body.RefundId);
    }
}
