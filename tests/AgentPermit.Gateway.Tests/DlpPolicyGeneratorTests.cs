using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Services;

namespace AgentPermit.Gateway.Tests;

/// <summary>The DLP-as-code generator: zero-bypass default, AgentPermit + low allowed, high/unknown blocked.</summary>
public sealed class DlpPolicyGeneratorTests
{
    private static DlpPolicyGenerator Gen() => new(
        new ConnectorRiskCatalog(Path.Combine(AppContext.BaseDirectory, "seed", "connector-risk-catalog.json")),
        new DlpOptions { AgentPermitConnectorId = "agentpermit" });

    [Fact]
    public void Generate_AppliesZeroBypassMapping()
    {
        var r = Gen().Generate(
            new[] { "shared_sql", "shared_teams", "shared_office365", "shared_zork9000", "agentpermit" },
            "AgentPermit DEV");

        var by = r.Decisions.ToDictionary(d => d.Connector, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(DlpGroup.Blocked, r.DefaultGroup);
        Assert.Equal(DlpGroup.Business, by["agentpermit"].Group);        // governed path always allowed
        Assert.Equal(DlpGroup.Business, by["shared_teams"].Group);       // LOW -> allowed
        Assert.Equal(DlpGroup.Business, by["shared_office365"].Group);   // MEDIUM -> allowed...
        Assert.True(by["shared_office365"].ReviewRequired);              // ...but flagged
        Assert.Equal(DlpGroup.Blocked, by["shared_sql"].Group);         // HIGH -> blocked
        Assert.Equal(DlpGroup.Blocked, by["shared_zork9000"].Group);    // unknown -> blocked (fail closed)

        // AgentPermit connector is only listed once even if passed in the input.
        Assert.Single(r.Decisions, d => d.Connector == "agentpermit");
    }

    [Fact]
    public void Generate_ProducesDlpDocument_WithBlockedDefault()
    {
        var r = Gen().Generate(new[] { "shared_sql", "shared_teams" }, "env1");
        var json = System.Text.Json.JsonSerializer.SerializeToElement(r.PolicyDocument);

        Assert.Equal("Blocked", json.GetProperty("defaultConnectorsClassification").GetString());
        var groups = json.GetProperty("connectorGroups").EnumerateArray().ToList();
        var confidential = groups.Single(g => g.GetProperty("classification").GetString() == "Confidential");
        var blocked = groups.Single(g => g.GetProperty("classification").GetString() == "Blocked");
        // teams + agentpermit are allowed; sql is blocked.
        Assert.Equal(2, confidential.GetProperty("connectors").GetArrayLength());
        Assert.Equal(1, blocked.GetProperty("connectors").GetArrayLength());
    }
}
