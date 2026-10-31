using System.Net.Http.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>Enterprise idempotency semantics and the validated request state machine.</summary>
public sealed class IdempotencyAndStateTests
{
    // Same key + a DIFFERENT payload must be rejected (not silently replayed), and must not execute.
    [Fact]
    public async Task SameKey_ChangedPayload_IsRejected()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        const string key = "reuse-key-1234567";

        var first = await (await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(100m, idempotencyKey: key))).Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("ALLOW", first!.Decision);

        var reused = await (await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(200m, idempotencyKey: key))).Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("DENY", reused!.Decision);
        Assert.Equal("IDEMPOTENCY_PAYLOAD_MISMATCH", reused.ReasonCode);
        // Only the first (100) executed.
        Assert.Equal(1, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // Same key + same payload still replays (returns the original, no second execution).
    [Fact]
    public async Task SameKey_SamePayload_Replays()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var body = RequestBuilder.Refund(100m, idempotencyKey: "same-key-1234567");

        var first = await (await client.PostAsJsonAsync("/api/actions/request", body))
            .Content.ReadFromJsonAsync<ActionResponseDto>();
        var second = await (await client.PostAsJsonAsync("/api/actions/request", body))
            .Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.True(second!.Replayed);
        Assert.Equal(first!.ActionRequestId, second.ActionRequestId);
        Assert.Equal(1, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // Idempotency keys are an agent-private namespace: a second agent using the SAME key (same payload)
    // gets its own fresh decision + execution, never a replay of the first agent's receipt.
    [Fact]
    public async Task SameKey_DifferentAgents_AreIndependent()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        const string key = "shared-key-1234567";

        // Register a second agent (dev-open admin API: no key configured in tests).
        var put = await client.PutAsJsonAsync("/api/admin/agents/billing-agent",
            new { displayName = "Billing Agent", enabled = true });
        put.EnsureSuccessStatusCode();

        var first = await (await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(100m, idempotencyKey: key))).Content.ReadFromJsonAsync<ActionResponseDto>();
        var second = await (await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(100m, agentId: "billing-agent", idempotencyKey: key))).Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("ALLOW", first!.Decision);
        Assert.Equal("ALLOW", second!.Decision);
        Assert.False(second.Replayed);
        Assert.NotEqual(first.ActionRequestId, second.ActionRequestId);
        Assert.Equal("billing-agent", second.AgentId);
        // Both executed — the key did not collide across agents.
        Assert.Equal(2, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // A second agent reusing another agent's key with a DIFFERENT payload is not a mismatch either —
    // the first agent's key is invisible to it.
    [Fact]
    public async Task SameKey_DifferentAgent_ChangedPayload_IsNotAMismatch()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        const string key = "shared-key-7654321";
        (await client.PutAsJsonAsync("/api/admin/agents/billing-agent", new { displayName = "Billing", enabled = true }))
            .EnsureSuccessStatusCode();

        var first = await (await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(100m, idempotencyKey: key))).Content.ReadFromJsonAsync<ActionResponseDto>();
        var other = await (await client.PostAsJsonAsync("/api/actions/request",
            RequestBuilder.Refund(200m, agentId: "billing-agent", idempotencyKey: key))).Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("ALLOW", first!.Decision);
        Assert.Equal("ALLOW", other!.Decision);
        Assert.NotEqual("IDEMPOTENCY_PAYLOAD_MISMATCH", other.ReasonCode);
    }

    // An unauthenticated probe (identity never resolved) is denied and audited, but must NOT reserve the
    // key: the legitimate agent presenting the same key afterwards gets a fresh ALLOW, not a replayed deny.
    [Fact]
    public async Task UnresolvedIdentityDenial_DoesNotSquatTheKey()
    {
        using var factory = new GatewayFactory { EnforceIdentityBinding = true, ApimSharedSecret = "s3cret" };
        var client = factory.CreateClient();
        const string key = "probe-key-1234567";
        var body = RequestBuilder.Refund(100m, idempotencyKey: key);

        // 1) Probe: no APIM origin, no identity -> NOT_FROM_APIM, audited.
        var probe = await (await client.PostAsJsonAsync("/api/actions/request", body))
            .Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("DENY", probe!.Decision);
        Assert.Equal("NOT_FROM_APIM", probe.ReasonCode);

        // 2) The real agent, through APIM, with the SAME key and payload -> fresh ALLOW, not a replay.
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/actions/request") { Content = JsonContent.Create(body) };
        msg.Headers.Add("X-Agent-Client-Id", GatewayFactory.BoundClientId);
        msg.Headers.Add("X-APIM-Origin", "s3cret");
        var real = await (await client.SendAsync(msg)).Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("ALLOW", real!.Decision);
        Assert.False(real.Replayed);
        Assert.NotEqual(probe.ActionRequestId, real.ActionRequestId);
        Assert.Equal(1, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // Repeating the same unauthenticated probe twice must not blow up on a duplicate-key insert; each
    // attempt is a separate audited denial.
    [Fact]
    public async Task RepeatedUnresolvedProbe_IsAuditedEachTime()
    {
        using var factory = new GatewayFactory { EnforceIdentityBinding = true, ApimSharedSecret = "s3cret" };
        var client = factory.CreateClient();
        var body = RequestBuilder.Refund(100m, idempotencyKey: "probe-key-7654321");

        var a = await client.PostAsJsonAsync("/api/actions/request", body);
        var b = await client.PostAsJsonAsync("/api/actions/request", body);
        a.EnsureSuccessStatusCode();
        b.EnsureSuccessStatusCode();
        var ra = await a.Content.ReadFromJsonAsync<ActionResponseDto>();
        var rb = await b.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("NOT_FROM_APIM", ra!.ReasonCode);
        Assert.Equal("NOT_FROM_APIM", rb!.ReasonCode);
        Assert.NotEqual(ra.ActionRequestId, rb.ActionRequestId);
    }

    // A resolved agent's early-exit denial (e.g. unknown action) IS filed under its key, so a retry replays.
    [Fact]
    public async Task ResolvedAgentDenial_ReplaysOnRetry()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var body = RequestBuilder.Refund(100m, actionId: "No.Such.Action", idempotencyKey: "unknown-act-1234567");

        var first = await (await client.PostAsJsonAsync("/api/actions/request", body)).Content.ReadFromJsonAsync<ActionResponseDto>();
        var second = await (await client.PostAsJsonAsync("/api/actions/request", body)).Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("UNKNOWN_ACTION", first!.ReasonCode);
        Assert.True(second!.Replayed);
        Assert.Equal(first.ActionRequestId, second.ActionRequestId);
    }

    [Theory]
    [InlineData(RequestState.PolicyEvaluated, RequestState.ExecutionPending, true)]
    [InlineData(RequestState.ExecutionPending, RequestState.Executing, true)]
    [InlineData(RequestState.Executing, RequestState.Succeeded, true)]
    [InlineData(RequestState.AwaitingApproval, RequestState.Approved, true)]
    [InlineData(RequestState.AwaitingApproval, RequestState.Rejected, true)]
    [InlineData(RequestState.Received, RequestState.Succeeded, false)]     // no illegal jump
    [InlineData(RequestState.PolicyEvaluated, RequestState.Succeeded, false)]
    [InlineData(RequestState.Succeeded, RequestState.Executing, false)]     // terminal
    [InlineData(RequestState.Denied, RequestState.Executing, false)]        // terminal
    public void StateMachine_AllowsOnlyValidTransitions(string from, string to, bool allowed)
    {
        Assert.Equal(allowed, RequestStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData("ALLOW", RequestState.ExecutionPending)]
    [InlineData("REQUIRE_APPROVAL", RequestState.AwaitingApproval)]
    [InlineData("DENY", RequestState.Denied)]
    public void InitialState_MatchesDecision(string decision, string expected)
    {
        Assert.Equal(expected, RequestStateMachine.InitialFor(decision));
    }
}
