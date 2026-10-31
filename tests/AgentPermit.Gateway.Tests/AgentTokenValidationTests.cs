using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Gateway-side validation of the AGENT's token. Without it the gateway trusts an APIM-forwarded header plus
/// a static shared secret, so a leaked secret — or an ingress reachable around APIM — lets any caller
/// impersonate any registered agent. With it, the agent IS the validated token's azp, a delegated token's
/// user becomes the verified initiatingUserId, and a forged header buys nothing.
/// </summary>
public sealed class AgentTokenValidationTests
{
    private static HttpRequestMessage Post(object body, string? bearer = null, string? headerClientId = null, string path = "/api/actions/request")
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (bearer is not null) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (headerClientId is not null) msg.Headers.Add("X-Agent-Client-Id", headerClientId);
        return msg;
    }

    private static async Task<ActionResponseDto> SendAsync(HttpClient c, HttpRequestMessage msg)
    {
        var resp = await c.SendAsync(msg);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ActionResponseDto>())!;
    }

    // A valid app-only token identifies the agent; no identity header needed at all.
    [Fact]
    public async Task ValidToken_IdentifiesTheAgent()
    {
        using var factory = new GatewayFactory { AgentAuth = true };
        var body = await SendAsync(factory.CreateClient(),
            Post(RequestBuilder.Refund(100m), bearer: TestTokens.AppOnly(GatewayFactory.BoundClientId)));

        Assert.Equal("ALLOW", body.Decision);
        Assert.Equal("copilot-refund-demo", body.AgentId);
    }

    // The forged header is the whole point: with validation on, it is ignored in favour of the token.
    [Fact]
    public async Task ForgedIdentityHeader_IsIgnoredInFavourOfTheToken()
    {
        using var factory = new GatewayFactory { AgentAuth = true };
        var client = factory.CreateClient();
        (await client.PutAsJsonAsync("/api/admin/agents/other-agent",
            new { displayName = "Other", enabled = true, entraClientId = "99999999-9999-9999-9999-999999999999" }))
            .EnsureSuccessStatusCode();

        // Token says copilot-refund-demo; the header claims the other agent's client id.
        var body = await SendAsync(client, Post(RequestBuilder.Refund(100m),
            bearer: TestTokens.AppOnly(GatewayFactory.BoundClientId),
            headerClientId: "99999999-9999-9999-9999-999999999999"));

        Assert.Equal("copilot-refund-demo", body.AgentId);
        Assert.Equal("ALLOW", body.Decision);
    }

    // A rejected token must NOT fall back to the header path, or validation would be optional in practice.
    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("expired")]
    public async Task InvalidToken_DoesNotFallBackToTheHeader(string flavour)
    {
        using var factory = new GatewayFactory { AgentAuth = true };
        var token = flavour switch
        {
            "wrong-key" => TestTokens.WrongKey(GatewayFactory.BoundClientId),
            "wrong-audience" => TestTokens.AppOnly(GatewayFactory.BoundClientId, audience: "api://someone-else"),
            "wrong-issuer" => TestTokens.AppOnly(GatewayFactory.BoundClientId, issuer: "https://evil.example/"),
            _ => TestTokens.AppOnly(GatewayFactory.BoundClientId, lifetime: TimeSpan.FromMinutes(-30))
        };

        var body = await SendAsync(factory.CreateClient(),
            Post(RequestBuilder.Refund(100m), bearer: token, headerClientId: GatewayFactory.BoundClientId));

        // Presenting a bad token is refused outright — not downgraded to the header or body-agentId path.
        Assert.Equal("DENY", body.Decision);
        Assert.Equal(ReasonCode.TokenInvalid, body.ReasonCode);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // Even with Required=false, a configured deployment never accepts a self-declared body agentId.
    [Fact]
    public async Task Configured_BodyAgentId_IsNotAnIdentity()
    {
        using var factory = new GatewayFactory { AgentAuth = true };
        var body = await SendAsync(factory.CreateClient(), Post(RequestBuilder.Refund(100m)));

        Assert.Equal("DENY", body.Decision);
        Assert.Equal(ReasonCode.IdentityRequired, body.ReasonCode);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // With Required=true, no token at all is a clean TOKEN_INVALID rather than a vague identity failure.
    [Fact]
    public async Task Required_NoToken_IsTokenInvalid()
    {
        using var factory = new GatewayFactory { AgentAuthRequired = true };
        var body = await SendAsync(factory.CreateClient(),
            Post(RequestBuilder.Refund(100m), headerClientId: GatewayFactory.BoundClientId));

        Assert.Equal("DENY", body.Decision);
        Assert.Equal(ReasonCode.TokenInvalid, body.ReasonCode);
    }

    [Fact]
    public async Task Required_ValidToken_Proceeds()
    {
        using var factory = new GatewayFactory { AgentAuthRequired = true };
        var body = await SendAsync(factory.CreateClient(),
            Post(RequestBuilder.Refund(100m), bearer: TestTokens.AppOnly(GatewayFactory.BoundClientId)));
        Assert.Equal("ALLOW", body.Decision);
    }

    // A delegated token's user becomes the verified initiatingUserId — the value separation of duties rests on.
    [Fact]
    public async Task DelegatedToken_DerivesInitiatingUser()
    {
        using var factory = new GatewayFactory { AgentAuth = true };
        var client = factory.CreateClient();
        var body = await SendAsync(client, Post(RequestBuilder.Refund(750m),
            bearer: TestTokens.OnBehalfOf(GatewayFactory.BoundClientId, "dana@contoso.com")));
        Assert.Equal("REQUIRE_APPROVAL", body.Decision);

        var pending = await (await client.GetAsync("/api/approvals/pending")).Content.ReadFromJsonAsync<JsonElement>();
        var approval = Assert.Single(pending.EnumerateArray());
        Assert.Equal("dana@contoso.com", approval.GetProperty("initiatingUserId").GetString());
    }

    // An app-only token carries an oid too (the service principal's) — that must NOT become a "user".
    [Fact]
    public async Task AppOnlyToken_HasNoInitiatingUser()
    {
        using var factory = new GatewayFactory { AgentAuth = true };
        var client = factory.CreateClient();
        await SendAsync(client, Post(RequestBuilder.Refund(750m), bearer: TestTokens.AppOnly(GatewayFactory.BoundClientId)));

        var pending = await (await client.GetAsync("/api/approvals/pending")).Content.ReadFromJsonAsync<JsonElement>();
        var approval = Assert.Single(pending.EnumerateArray());
        Assert.True(approval.GetProperty("initiatingUserId").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
    }

    // A delegated token with no UPN still yields the user's object id.
    [Fact]
    public async Task DelegatedToken_WithoutUpn_UsesObjectId()
    {
        using var factory = new GatewayFactory { AgentAuth = true };
        var client = factory.CreateClient();
        await SendAsync(client, Post(RequestBuilder.Refund(750m),
            bearer: TestTokens.OnBehalfOfObjectId(GatewayFactory.BoundClientId, "55555555-5555-5555-5555-555555555555")));

        var pending = await (await client.GetAsync("/api/approvals/pending")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("55555555-5555-5555-5555-555555555555",
            Assert.Single(pending.EnumerateArray()).GetProperty("initiatingUserId").GetString());
    }

    // Declaring a different initiating user than the token's is a spoof, exactly like a mismatched agentId.
    [Fact]
    public async Task BodyInitiatingUser_DisagreeingWithTheToken_IsRejected()
    {
        using var factory = new GatewayFactory { AgentAuth = true };
        var body = new Dictionary<string, object?>
        {
            ["actionId"] = RequestBuilder.RefundActionId,
            ["agentId"] = RequestBuilder.RegisteredAgentId,
            ["idempotencyKey"] = $"idem-{Guid.NewGuid():N}",
            ["initiatingUserId"] = "someone.else@contoso.com",
            ["parameters"] = new { customerId = "C1", amount = 100m, currency = "AED", reason = "x" }
        };

        var result = await SendAsync(factory.CreateClient(),
            Post(body, bearer: TestTokens.OnBehalfOf(GatewayFactory.BoundClientId, "dana@contoso.com")));

        Assert.Equal("DENY", result.Decision);
        Assert.Equal(ReasonCode.InitiatingUserMismatch, result.ReasonCode);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // Declaring the SAME user the token carries is fine (a connector that echoes it back).
    [Fact]
    public async Task BodyInitiatingUser_MatchingTheToken_IsAccepted()
    {
        using var factory = new GatewayFactory { AgentAuth = true };
        var body = new Dictionary<string, object?>
        {
            ["actionId"] = RequestBuilder.RefundActionId,
            ["agentId"] = RequestBuilder.RegisteredAgentId,
            ["idempotencyKey"] = $"idem-{Guid.NewGuid():N}",
            ["initiatingUserId"] = "Dana@Contoso.com",
            ["parameters"] = new { customerId = "C1", amount = 100m, currency = "AED", reason = "x" }
        };

        var result = await SendAsync(factory.CreateClient(),
            Post(body, bearer: TestTokens.OnBehalfOf(GatewayFactory.BoundClientId, "dana@contoso.com")));
        Assert.Equal("ALLOW", result.Decision);
    }

    // MCP is the same pipeline, so the token path applies there too.
    [Fact]
    public async Task McpFrontDoor_UsesTheSameTokenIdentity()
    {
        using var factory = new GatewayFactory { AgentAuthRequired = true };
        var client = factory.CreateClient();
        var call = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "Refund.Issue",
                arguments = new { customerId = "C1", amount = 100m, currency = "AED", reason = "x", idempotencyKey = $"mcp-{Guid.NewGuid():N}" }
            }
        };

        var noToken = await client.SendAsync(Post(call, path: "/mcp"));
        noToken.EnsureSuccessStatusCode();
        Assert.Contains("TOKEN_INVALID", await noToken.Content.ReadAsStringAsync());

        var withToken = await client.SendAsync(Post(call, bearer: TestTokens.AppOnly(GatewayFactory.BoundClientId), path: "/mcp"));
        withToken.EnsureSuccessStatusCode();
        var text = await withToken.Content.ReadAsStringAsync();
        Assert.Contains("ALLOW", text);
    }

    // The shared-connector badge path can be switched off once every agent has its own identity: a badge is
    // an assertion the gateway cannot verify, so any agent behind that connector could claim another's.
    [Fact]
    public async Task BodyAssertedBadge_CanBeDisabled()
    {
        const string otherClientId = "88888888-8888-8888-8888-888888888888";

        // Enabled (today's behaviour): the connector's badge decides which agent this is, so a caller behind
        // the shared connector can act AS another agent — and inherit its rules and rollout posture.
        using var enabled = new GatewayFactory { TrustedConnectorClientIds = new[] { GatewayFactory.BoundClientId } };
        var enabledClient = enabled.CreateClient();
        (await enabledClient.PutAsJsonAsync("/api/admin/agents/other-agent",
            new { displayName = "Other", enabled = true, entraClientId = otherClientId })).EnsureSuccessStatusCode();

        var viaBadge = await SendAsync(enabledClient,
            Post(RequestBuilder.RefundBadge(100m, agentIdentity: otherClientId), headerClientId: GatewayFactory.BoundClientId));
        Assert.Equal("other-agent", viaBadge.AgentId);

        // Disabled: the badge is ignored entirely; the agent is whoever the authenticated caller is bound to.
        using var disabled = new GatewayFactory
        {
            TrustedConnectorClientIds = new[] { GatewayFactory.BoundClientId },
            AllowBodyAssertedAgentIdentity = false
        };
        var disabledClient = disabled.CreateClient();
        (await disabledClient.PutAsJsonAsync("/api/admin/agents/other-agent",
            new { displayName = "Other", enabled = true, entraClientId = otherClientId })).EnsureSuccessStatusCode();

        var ignored = await SendAsync(disabledClient,
            Post(RequestBuilder.RefundBadge(100m, agentIdentity: otherClientId, agentId: "copilot-refund-demo"),
                 headerClientId: GatewayFactory.BoundClientId));
        Assert.Equal("copilot-refund-demo", ignored.AgentId);
        Assert.Equal("ALLOW", ignored.Decision);
    }

    // With agent auth off, everything behaves exactly as before (the APIM header path).
    [Fact]
    public async Task AgentAuthOff_HeaderPathUnchanged()
    {
        using var factory = new GatewayFactory();
        var body = await SendAsync(factory.CreateClient(),
            Post(RequestBuilder.Refund(100m), headerClientId: GatewayFactory.BoundClientId));
        Assert.Equal("ALLOW", body.Decision);
        Assert.Equal("copilot-refund-demo", body.AgentId);
    }
}
