using AgentPermit.Gateway.Domain;

namespace AgentPermit.Gateway.Services;

public sealed class DlpOptions
{
    /// <summary>The Power Platform connector id for AgentPermit's own connector — always placed in the
    /// allowed (Business) group so the governed path stays open.</summary>
    public string AgentPermitConnectorId { get; set; } = "agentpermit";
}

/// <summary>Groups in a generated DLP policy (mapped to Power Platform classifications on export).</summary>
public static class DlpGroup
{
    public const string Business = "BUSINESS";   // Confidential — allowed
    public const string Blocked = "BLOCKED";     // Blocked — must route through AgentPermit
}

public sealed class ConnectorDecision
{
    public required string Connector { get; init; }
    public required string Tier { get; init; }
    public required string Source { get; init; }
    public required string Group { get; init; }
    public bool ReviewRequired { get; init; }
    public required string Reason { get; init; }
}

public sealed class DlpGenerationResult
{
    public required string Environment { get; init; }
    public required string CatalogVersion { get; init; }
    public required string DefaultGroup { get; init; }
    public required IReadOnlyList<ConnectorDecision> Decisions { get; init; }
    /// <summary>The DLP policy document (policy-as-code) ready to review and deploy to Power Platform.</summary>
    public required object PolicyDocument { get; init; }
}

/// <summary>
/// Generates a Power Platform DLP policy from the connectors an agent/environment uses and the connector
/// risk catalog. Zero-bypass posture: default group is Blocked; the AgentPermit connector and LOW-risk
/// connectors are allowed; MEDIUM is allowed-but-flagged for review; HIGH/CRITICAL and unknown are Blocked
/// (they must route through AgentPermit). Every decision carries its rationale. This is the artifact — live
/// deployment to Power Platform (API/Terraform) is a separate, reviewed step.
/// </summary>
public sealed class DlpPolicyGenerator(ConnectorRiskCatalog catalog, DlpOptions options)
{
    public DlpGenerationResult Generate(IEnumerable<string> connectors, string environment)
    {
        var decisions = new List<ConnectorDecision>
        {
            new()
            {
                Connector = options.AgentPermitConnectorId, Tier = ConsequenceTier.Low, Source = RiskSource.Catalog,
                Group = DlpGroup.Business, Reason = "AgentPermit connector — the sanctioned governed path (always allowed)."
            }
        };

        foreach (var conn in connectors
                     .Where(c => !string.IsNullOrWhiteSpace(c))
                     .Select(c => c.Trim())
                     .Where(c => !string.Equals(c, options.AgentPermitConnectorId, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var a = catalog.Classify(conn);
            var (group, review, reason) = Decide(a);
            decisions.Add(new ConnectorDecision
            {
                Connector = conn, Tier = a.Tier, Source = a.Source, Group = group,
                ReviewRequired = review, Reason = reason
            });
        }

        return new DlpGenerationResult
        {
            Environment = environment,
            CatalogVersion = catalog.Version,
            DefaultGroup = DlpGroup.Blocked,
            Decisions = decisions,
            PolicyDocument = BuildPolicyDocument(decisions, environment)
        };
    }

    private static (string Group, bool Review, string Reason) Decide(ConnectorRiskAssessment a)
    {
        var why = string.Join(" ", a.Reasons);
        return a.Tier switch
        {
            ConsequenceTier.Low => (DlpGroup.Business, false, $"LOW risk — allowed. {why}"),
            ConsequenceTier.Medium => (DlpGroup.Business, true, $"MEDIUM risk — allowed but flagged; consider routing via AgentPermit. {why}"),
            _ => (DlpGroup.Blocked, false, $"{a.Tier} risk — blocked; must route through AgentPermit. {why}")
        };
    }

    /// <summary>The DLP policy shape Power Platform expects (Confidential = Business, Blocked = Blocked,
    /// default classification Blocked for zero-bypass).</summary>
    private object BuildPolicyDocument(IReadOnlyList<ConnectorDecision> decisions, string environment)
    {
        object[] Connectors(string group) => decisions
            .Where(d => d.Group == group)
            .Select(d => (object)new { id = d.Connector, name = d.Connector })
            .ToArray();

        return new
        {
            displayName = $"AgentPermit generated · {environment}",
            defaultConnectorsClassification = "Blocked",
            environmentType = "OnlyEnvironments",
            environments = new[] { new { name = environment } },
            connectorGroups = new object[]
            {
                new { classification = "Confidential", connectors = Connectors(DlpGroup.Business) },
                new { classification = "Blocked", connectors = Connectors(DlpGroup.Blocked) }
            }
        };
    }
}
