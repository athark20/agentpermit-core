using System.Net;
using System.Net.Http.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Covers the Week 2 slice of the MVP acceptance tests: deterministic enforcement,
/// auto-execution with receipts, approval routing, denial, idempotency, and audit.
/// </summary>
public sealed class GatewayEndToEndTests
{
    // MVP #1 + #4: a registered agent's small refund is ALLOWED and auto-executes with a receipt.
    [Fact]
    public async Task RegisteredAgent_Aed100_Allows_And_Executes()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(100m));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.NotNull(body);
        Assert.Equal("ALLOW", body!.Decision);
        Assert.Equal("SUCCEEDED", body.Status);
        Assert.NotNull(body.Receipt);
        Assert.Equal("Succeeded", body.Receipt!.ExecutionStatus);
        Assert.StartsWith("RF-", body.Receipt.TargetResourceId);

        // The refund was actually recorded in the simulated target system.
        Assert.Equal(1, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // MVP #2: an unregistered agent is denied (and still audited).
    [Fact]
    public async Task UnregisteredAgent_IsDenied()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(100m, agentId: "rogue-agent"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("AGENT_NOT_REGISTERED", body.ReasonCode);
        Assert.Null(body.Receipt);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // MVP #3: an unknown action is denied.
    [Fact]
    public async Task UnknownAction_IsDenied()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(100m, actionId: "Unknown.Action"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("UNKNOWN_ACTION", body.ReasonCode);
    }

    // MVP #5: a mid-band refund requires approval and does NOT execute.
    [Fact]
    public async Task Aed750_RequiresApproval_AndDoesNotExecute()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(750m));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("REQUIRE_APPROVAL", body!.Decision);
        Assert.Equal("AWAITING_APPROVAL", body.Status);
        Assert.NotNull(body.ApprovalRequestId);
        Assert.Null(body.Receipt);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // MVP #8: a large refund is denied.
    [Fact]
    public async Task Aed3000_IsDenied()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(3000m));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("POLICY_DENY", body.ReasonCode);
        Assert.Null(body.Receipt);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // MVP #10: replaying the same idempotency key does not execute twice.
    [Fact]
    public async Task Replay_SameIdempotencyKey_DoesNotExecuteTwice()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var request = RequestBuilder.Refund(100m, idempotencyKey: "replay-key-123456");

        var first = await (await client.PostAsJsonAsync("/api/actions/request", request))
            .Content.ReadFromJsonAsync<ActionResponseDto>();
        var second = await (await client.PostAsJsonAsync("/api/actions/request", request))
            .Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.False(first!.Replayed);
        Assert.True(second!.Replayed);
        Assert.Equal(first.ActionRequestId, second.ActionRequestId);
        Assert.Equal(first.Receipt!.ReceiptId, second.Receipt!.ReceiptId);
        // Executed exactly once against the target system.
        Assert.Equal(1, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // MVP #12: every request produces an audit record (ActionRequest + PolicyDecision).
    [Fact]
    public async Task EveryRequest_GeneratesAuditRecord()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var body = await (await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(100m)))
            .Content.ReadFromJsonAsync<ActionResponseDto>();

        var store = factory.Services.GetRequiredService<IAgentPermitStore>();
        var audit = await store.GetActionRequestAsync(body!.ActionRequestId!.Value, CancellationToken.None);
        var decision = await store.GetLatestPolicyDecisionAsync(body.ActionRequestId!.Value, CancellationToken.None);

        Assert.NotNull(audit);
        Assert.Equal("Refund.Issue", audit!.ActionId);
        Assert.False(string.IsNullOrEmpty(audit.RequestPayloadHash));
        Assert.NotNull(decision);
        Assert.Equal("ALLOW", decision!.Decision);
    }

    // Invalid parameters (wrong currency) are rejected against the action schema.
    [Fact]
    public async Task InvalidParameters_AreDenied()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(100m, currency: "USD"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("INVALID_PARAMETERS", body.ReasonCode);
    }

    // Idempotency is caller-owned and required: a missing key is a 400, never a gateway-invented key.
    [Fact]
    public async Task MissingIdempotencyKey_Returns400()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/actions/request", new
        {
            actionId = "Refund.Issue",
            agentId = RequestBuilder.RegisteredAgentId,
            correlationId = Guid.NewGuid().ToString(),
            parameters = new { customerId = "CUST-1", amount = 100, currency = "AED", reason = "x" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // GetActionStatus returns the recorded decision + receipt for a known request.
    [Fact]
    public async Task GetActionStatus_ReturnsRecordedState()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(100m)))
            .Content.ReadFromJsonAsync<ActionResponseDto>();

        var statusResp = await client.GetAsync($"/api/actions/{created!.ActionRequestId}");
        statusResp.EnsureSuccessStatusCode();
        var status = await statusResp.Content.ReadFromJsonAsync<ActionStatusDto>();

        Assert.Equal(created.ActionRequestId, status!.ActionRequestId);
        Assert.Equal("ALLOW", status.Decision);
        Assert.Equal("SUCCEEDED", status.Status);
        Assert.NotNull(status.Receipt);
    }

    // Unknown request id -> 404.
    [Fact]
    public async Task GetActionStatus_UnknownId_Returns404()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync($"/api/actions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // Receipts chain: the second receipt references the first receipt's hash.
    [Fact]
    public async Task Receipts_AreHashChained()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var first = await (await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(100m)))
            .Content.ReadFromJsonAsync<ActionResponseDto>();
        var second = await (await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(200m)))
            .Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.NotNull(first!.Receipt);
        Assert.NotNull(second!.Receipt);
        Assert.Null(first.Receipt!.PreviousReceiptHash);
        Assert.Equal(first.Receipt.ReceiptHash, second.Receipt!.PreviousReceiptHash);
    }
}
