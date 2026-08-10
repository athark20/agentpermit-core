using System.Net.Http.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// The gateway must authorize against the authenticated Entra identity (APIM-forwarded),
/// never the body-supplied agentId. Spoofing the agentId, using an unbound identity, or
/// bypassing APIM must all be rejected.
/// </summary>
public sealed class IdentityBindingTests
{
    private static HttpRequestMessage Post(object body, string? clientId = null, string? origin = null)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/actions/request")
        {
            Content = JsonContent.Create(body)
        };
        if (clientId is not null) msg.Headers.Add("X-Agent-Client-Id", clientId);
        if (origin is not null) msg.Headers.Add("X-APIM-Origin", origin);
        return msg;
    }

    // Authenticated identity bound to the agent + matching body agentId -> allowed.
    [Fact]
    public async Task MatchingIdentity_Authorizes()
    {
        using var factory = new GatewayFactory();
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.Refund(100m), clientId: GatewayFactory.BoundClientId));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("ALLOW", body!.Decision);
        Assert.Equal("copilot-refund-demo", body.AgentId);
    }

    // Authenticated as the real agent, but the body claims a different agentId -> spoof rejected.
    [Fact]
    public async Task SpoofedAgentId_IsRejected()
    {
        using var factory = new GatewayFactory();
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.Refund(100m, agentId: "attacker-agent"), clientId: GatewayFactory.BoundClientId));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("AGENT_IDENTITY_MISMATCH", body.ReasonCode);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // An authenticated identity that isn't bound to any registered agent -> denied.
    [Fact]
    public async Task UnboundIdentity_IsDenied()
    {
        using var factory = new GatewayFactory();
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.Refund(100m), clientId: "00000000-0000-0000-0000-000000000000"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("AGENT_NOT_REGISTERED", body.ReasonCode);
    }

    // Enforcement on: a request that didn't come through APIM (no/mismatched origin secret) is denied.
    [Fact]
    public async Task Enforced_WithoutApimOrigin_IsDenied()
    {
        using var factory = new GatewayFactory { EnforceIdentityBinding = true, ApimSharedSecret = "s3cret" };
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.Refund(100m), clientId: GatewayFactory.BoundClientId)); // no origin header
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("NOT_FROM_APIM", body.ReasonCode);
    }

    // Enforcement on: correct origin secret + bound identity -> authorized.
    [Fact]
    public async Task Enforced_WithApimOriginAndIdentity_Authorizes()
    {
        using var factory = new GatewayFactory { EnforceIdentityBinding = true, ApimSharedSecret = "s3cret" };
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.Refund(100m), clientId: GatewayFactory.BoundClientId, origin: "s3cret"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("ALLOW", body!.Decision);
    }

    // Enforcement on: origin verified but no identity header -> denied.
    [Fact]
    public async Task Enforced_WithoutIdentity_IsDenied()
    {
        using var factory = new GatewayFactory { EnforceIdentityBinding = true, ApimSharedSecret = "s3cret" };
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.Refund(100m), origin: "s3cret")); // origin ok, no client id
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("IDENTITY_REQUIRED", body.ReasonCode);
    }

    // A trusted connector puts the agent's Entra Agent ID (badge) directly in the agentId FIELD (because
    // Copilot Studio only exposes existing body fields) -> the gateway recognises it as a badge and resolves.
    [Fact]
    public async Task TrustedConnector_BadgeInAgentIdField_Resolves()
    {
        using var factory = new GatewayFactory { TrustedConnectorClientIds = new[] { GatewayFactory.BoundClientId } };
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.Refund(100m, agentId: GatewayFactory.BoundClientId), // badge in the agentId field
                 clientId: GatewayFactory.BoundClientId));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("ALLOW", body!.Decision);
        Assert.Equal("copilot-refund-demo", body.AgentId);
    }

    // A GUID-shaped (Entra Agent ID) agentId that isn't registered, from a trusted connector, is treated
    // as an unregistered badge and denied AGENT_NOT_REGISTERED (a clear message, not a vague mismatch).
    [Fact]
    public async Task TrustedConnector_UnregisteredBadgeInAgentIdField_IsNotRegistered()
    {
        using var factory = new GatewayFactory { TrustedConnectorClientIds = new[] { GatewayFactory.BoundClientId } };
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.Refund(100m, agentId: "7a934bb8-5329-4a34-85be-f3be02abc792"),
                 clientId: GatewayFactory.BoundClientId));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("AGENT_NOT_REGISTERED", body.ReasonCode);
    }

    // A trusted connector carries the calling agent's badge IN THE BODY with NO body agentId ->
    // the gateway resolves the agent from the badge alone (the one-field Copilot Studio path).
    [Fact]
    public async Task TrustedConnector_BodyBadge_NoAgentId_Resolves()
    {
        using var factory = new GatewayFactory { TrustedConnectorClientIds = new[] { GatewayFactory.BoundClientId } };
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.RefundBadge(100m, agentIdentity: GatewayFactory.BoundClientId),
                 clientId: GatewayFactory.BoundClientId));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("ALLOW", body!.Decision);
        Assert.Equal("copilot-refund-demo", body.AgentId);
    }

    // Trusted connector + body badge + a body agentId that disagrees with the badge -> spoof rejected.
    [Fact]
    public async Task TrustedConnector_BodyBadge_MismatchedAgentId_IsRejected()
    {
        using var factory = new GatewayFactory { TrustedConnectorClientIds = new[] { GatewayFactory.BoundClientId } };
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.RefundBadge(100m, agentIdentity: GatewayFactory.BoundClientId, agentId: "attacker-agent"),
                 clientId: GatewayFactory.BoundClientId));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("AGENT_IDENTITY_MISMATCH", body.ReasonCode);
    }

    // A body badge from an UNtrusted caller is ignored (no trusted-connector list): the gateway falls back
    // to the authenticated azp (here an unbound id), so even though the badge names a real agent the request
    // is denied AGENT_NOT_REGISTERED — proving the badge is only honoured from a trusted connector.
    [Fact]
    public async Task UntrustedConnector_BodyBadge_IsIgnored()
    {
        using var factory = new GatewayFactory(); // no TrustedConnectorClientIds configured
        var resp = await factory.CreateClient().SendAsync(
            Post(RequestBuilder.RefundBadge(100m, agentIdentity: GatewayFactory.BoundClientId, agentId: "copilot-refund-demo"),
                 clientId: "00000000-0000-0000-0000-000000000000"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("DENY", body!.Decision);
        Assert.Equal("AGENT_NOT_REGISTERED", body.ReasonCode);
    }
}
