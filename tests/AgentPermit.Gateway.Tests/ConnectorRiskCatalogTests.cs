using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Services;

namespace AgentPermit.Gateway.Tests;

/// <summary>The connector risk catalog: catalog entries are authoritative, unknown connectors fall back to
/// heuristics and then fail closed to HIGH — all explainable.</summary>
public sealed class ConnectorRiskCatalogTests
{
    private static ConnectorRiskCatalog Load()
        => new(Path.Combine(AppContext.BaseDirectory, "seed", "connector-risk-catalog.json"));

    [Fact]
    public void CatalogLoads_WithVersionAndEntries()
    {
        var c = Load();
        Assert.NotEqual("0.0.0", c.Version);
        Assert.NotEmpty(c.Entries);
    }

    [Theory]
    [InlineData("shared_sql", ConsequenceTier.High)]
    [InlineData("shared_http", ConsequenceTier.High)]
    [InlineData("shared_stripe", ConsequenceTier.High)]
    [InlineData("shared_sharepointonline", ConsequenceTier.Medium)]
    [InlineData("shared_teams", ConsequenceTier.Low)]
    [InlineData("shared_approvals", ConsequenceTier.Low)]
    public void KnownConnectors_ClassifyFromCatalog(string connector, string expected)
    {
        var a = Load().Classify(connector);
        Assert.Equal(expected, a.Tier);
        Assert.Equal(RiskSource.Catalog, a.Source);
    }

    [Fact]
    public void UnknownConnector_WithRiskyKeyword_UsesHeuristic()
    {
        var a = Load().Classify("shared_custom_payment_gateway");
        Assert.Equal(ConsequenceTier.High, a.Tier);          // "payment" keyword token
        Assert.Equal(RiskSource.Heuristic, a.Source);
    }

    [Fact]
    public void TotallyUnknownConnector_FailsClosedToHigh()
    {
        var a = Load().Classify("shared_zork9000");
        Assert.Equal(ConsequenceTier.High, a.Tier);
        Assert.Equal(RiskSource.Default, a.Source);
    }
}
