using System.Net;
using System.Text.Json;

namespace AgentPermit.Gateway.Tests;

/// <summary>The go-to-market action catalog loads from seed/catalog and is served to the console.</summary>
public sealed class ActionCatalogTests
{
    [Fact]
    public async Task Catalog_LoadsManyActions_AcrossDomains_WithConsequenceTiers()
    {
        using var f = new GatewayFactory();
        var res = await f.CreateClient().GetAsync("/api/admin/actions");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);   // dev-open in tests

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var actions = doc.RootElement.EnumerateArray().ToList();

        // A real catalog, not a handful.
        Assert.True(actions.Count >= 60, $"expected a broad catalog, got {actions.Count}");

        var ids = actions.Select(a => a.GetProperty("actionId").GetString()).ToHashSet();
        // Spot-check high-value, cross-domain actions are present.
        foreach (var id in new[] { "Beneficiary.Add", "FundsTransfer.International", "Payroll.Run.Approve",
                                   "AccessGrant.Privileged", "MedicalRecord.Release", "Data.Delete.Bulk",
                                   "Cloud.Resource.Delete", "KYC.RiskRating.Override" })
            Assert.Contains(id, ids);

        // Domains span well beyond finance.
        var domains = actions.Select(a => a.GetProperty("domain").GetString()).ToHashSet();
        Assert.True(domains.Count >= 10, $"expected many domains, got {domains.Count}");

        // Critical money-movement actions carry the CRITICAL consequence tier (always binding).
        var beneficiary = actions.First(a => a.GetProperty("actionId").GetString() == "Beneficiary.Add");
        Assert.Equal("CRITICAL", beneficiary.GetProperty("consequenceTier").GetString());
    }
}
