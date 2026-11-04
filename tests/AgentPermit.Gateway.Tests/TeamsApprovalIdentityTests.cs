using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Services;
using AgentPermit.Gateway.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Teams approvals must carry the approver's own Entra ID sign-in. The shared admin key (what a Power Automate
/// flow holds) identifies a service, not a person, so by default it can read approvals but cannot approve or
/// reject them; the card links to the console instead, where the approver signs in and decides. The legacy
/// workflow mode keeps old flows working, with every such decision flagged as unverified.
/// </summary>
public sealed class TeamsApprovalIdentityTests
{
    private const string AdminKey = "s3cret-admin";

    // ---------------------------------------------------------------- shared key cannot decide

    [Fact]
    public async Task SharedKey_CannotApprove_ByDefault()
    {
        using var factory = new GatewayFactory { AdminApiKey = AdminKey };
        var client = factory.CreateClient();
        var approvalId = await CreateApprovalAsync(client);

        var resp = await client.SendAsync(Decision(approvalId, "approve", key: AdminKey, approverId: "cfo@bank.ae"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
        Assert.Equal(ApprovalStatus.Pending, (await StoredApprovalAsync(factory.Services, approvalId)).Status);
    }

    [Fact]
    public async Task SharedKey_CannotReject_ByDefault()
    {
        using var factory = new GatewayFactory { AdminApiKey = AdminKey };
        var client = factory.CreateClient();
        var approvalId = await CreateApprovalAsync(client);

        var resp = await client.SendAsync(Decision(approvalId, "reject", key: AdminKey, approverId: "cfo@bank.ae"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(ApprovalStatus.Pending, (await StoredApprovalAsync(factory.Services, approvalId)).Status);
    }

    // The flow still needs to read what it is notifying about.
    [Fact]
    public async Task SharedKey_CanStillReadApprovals()
    {
        using var factory = new GatewayFactory { AdminApiKey = AdminKey };
        var client = factory.CreateClient();
        var approvalId = await CreateApprovalAsync(client);

        using var msg = new HttpRequestMessage(HttpMethod.Get, $"/api/approvals/{approvalId}");
        msg.Headers.Add("X-Admin-Key", AdminKey);
        var resp = await client.SendAsync(msg);

        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<ApprovalSummaryDto>();
        Assert.Equal(ApprovalStatus.Pending, summary!.Status);
    }

    // Legacy flows keep working only when explicitly allowed, and the decision says it was not verified.
    [Fact]
    public async Task LegacyWorkflowMode_AllowsKeyDecisions_FlaggedAsUnverified()
    {
        using var factory = new GatewayFactory { AdminApiKey = AdminKey, AllowWorkflowDecisions = true };
        var client = factory.CreateClient();
        var approvalId = await CreateApprovalAsync(client);

        var resp = await client.SendAsync(Decision(approvalId, "approve", key: AdminKey, approverId: "responder@bank.ae"));

        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>();
        Assert.Equal(ApprovalOutcome.Executed, result!.Outcome);
        Assert.Contains("not verified", result.Message);
        Assert.Equal("responder@bank.ae", (await StoredApprovalAsync(factory.Services, approvalId)).ApproverId);

        var audit = await factory.Services.GetRequiredService<IAuditStore>().ListRecentAsync(20, CancellationToken.None);
        var entry = Assert.Single(audit, e => e.Action == "approval.approve");
        Assert.Contains("not verified", entry.Summary);
    }

    // ---------------------------------------------------------------- the approver's own sign-in

    [Fact]
    public async Task SignedInApprover_DecidesAsThemselves_BodyIgnored()
    {
        using var factory = new GatewayFactory { ConsoleSignIn = true };
        await GrantRoleAsync(factory.Services, "approver", "fatima@bank.ae");
        var client = factory.CreateClient();
        var approvalId = await CreateApprovalAsync(client);

        var resp = await client.SendAsync(Decision(approvalId, "approve",
            bearer: PersonToken("fatima@bank.ae", "Fatima Hassan"), approverId: "someone-else@bank.ae"));

        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>();
        Assert.Equal(ApprovalOutcome.Executed, result!.Outcome);
        Assert.DoesNotContain("not verified", result.Message ?? string.Empty);
        Assert.Equal("Fatima Hassan <fatima@bank.ae>", (await StoredApprovalAsync(factory.Services, approvalId)).ApproverId);
        Assert.Equal(1, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    [Fact]
    public async Task SignedInViewer_CannotDecide()
    {
        using var factory = new GatewayFactory { ConsoleSignIn = true };
        await GrantRoleAsync(factory.Services, "viewer", "omar@bank.ae");
        var client = factory.CreateClient();
        var approvalId = await CreateApprovalAsync(client);

        var resp = await client.SendAsync(Decision(approvalId, "approve", bearer: PersonToken("omar@bank.ae", "Omar Saeed")));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(ApprovalStatus.Pending, (await StoredApprovalAsync(factory.Services, approvalId)).Status);
    }

    [Fact]
    public async Task NoSignIn_CannotDecide()
    {
        using var factory = new GatewayFactory { ConsoleSignIn = true };
        var client = factory.CreateClient();
        var approvalId = await CreateApprovalAsync(client);

        var resp = await client.SendAsync(Decision(approvalId, "approve", approverId: "cfo@bank.ae"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // ---------------------------------------------------------------- what the Teams card is sent

    [Fact]
    public async Task Notification_LinksToConsole_AndListsEveryField()
    {
        var hook = new CapturingHandler();
        var options = new TeamsOptions
        {
            WebhookUrl = "https://flow.example/hook",
            ApprovalsBaseUrl = "https://apim.example/agentpermit/",
            ConsoleBaseUrl = "https://console.example/",
        };
        var notifier = new HttpTeamsNotifier(new HttpClient(hook), options, NullLogger<HttpTeamsNotifier>.Instance);
        var (approval, request) = BankDetailChange();

        await notifier.NotifyApprovalRequestedAsync(approval, request, CancellationToken.None);

        var sent = JsonDocument.Parse(hook.Body!).RootElement;
        Assert.Equal("console", sent.GetProperty("decisionMode").GetString());
        Assert.Equal($"https://console.example/#/approvals/{approval.ApprovalRequestId}", sent.GetProperty("reviewUrl").GetString());
        Assert.Equal($"https://apim.example/agentpermit/api/approvals/{approval.ApprovalRequestId}/approve", sent.GetProperty("approveUrl").GetString());

        var facts = sent.GetProperty("facts").EnumerateArray()
            .ToDictionary(f => f.GetProperty("title").GetString()!, f => f.GetProperty("value").GetString());
        Assert.Equal("V-1001", facts["vendorId"]);
        Assert.Equal("AE070331234567890123456", facts["newIban"]);
        Assert.Equal("15750.00", facts["amount"]);                 // exact text, not re-formatted
        Assert.Equal("[\"urgent\",\"q4\"]", facts["tags"]);        // nested values as compact JSON
        Assert.Equal(string.Empty, facts["note"]);
    }

    [Fact]
    public async Task Notification_LegacyMode_AndNoConsoleUrl()
    {
        var hook = new CapturingHandler();
        var options = new TeamsOptions { WebhookUrl = "https://flow.example/hook", AllowWorkflowDecisions = true };
        var notifier = new HttpTeamsNotifier(new HttpClient(hook), options, NullLogger<HttpTeamsNotifier>.Instance);
        var (approval, request) = BankDetailChange();

        await notifier.NotifyApprovalRequestedAsync(approval, request, CancellationToken.None);

        var sent = JsonDocument.Parse(hook.Body!).RootElement;
        Assert.Equal("workflow", sent.GetProperty("decisionMode").GetString());
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("reviewUrl").ValueKind);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A mid-size refund: the seeded baseline policy sends it for approval.</summary>
    private static async Task<Guid> CreateApprovalAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(750m));
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("REQUIRE_APPROVAL", dto!.Decision);
        return dto.ApprovalRequestId!.Value;
    }

    private static HttpRequestMessage Decision(Guid approvalId, string verb, string? key = null, string? bearer = null, string? approverId = null)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, $"/api/approvals/{approvalId}/{verb}")
        {
            Content = JsonContent.Create(new ApprovalDecisionRequestDto { ApproverId = approverId, Comment = "test" })
        };
        if (key is not null) msg.Headers.Add("X-Admin-Key", key);
        if (bearer is not null) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return msg;
    }

    private static async Task<ApprovalRequestRecord> StoredApprovalAsync(IServiceProvider services, Guid approvalId)
        => (await services.GetRequiredService<IAgentPermitStore>().GetApprovalAsync(approvalId, CancellationToken.None))!;

    private static Task GrantRoleAsync(IServiceProvider services, string role, string email)
        => services.GetRequiredService<IConfigStore>().UpsertAsync("access-role", role,
            JsonSerializer.Serialize(new { members = new[] { new { email, displayName = email } } }), CancellationToken.None);

    /// <summary>A person's console sign-in token (what MSAL gets for the approver in the browser).</summary>
    private static string PersonToken(string upn, string name)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestTokens.Issuer,
            Audience = GatewayFactory.ConsoleAudience,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("preferred_username", upn),
                new Claim("name", name),
                new Claim("oid", Guid.NewGuid().ToString()),
            }),
            NotBefore = DateTime.UtcNow.AddMinutes(-2),
            IssuedAt = DateTime.UtcNow.AddMinutes(-2),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(TestTokens.SigningKey, SecurityAlgorithms.HmacSha256),
        });

    private static (ApprovalRequestRecord, ActionRequestRecord) BankDetailChange()
    {
        var approval = new ApprovalRequestRecord
        {
            ApprovalRequestId = Guid.NewGuid(),
            ActionRequestId = Guid.NewGuid(),
            ApprovalType = "SingleApprover",
            ApproverRole = "FinanceController",
            PayloadHash = "abc123",
            Status = ApprovalStatus.Pending,
            RequestedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(24),
        };
        var request = new ActionRequestRecord
        {
            ActionRequestId = approval.ActionRequestId,
            ActionId = "Vendor.UpdateBankDetails",
            ExecutingAgentId = "ap-agent",
            InitiatingUserId = "clerk@bank.ae",
            RequestPayloadJson = """{"vendorId":"V-1001","newIban":"AE070331234567890123456","amount":15750.00,"tags":["urgent","q4"],"note":null}""",
            Status = RequestState.AwaitingApproval,
        };
        return (approval, request);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
