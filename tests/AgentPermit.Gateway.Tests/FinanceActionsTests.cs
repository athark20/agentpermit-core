using System.Text.Json;
using AgentPermit.Gateway.Adapters;
using AgentPermit.Gateway.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPermit.Gateway.Tests;

/// <summary>
/// The real-world Finance/AP agent's governed actions. Consequence tiers are the safety contract:
/// reads are LOW (warm-up eligible), invoice approval is MEDIUM, and money movement / bank-detail
/// changes are CRITICAL (always binding). The SQL adapter fails closed when unconfigured.
/// </summary>
public sealed class FinanceActionsTests
{
    [Theory]
    [InlineData("Vendor.Lookup", "LOW")]
    [InlineData("Invoice.Lookup", "LOW")]
    [InlineData("Invoice.Approve", "MEDIUM")]
    [InlineData("Payment.Issue", "CRITICAL")]
    [InlineData("Vendor.UpdateBankDetails", "CRITICAL")]
    public void FinanceActions_AreLoaded_WithDeclaredConsequenceTier(string actionId, string tier)
    {
        using var factory = new GatewayFactory();
        var def = factory.Services.GetRequiredService<IActionRegistry>().Find(actionId);
        Assert.NotNull(def);
        Assert.Equal(tier, def!.ConsequenceTier);
        Assert.StartsWith("FINANCE_", def.TargetSystem);
    }

    [Fact]
    public async Task SqlFinanceAdapter_WhenUnconfigured_FailsClosed()
    {
        var adapter = new SqlFinanceAdapter(null, NullLogger<SqlFinanceAdapter>.Instance);
        Assert.Equal("SQL_FINANCE", adapter.AdapterCode);

        var res = await adapter.ExecuteAsync(new AdapterRequest
        {
            ExecutionId = Guid.NewGuid(),
            ActionRequestId = Guid.NewGuid(),
            ActionId = "Payment.Issue",
            TargetSystem = "FINANCE_AP",
            Parameters = JsonDocument.Parse("{}").RootElement
        }, CancellationToken.None);

        Assert.Equal(AdapterExecutionStatus.Failed, res.Status);
        Assert.Equal("TARGET_NOT_CONFIGURED", res.ErrorCode);
    }
}
