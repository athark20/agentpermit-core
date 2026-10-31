using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Evidence completeness and chain integrity: every verdict of a resolved agent is a receipt (not only
/// executions), receipts carry a strictly increasing sequence number, concurrent writers cannot fork the
/// chain, and the verifier checks continuity against the predecessor.
/// </summary>
public sealed class ReceiptChainTests
{
    private static async Task<ActionResponseDto> Post(HttpClient c, object body) =>
        (await (await c.PostAsJsonAsync("/api/actions/request", body)).Content.ReadFromJsonAsync<ActionResponseDto>())!;

    // Policy DENY -> a DECISION receipt that verifies (integrity + chain) like an execution receipt.
    [Fact]
    public async Task PolicyDeny_MintsVerifiableDecisionReceipt()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var deny = await Post(client, RequestBuilder.Refund(9000m));
        Assert.Equal("DENY", deny.Decision);
        Assert.NotNull(deny.Receipt);
        Assert.Equal("DECISION", deny.Receipt!.Kind);
        Assert.Equal("NotExecuted", deny.Receipt.ExecutionStatus);
        Assert.Equal(0, deny.Receipt.SequenceNumber); // genesis
        Assert.Null(deny.Receipt.PreviousReceiptHash);
        Assert.Equal(deny.ReceiptNumber, deny.Receipt.ReceiptNumber);

        var v = await (await client.GetAsync($"/api/admin/receipts/{deny.ActionRequestId}/verify")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(v.GetProperty("integrityOk").GetBoolean());
        Assert.True(v.GetProperty("chainOk").GetBoolean());
        Assert.True(v.GetProperty("verified").GetBoolean());
        Assert.Equal("DECISION", v.GetProperty("kind").GetString());

        // The decision receipt's content carries the reason code and kind (they are inside the signed hash).
        var stored = await factory.Services.GetRequiredService<IAgentPermitStore>()
            .GetReceiptForRequestAsync(deny.ActionRequestId, CancellationToken.None);
        using var doc = JsonDocument.Parse(stored!.ReceiptJson);
        Assert.Equal("POLICY_DENY", doc.RootElement.GetProperty("reasonCode").GetString());
        Assert.Equal("DECISION", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("sequenceNumber").GetInt64());
    }

    // Early-exit denial of a RESOLVED agent (unknown action) is signed; an UNRESOLVED probe is not.
    [Fact]
    public async Task EarlyExitDenials_SignedOnlyForResolvedAgents()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resolved = await Post(client, RequestBuilder.Refund(100m, actionId: "Nope.Nope"));
        Assert.Equal("UNKNOWN_ACTION", resolved.ReasonCode);
        Assert.NotNull(resolved.Receipt);
        Assert.Equal("DECISION", resolved.Receipt!.Kind);

        var unresolved = await Post(client, RequestBuilder.Refund(100m, agentId: "rogue-agent"));
        Assert.Equal("AGENT_NOT_REGISTERED", unresolved.ReasonCode);
        Assert.Null(unresolved.Receipt);
    }

    // REQUIRE_APPROVAL: a DECISION receipt at request time, then an EXECUTION receipt after approval; the
    // request's "representative" receipt becomes the execution one, and both sit in sequence in the chain.
    [Fact]
    public async Task Approval_DecisionReceiptThenExecutionReceipt()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var stepUp = await Post(client, RequestBuilder.Refund(750m));
        Assert.Equal("DECISION", stepUp.Receipt!.Kind);
        Assert.Equal(0, stepUp.Receipt.SequenceNumber);

        var approve = await client.PostAsJsonAsync($"/api/approvals/{stepUp.ApprovalRequestId}/approve",
            new ApprovalDecisionRequestDto { ApproverId = "manager@contoso.com" });
        approve.EnsureSuccessStatusCode();
        var result = await approve.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>();
        Assert.Equal("Executed", result!.Outcome);
        Assert.Equal("EXECUTION", result.Receipt!.Kind);
        Assert.Equal(1, result.Receipt.SequenceNumber);
        Assert.Equal(stepUp.Receipt.ReceiptHash, result.Receipt.PreviousReceiptHash);

        // Status now reports the EXECUTION receipt for the request.
        var status = await (await client.GetAsync($"/api/actions/{stepUp.ActionRequestId}")).Content.ReadFromJsonAsync<ActionStatusDto>();
        Assert.Equal("EXECUTION", status!.Receipt!.Kind);
        Assert.Equal("Succeeded", status.Receipt.ExecutionStatus);

        var list = await (await client.GetAsync("/api/admin/receipts?take=10")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, list.GetArrayLength());
    }

    // Concurrency: many requests in parallel still yield one linear chain — unique, gap-free sequence numbers
    // and every previousReceiptHash equal to the hash of sequence - 1. (Before: two writers read the same
    // head and forked it.)
    [Fact]
    public async Task ParallelRequests_ProduceOneLinearChain()
    {
        using var factory = new GatewayFactory { CircuitBreakerMax = 1000 };
        var client = factory.CreateClient();

        var tasks = Enumerable.Range(0, 24).Select(i =>
            Post(client, RequestBuilder.Refund(i % 3 == 0 ? 9000m : 100m))); // mix of ALLOW executions and DENY decisions
        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.NotNull(r.Receipt));

        var store = factory.Services.GetRequiredService<IAgentPermitStore>();
        var all = await store.ListRecentReceiptsAsync(100, CancellationToken.None);
        Assert.Equal(24, all.Count);

        var ordered = all.OrderBy(r => r.SequenceNumber).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            Assert.Equal(i, ordered[i].SequenceNumber);
            if (i == 0) Assert.Null(ordered[i].PreviousReceiptHash);
            else Assert.Equal(ordered[i - 1].ReceiptHash, ordered[i].PreviousReceiptHash);
        }
        Assert.Equal(24, ordered.Select(r => r.ReceiptHash).Distinct().Count());
    }

    // The verifier detects a broken link: if the predecessor at sequence-1 doesn't hash to what the receipt
    // claims, chainOk is false and the receipt is not "verified".
    [Fact]
    public async Task Verify_FlagsBrokenChainLink()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var a = await Post(client, RequestBuilder.Refund(100m));
        var b = await Post(client, RequestBuilder.Refund(100m));

        var store = factory.Services.GetRequiredService<IAgentPermitStore>();
        var second = await store.GetReceiptForRequestAsync(b.ActionRequestId, CancellationToken.None);
        // Simulate tampering with the stored link (in-memory store hands back its live record).
        second!.PreviousReceiptHash = new string('0', 64);

        var v = await (await client.GetAsync($"/api/admin/receipts/{b.ActionRequestId}/verify")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(v.GetProperty("chainOk").GetBoolean());
        Assert.False(v.GetProperty("verified").GetBoolean());
        Assert.Equal(a.Receipt!.ReceiptHash, v.GetProperty("expectedPreviousHash").GetString());
    }

    // Decision receipts can be switched off (cost); executions are always receipted.
    [Fact]
    public async Task SignDecisions_CanBeDisabled()
    {
        using var factory = new GatewayFactory { SignDecisions = false };
        var client = factory.CreateClient();

        var deny = await Post(client, RequestBuilder.Refund(9000m));
        Assert.Equal("DENY", deny.Decision);
        Assert.Null(deny.Receipt);

        var allow = await Post(client, RequestBuilder.Refund(100m));
        Assert.NotNull(allow.Receipt);
        Assert.Equal("EXECUTION", allow.Receipt!.Kind);
    }
}
