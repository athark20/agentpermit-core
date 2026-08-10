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
