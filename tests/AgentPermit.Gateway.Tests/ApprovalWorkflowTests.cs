using System.Net;
using System.Net.Http.Json;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// Week 4 approval workflow — completes MVP tests #6, #7, #9 (and reinforces #10):
/// approved executes, rejected does not, tampered payload is refused, and a repeated
/// approve never executes twice.
/// </summary>
public sealed class ApprovalWorkflowTests
{
    private static async Task<ActionResponseDto> CreateApprovalRequestAsync(HttpClient client, decimal amount = 750m)
    {
        var resp = await client.PostAsJsonAsync("/api/actions/request", RequestBuilder.Refund(amount));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.Equal("REQUIRE_APPROVAL", body!.Decision);
        Assert.NotNull(body.ApprovalRequestId);
        return body;
    }

    // MVP #6: an approved AED 750 refund executes and produces a receipt.
    [Fact]
    public async Task Approve_Executes_AndProducesReceipt()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var request = await CreateApprovalRequestAsync(client);

        var resp = await client.PostAsJsonAsync($"/api/approvals/{request.ApprovalRequestId}/approve",
            new ApprovalDecisionRequestDto { ApproverId = "manager@contoso.com", Comment = "Looks valid" });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>();

        Assert.Equal("Executed", result!.Outcome);
        Assert.NotNull(result.Receipt);
        Assert.Equal("Succeeded", result.Receipt!.ExecutionStatus);
        Assert.Equal(1, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // MVP #7: a rejected refund does not execute.
    [Fact]
    public async Task Reject_DoesNotExecute()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var request = await CreateApprovalRequestAsync(client);

        var resp = await client.PostAsJsonAsync($"/api/approvals/{request.ApprovalRequestId}/reject",
            new ApprovalDecisionRequestDto { ApproverId = "manager@contoso.com", Comment = "Not this time" });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>();

        Assert.Equal("Rejected", result!.Outcome);
        Assert.Null(result.Receipt);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);

        // A rejected approval can no longer be approved.
        var second = await client.PostAsJsonAsync($"/api/approvals/{request.ApprovalRequestId}/approve",
            new ApprovalDecisionRequestDto());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // MVP #9: approving a payload hash that differs from the frozen one is refused (no execution).
    [Fact]
    public async Task ModifiedApprovedPayload_IsRejected()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var request = await CreateApprovalRequestAsync(client);

        var resp = await client.PostAsJsonAsync($"/api/approvals/{request.ApprovalRequestId}/approve",
            new ApprovalDecisionRequestDto
            {
                ApproverId = "manager@contoso.com",
                ExpectedPayloadHash = new string('A', 64) // not the frozen hash
            });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>();
        Assert.Equal("PayloadChanged", result!.Outcome);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // MVP #10 (approval path): approving twice executes exactly once.
    [Fact]
    public async Task Approve_Twice_ExecutesOnce()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var request = await CreateApprovalRequestAsync(client);
        var url = $"/api/approvals/{request.ApprovalRequestId}/approve";

        var first = await (await client.PostAsJsonAsync(url, new ApprovalDecisionRequestDto()))
            .Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>();
        Assert.Equal("Executed", first!.Outcome);

        // Second approve is refused (already decided) — and nothing executes again.
        var second = await client.PostAsJsonAsync(url, new ApprovalDecisionRequestDto());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // An expired approval cannot be actioned.
    [Fact]
    public async Task ExpiredApproval_CannotBeApproved()
    {
        using var factory = new GatewayFactory { ApprovalExpiryHours = -1 }; // born expired
        var client = factory.CreateClient();
        var request = await CreateApprovalRequestAsync(client);

        var resp = await client.PostAsJsonAsync($"/api/approvals/{request.ApprovalRequestId}/approve",
            new ApprovalDecisionRequestDto());

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<ApprovalDecisionResponseDto>();
        Assert.Equal("Expired", result!.Outcome);
        Assert.Equal(0, factory.Services.GetRequiredService<IRefundLedger>().Count);
    }

    // Unknown approval id -> 404.
    [Fact]
    public async Task UnknownApproval_Returns404()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync($"/api/approvals/{Guid.NewGuid()}/approve",
            new ApprovalDecisionRequestDto());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // Pending list surfaces the awaiting request.
    [Fact]
    public async Task PendingList_IncludesAwaitingApproval()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        var request = await CreateApprovalRequestAsync(client);

        var pending = await client.GetFromJsonAsync<List<ApprovalSummaryDto>>("/api/approvals/pending");

        Assert.Contains(pending!, a => a.ApprovalRequestId == request.ApprovalRequestId
                                       && a.Amount == 750m && a.Status == "Pending");
    }
}
