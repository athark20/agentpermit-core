using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Policies;
using AgentPermit.Gateway.Simulator;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Two-person control. A rule can require several DIFFERENT people to approve an action (large payments,
/// bank-detail changes): each approval counts once, nothing runs until enough people have said yes, and one
/// "no" stops it. Rule changes can require a second administrator: a publish becomes a proposal that someone
/// else must approve before it goes live. The freeze switch stays one-person.
/// </summary>
public sealed class TwoPersonControlTests
{
    // ================================================================ approvals that need two people
    // These run on the dev-open path, where the approver identity is taken from the body: the same string the
    // Entra path derives from the token.

    [Fact]
    public async Task TwoApproverRule_RunsOnlyAfterTwoDifferentPeopleApprove()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await PublishAsync(client, TwoPersonSnapshot("9.0.0"));
        var approvalId = await CreateApprovalAsync(client);

        var pending = await (await client.GetAsync($"/api/approvals/{approvalId}")).Content.ReadFromJsonAsync<ApprovalSummaryDto>();
        Assert.Equal(2, pending!.RequiredApprovals);

        // First approver: counted, nothing runs yet.
        var first = await ApproveAsync(client, approvalId, "alice@contoso.com");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstResult = await first.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>();
        Assert.Equal(ApprovalOutcome.ApprovalRecorded, firstResult!.Outcome);
        Assert.Equal(1, firstResult.ApprovalsReceived);
        Assert.Equal(2, firstResult.ApprovalsRequired);
        Assert.Equal(0, Ledger(factory));

        // The same person again, however their identity is written, does not count twice.
        foreach (var again in new[] { "alice@contoso.com", "Alice Example <ALICE@contoso.com>" })
        {
            var repeat = await ApproveAsync(client, approvalId, again);
            Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
            Assert.Equal(ApprovalOutcome.AlreadyApprovedByYou, (await repeat.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);
        }
        Assert.Equal(0, Ledger(factory));

        var midway = await (await client.GetAsync($"/api/approvals/{approvalId}")).Content.ReadFromJsonAsync<ApprovalSummaryDto>();
        Assert.Equal(ApprovalStatus.Pending, midway!.Status);
        Assert.Equal(new[] { "alice@contoso.com" }, midway.ApprovedBy);

        // A second, different person: now it runs.
        var second = await ApproveAsync(client, approvalId, "bob@contoso.com");
        second.EnsureSuccessStatusCode();
        Assert.Equal(ApprovalOutcome.Executed, (await second.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);
        Assert.Equal(1, Ledger(factory));

        var stored = await factory.Services.GetRequiredService<IAgentPermitStore>().GetApprovalAsync(approvalId, CancellationToken.None);
        Assert.Equal(ApprovalStatus.Approved, stored!.Status);
        Assert.Contains("alice@contoso.com", stored.ApproverId);
        Assert.Contains("bob@contoso.com", stored.ApproverId);
    }

    [Fact]
    public async Task TwoApproverRule_OneRejectionStopsIt()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await PublishAsync(client, TwoPersonSnapshot("9.0.1"));
        var approvalId = await CreateApprovalAsync(client);

        Assert.Equal(HttpStatusCode.Accepted, (await ApproveAsync(client, approvalId, "alice@contoso.com")).StatusCode);

        var no = await client.PostAsJsonAsync($"/api/approvals/{approvalId}/reject",
            new ApprovalDecisionRequestDto { ApproverId = "bob@contoso.com", Comment = "Not this vendor" });
        no.EnsureSuccessStatusCode();
        Assert.Equal(ApprovalOutcome.Rejected, (await no.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);

        var late = await ApproveAsync(client, approvalId, "carol@contoso.com");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(0, Ledger(factory));
    }

    // Separation of duties applies to each approver: the person who asked can't be one of the two.
    [Fact]
    public async Task TwoApproverRule_InitiatorCannotBeOneOfTheApprovers()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await PublishAsync(client, TwoPersonSnapshot("9.0.2"));
        var approvalId = await CreateApprovalAsync(client, initiatingUserId: "carol@contoso.com");

        var self = await ApproveAsync(client, approvalId, "carol@contoso.com");
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal(ApprovalOutcome.SeparationOfDuties, (await self.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);

        Assert.Equal(HttpStatusCode.Accepted, (await ApproveAsync(client, approvalId, "alice@contoso.com")).StatusCode);
        (await ApproveAsync(client, approvalId, "bob@contoso.com")).EnsureSuccessStatusCode();
        Assert.Equal(1, Ledger(factory));
    }

    [Theory]
    [InlineData("DENY", 2)]      // only "ask a person" rules have approvers
    [InlineData("REQUIRE_APPROVAL", 0)]
    [InlineData("REQUIRE_APPROVAL", 6)]
    public async Task Publish_RejectsInvalidRequiredApprovals(string decision, int requiredApprovals)
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/policies/publish", TwoPersonSnapshot("9.1.0", requiredApprovals, decision));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("requiredApprovals", await resp.Content.ReadAsStringAsync());
    }

    // Rules signed before this field existed must hash exactly as before, so an unset value is not written.
    [Fact]
    public void RuleWithoutRequiredApprovals_SerializesAsBefore()
    {
        var json = JsonSerializer.Serialize(new PolicyRule { Id = "r", Name = "r", Decision = "REQUIRE_APPROVAL" });
        Assert.DoesNotContain("requiredApprovals", json);
        Assert.Contains("\"requiredApprovals\":2",
            JsonSerializer.Serialize(new PolicyRule { Id = "r", Name = "r", Decision = "REQUIRE_APPROVAL", RequiredApprovals = 2 }));
    }

    // The console rebuilds rules from /active when it saves, so /active must carry every field a rule can have.
    [Fact]
    public async Task ActiveRules_CarryRequiredApprovalsAndWindowedLimits()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await PublishAsync(client, new
        {
            policyCode = "Refund.Issue.TwoPerson", version = "9.2.0", defaultDecision = "DENY",
            rules = new object[]
            {
                new
                {
                    id = "daily-cap", name = "Daily cap", priority = 10, actionId = "Refund.Issue", decision = "REQUIRE_APPROVAL",
                    requiredApprovals = 2, reason = "Over the daily cap",
                    conditions = new object[]
                    {
                        new { field = "amount", @operator = "greaterThan", value = 5000,
                              aggregate = new { function = "sum", window = "PT24H", groupBy = new[] { "agentId" } } }
                    }
                }
            }
        });

        var active = JsonDocument.Parse(await (await client.GetAsync("/api/policies/active")).Content.ReadAsStringAsync()).RootElement;
        var rule = Assert.Single(active.GetProperty("rules").EnumerateArray());
        Assert.Equal(2, rule.GetProperty("requiredApprovals").GetInt32());
        Assert.Equal("Over the daily cap", rule.GetProperty("reason").GetString());
        var aggregate = rule.GetProperty("conditions")[0].GetProperty("aggregate");
        Assert.Equal("sum", aggregate.GetProperty("function").GetString());
        Assert.Equal("PT24H", aggregate.GetProperty("window").GetString());
    }

    // ================================================================ rule changes need a second administrator

    [Fact]
    public async Task RuleChange_GoesLiveOnlyWhenADifferentAdminApproves()
    {
        using var baseFactory = new GatewayFactory();
        using var factory = WithSecondApprover(baseFactory, signIn: true);
        var client = factory.CreateClient();
        var liveBefore = await ActiveVersionAsync(client);

        var proposal = await SendAsync(client, HttpMethod.Post, "/api/policies/publish", Admin("alice@bank.ae", "Alice Admin"), TwoPersonSnapshot("10.0.0"));
        Assert.Equal(HttpStatusCode.Accepted, proposal.StatusCode);
        var proposed = JsonDocument.Parse(await proposal.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("PendingApproval", proposed.GetProperty("status").GetString());
        var changeId = proposed.GetProperty("changeId").GetString()!;
        Assert.Equal(liveBefore, await ActiveVersionAsync(client));   // nothing is live yet

        var own = await SendAsync(client, HttpMethod.Post, $"/api/policies/changes/{changeId}/approve", Admin("alice@bank.ae", "Alice Admin"));
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal(liveBefore, await ActiveVersionAsync(client));

        var second = await SendAsync(client, HttpMethod.Post, $"/api/policies/changes/{changeId}/approve", Admin("bob@bank.ae", "Bob Admin"));
        second.EnsureSuccessStatusCode();
        Assert.Equal("10.0.0", await ActiveVersionAsync(client));

        var active = JsonDocument.Parse(await (await client.GetAsync("/api/policies/active")).Content.ReadAsStringAsync()).RootElement;
        var publisher = active.GetProperty("publisher").GetString()!;
        Assert.Contains("alice@bank.ae", publisher);
        Assert.Contains("bob@bank.ae", publisher);

        var again = await SendAsync(client, HttpMethod.Post, $"/api/policies/changes/{changeId}/approve", Admin("carol@bank.ae", "Carol Admin"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    // A proposal made against rules that have since changed must not overwrite the newer rules.
    [Fact]
    public async Task RuleChange_IsSupersededWhenTheLiveRulesChangedFirst()
    {
        using var baseFactory = new GatewayFactory();
        using var factory = WithSecondApprover(baseFactory, signIn: true);
        var client = factory.CreateClient();
        var alice = Admin("alice@bank.ae", "Alice Admin");
        var bob = Admin("bob@bank.ae", "Bob Admin");

        var first = await ProposeAsync(client, alice, "11.0.0");
        var second = await ProposeAsync(client, alice, "11.0.1");

        (await SendAsync(client, HttpMethod.Post, $"/api/policies/changes/{first}/approve", bob)).EnsureSuccessStatusCode();
        var stale = await SendAsync(client, HttpMethod.Post, $"/api/policies/changes/{second}/approve", bob);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("11.0.0", await ActiveVersionAsync(client));
    }

    [Fact]
    public async Task RuleChange_CanBeRejected_ThenNotApproved()
    {
        using var baseFactory = new GatewayFactory();
        using var factory = WithSecondApprover(baseFactory, signIn: true);
        var client = factory.CreateClient();
        var changeId = await ProposeAsync(client, Admin("alice@bank.ae", "Alice Admin"), "12.0.0");

        var withdraw = await SendAsync(client, HttpMethod.Post, $"/api/policies/changes/{changeId}/reject", Admin("alice@bank.ae", "Alice Admin"), new { comment = "Wrong limit" });
        withdraw.EnsureSuccessStatusCode();

        var approve = await SendAsync(client, HttpMethod.Post, $"/api/policies/changes/{changeId}/approve", Admin("bob@bank.ae", "Bob Admin"));
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);

        var list = JsonDocument.Parse(await (await SendAsync(client, HttpMethod.Get, "/api/policies/changes", Admin("bob@bank.ae", "Bob Admin"))).Content.ReadAsStringAsync()).RootElement;
        Assert.True(list.GetProperty("requireSecondApprover").GetBoolean());
        var change = Assert.Single(list.GetProperty("changes").EnumerateArray());
        Assert.Equal("Rejected", change.GetProperty("status").GetString());
    }

    // The shared admin key is a service, not a person: it can propose (e.g. rules as code) but never approve.
    [Fact]
    public async Task SharedKey_CanProposeButNotApproveARuleChange()
    {
        using var baseFactory = new GatewayFactory { AdminApiKey = "s3cret-admin" };
        using var factory = WithSecondApprover(baseFactory, signIn: false);
        var client = factory.CreateClient();
        var key = (HttpRequestMessage m) => m.Headers.Add("X-Admin-Key", "s3cret-admin");

        var proposal = await SendAsync(client, HttpMethod.Post, "/api/policies/publish", key, TwoPersonSnapshot("13.0.0"));
        Assert.Equal(HttpStatusCode.Accepted, proposal.StatusCode);
        var changeId = JsonDocument.Parse(await proposal.Content.ReadAsStringAsync()).RootElement.GetProperty("changeId").GetString();

        var approve = await SendAsync(client, HttpMethod.Post, $"/api/policies/changes/{changeId}/approve", key);
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
    }

    // The emergency stop must never wait for a second person.
    [Fact]
    public async Task Freeze_StaysOnePerson_EvenWithTwoPersonRuleChanges()
    {
        using var baseFactory = new GatewayFactory();
        using var factory = WithSecondApprover(baseFactory, signIn: true);
        var client = factory.CreateClient();
        var alice = Admin("alice@bank.ae", "Alice Admin");

        var freeze = await SendAsync(client, HttpMethod.Post, "/api/admin/safety/freeze", alice, new { reason = "Incident" });
        freeze.EnsureSuccessStatusCode();

        var blocked = await (await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(100m)))
            .Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("DENY", blocked!.Decision);
        Assert.Equal(ReasonCode.SystemFrozen, blocked.ReasonCode);

        (await SendAsync(client, HttpMethod.Post, "/api/admin/safety/unfreeze", alice)).EnsureSuccessStatusCode();
    }

    // ================================================================ helpers

    private const string Audience = "api://agentpermit-console-two-person";

    /// <summary>A refund rule set: over 500 needs <paramref name="requiredApprovals"/> approvers, the rest is allowed.</summary>
    private static object TwoPersonSnapshot(string version, int? requiredApprovals = 2, string decision = "REQUIRE_APPROVAL") => new
    {
        policyCode = "Refund.Issue.TwoPerson",
        version,
        defaultDecision = "DENY",
        rules = new object[]
        {
            new
            {
                id = "big-refunds", name = "Big refunds need two people", priority = 10, actionId = "Refund.Issue",
                decision, approverRoles = new[] { "RefundApprover" }, requiredApprovals,
                conditions = new object[] { new { field = "amount", @operator = "greaterThan", value = 500 } }
            },
            new
            {
                id = "small-refunds", name = "Small refunds", priority = 20, actionId = "Refund.Issue",
                decision = "ALLOW", conditions = Array.Empty<object>()
            }
        }
    };

    private static async Task PublishAsync(HttpClient client, object snapshot)
        => (await client.PostAsJsonAsync("/api/policies/publish", snapshot)).EnsureSuccessStatusCode();

    private static async Task<Guid> CreateApprovalAsync(HttpClient client, string? initiatingUserId = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["actionId"] = RequestBuilder.RefundActionId,
            ["agentId"] = RequestBuilder.RegisteredAgentId,
            ["correlationId"] = Guid.NewGuid().ToString(),
            ["idempotencyKey"] = $"idem-{Guid.NewGuid():N}",
            ["parameters"] = new { customerId = "CUST-88", amount = 9000m, currency = "AED", reason = "Contract dispute" }
        };
        if (initiatingUserId is not null) body["initiatingUserId"] = initiatingUserId;
        var resp = await client.PostAsJsonAsync("/api/actions/request", body);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("REQUIRE_APPROVAL", dto!.Decision);
        return dto.ApprovalRequestId!.Value;
    }

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid approvalId, string approver)
        => client.PostAsJsonAsync($"/api/approvals/{approvalId}/approve",
            new ApprovalDecisionRequestDto { ApproverId = approver, Comment = "ok" });

    private static int Ledger(GatewayFactory factory) => factory.Services.GetRequiredService<IRefundLedger>().Count;

    private static async Task<string?> ActiveVersionAsync(HttpClient client)
        => JsonDocument.Parse(await (await client.GetAsync("/api/policies/active")).Content.ReadAsStringAsync())
            .RootElement.GetProperty("version").GetString();

    private static async Task<string> ProposeAsync(HttpClient client, Action<HttpRequestMessage> who, string version)
    {
        var resp = await SendAsync(client, HttpMethod.Post, "/api/policies/publish", who, TwoPersonSnapshot(version));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("changeId").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, Action<HttpRequestMessage> who, object? body = null)
    {
        using var msg = new HttpRequestMessage(method, path);
        if (body is not null) msg.Content = JsonContent.Create(body);
        who(msg);
        return await client.SendAsync(msg);
    }

    /// <summary>Sign the request in as an administrator (Entra app role Admin.Access).</summary>
    private static Action<HttpRequestMessage> Admin(string upn, string name)
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestTokens.Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("preferred_username", upn),
                new Claim("name", name),
                new Claim("oid", Guid.NewGuid().ToString()),
                new Claim("roles", "Admin.Access"),
            }),
            NotBefore = DateTime.UtcNow.AddMinutes(-2),
            IssuedAt = DateTime.UtcNow.AddMinutes(-2),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(TestTokens.SigningKey, SecurityAlgorithms.HmacSha256),
        });
        return m => m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Policy:RequireSecondApprover on, optionally with console sign-in (AdminAuth). The real JwtBearer handler
    /// runs; only its key material is swapped for the tests' symmetric key. These tests never execute an action,
    /// so they don't depend on the simulator hop that the base factory wires to its own server.
    /// </summary>
    private static WebApplicationFactory<Program> WithSecondApprover(GatewayFactory factory, bool signIn)
        => factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Policy:RequireSecondApprover", "true");
            if (!signIn) return;
            b.UseSetting("AdminAuth:Authority", TestTokens.Authority);
            b.UseSetting("AdminAuth:Audience", Audience);
            b.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    o.Authority = null;
                    o.MetadataAddress = null;
                    o.ConfigurationManager = null;
                    o.RequireHttpsMetadata = false;
                    o.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
                    o.TokenValidationParameters.ValidateIssuerSigningKey = true;
                    o.TokenValidationParameters.ValidIssuer = TestTokens.Issuer;
                    o.TokenValidationParameters.ValidAudience = Audience;
                }));
        });
}
