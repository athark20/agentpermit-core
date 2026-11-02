using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Who may decide an approval. The seeded baseline rule for a mid-size refund names the approver role
/// "RefundApprover"; the gateway must enforce membership of that group (not just "is an Approver"), refuse the
/// initiating user and the agent owner (separation of duties), and show the approver the whole payload.
/// Tests run on the dev-open path, where the approver identity is taken from the body — the same string the
/// Entra path derives from the token.
/// </summary>
public sealed class ApproverAuthorizationTests
{
    private const string Role = "RefundApprover";

    private static async Task<ActionResponseDto> CreateApprovalAsync(HttpClient client, string? initiatingUserId = null, string agentId = RequestBuilder.RegisteredAgentId)
    {
        var body = new Dictionary<string, object?>
        {
            ["actionId"] = RequestBuilder.RefundActionId,
            ["agentId"] = agentId,
            ["correlationId"] = Guid.NewGuid().ToString(),
            ["idempotencyKey"] = $"idem-{Guid.NewGuid():N}",
            ["parameters"] = new { customerId = "CUST-77", amount = 750m, currency = "AED", reason = "Late delivery" }
        };
        if (initiatingUserId is not null) body["initiatingUserId"] = initiatingUserId;
        var resp = await client.PostAsJsonAsync("/api/actions/request", body);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("REQUIRE_APPROVAL", dto!.Decision);
        return dto;
    }

    private static Task<HttpResponseMessage> DefineGroupAsync(HttpClient client, string name, params string[] emails)
        => client.PutAsJsonAsync($"/api/admin/approver-groups/{name}", new
        {
            description = $"{name} test group",
            members = emails.Select(e => new { email = e, displayName = e.Split('@')[0] }).ToArray()
        });

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid? approvalId, string approver)
        => client.PostAsJsonAsync($"/api/approvals/{approvalId}/approve",
            new ApprovalDecisionRequestDto { ApproverId = approver, Comment = "test" });

    // With no approver group defined (default settings), the generic approver path still works — unchanged.
    [Fact]
    public async Task NoGroupDefined_DefaultSettings_FallsBackToGenericApprover()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var req = await CreateApprovalAsync(client);

        var resp = await ApproveAsync(client, req.ApprovalRequestId, "anyone@contoso.com");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("Executed", (await resp.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);
    }

    // Fail closed: when named groups are required and none exists for the rule's role, nobody can approve.
    [Fact]
    public async Task NoGroupDefined_RequireNamedGroups_RefusesEveryone()
    {
        using var factory = new GatewayFactory { RequireNamedApproverGroups = true };
        var client = factory.CreateClient();
        var req = await CreateApprovalAsync(client);

        var resp = await ApproveAsync(client, req.ApprovalRequestId, "anyone@contoso.com");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>();
        Assert.Equal(ApprovalOutcome.ApproverRoleRequired, result!.Outcome);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // The rule names RefundApprover: a non-member is refused (403), a member executes.
    [Fact]
    public async Task GroupDefined_OnlyMembersMayApprove()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        (await DefineGroupAsync(client, Role, "alice@contoso.com")).EnsureSuccessStatusCode();
        var req = await CreateApprovalAsync(client);

        var bob = await ApproveAsync(client, req.ApprovalRequestId, "bob@contoso.com");
        Assert.Equal(HttpStatusCode.Forbidden, bob.StatusCode);
        Assert.Equal(ApprovalOutcome.ApproverRoleRequired, (await bob.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);

        // The approval is still pending (a refused decider does not consume it).
        var alice = await ApproveAsync(client, req.ApprovalRequestId, "Alice Example <ALICE@contoso.com>"); // console-style identity, odd casing
        alice.EnsureSuccessStatusCode();
        Assert.Equal("Executed", (await alice.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);
        Assert.Equal(1, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // Declining is also role-gated: a non-member can't reject on the group's behalf either.
    [Fact]
    public async Task GroupDefined_NonMemberCannotReject()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        (await DefineGroupAsync(client, Role, "alice@contoso.com")).EnsureSuccessStatusCode();
        var req = await CreateApprovalAsync(client);

        var resp = await client.PostAsJsonAsync($"/api/approvals/{req.ApprovalRequestId}/reject",
            new ApprovalDecisionRequestDto { ApproverId = "bob@contoso.com" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // Separation of duties: the person who initiated the request cannot approve it, even as a group member.
    [Fact]
    public async Task InitiatingUser_CannotApproveOwnRequest()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        (await DefineGroupAsync(client, Role, "carol@contoso.com", "dave@contoso.com")).EnsureSuccessStatusCode();
        var req = await CreateApprovalAsync(client, initiatingUserId: "carol@contoso.com");

        var self = await ApproveAsync(client, req.ApprovalRequestId, "carol@contoso.com");
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal(ApprovalOutcome.SeparationOfDuties, (await self.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);

        var other = await ApproveAsync(client, req.ApprovalRequestId, "dave@contoso.com");
        other.EnsureSuccessStatusCode();
        Assert.Equal("Executed", (await other.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);
    }

    // Separation of duties: the agent's registered owner cannot approve that agent's actions.
    [Fact]
    public async Task AgentOwner_CannotApproveOwnAgent()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        (await client.PutAsJsonAsync("/api/admin/agents/owned-agent",
            new { displayName = "Owned Agent", enabled = true, owner = "owner@contoso.com" })).EnsureSuccessStatusCode();
        var req = await CreateApprovalAsync(client, agentId: "owned-agent");

        var owner = await ApproveAsync(client, req.ApprovalRequestId, "owner@contoso.com");
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
        Assert.Equal(ApprovalOutcome.SeparationOfDuties, (await owner.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);

        var other = await ApproveAsync(client, req.ApprovalRequestId, "finance@contoso.com");
        other.EnsureSuccessStatusCode();
        Assert.Equal("Executed", (await other.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>())!.Outcome);
    }

    // The approver sees the WHOLE payload, not just amount/currency/customer/reason.
    [Fact]
    public async Task PendingSummary_CarriesFullPayloadAndInitiator()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await CreateApprovalAsync(client, initiatingUserId: "erin@contoso.com");

        var pending = await (await client.GetAsync("/api/approvals/pending")).Content.ReadFromJsonAsync<JsonElement>();
        var a = Assert.Single(pending.EnumerateArray());
        Assert.Equal("erin@contoso.com", a.GetProperty("initiatingUserId").GetString());
        Assert.Equal(Role, a.GetProperty("approverRole").GetString());
        var p = a.GetProperty("parameters");
        Assert.Equal("CUST-77", p.GetProperty("customerId").GetString());
        Assert.Equal(750m, p.GetProperty("amount").GetDecimal());
        Assert.Equal("Late delivery", p.GetProperty("reason").GetString());
    }

    // Admin CRUD for groups: list / get / put / delete, with e-mails normalised.
    [Fact]
    public async Task ApproverGroups_AdminCrud()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        (await DefineGroupAsync(client, "FinanceController", "CFO@Contoso.com", "cfo@contoso.com", "deputy@contoso.com")).EnsureSuccessStatusCode();
        var g = await (await client.GetAsync("/api/admin/approver-groups/financecontroller")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FinanceController", g.GetProperty("name").GetString());
        Assert.Equal(2, g.GetProperty("members").GetArrayLength()); // duplicate (case-insensitive) collapsed

        var list = await (await client.GetAsync("/api/admin/approver-groups")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("name").GetString() == "FinanceController");

        var del = await client.DeleteAsync("/api/admin/approver-groups/FinanceController");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var gone = await client.GetAsync("/api/admin/approver-groups/FinanceController");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }
}
