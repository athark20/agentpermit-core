using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPermit.Gateway.Domain;

namespace AgentPermit.Gateway.Services;

/// <summary>One curated connector risk entry.</summary>
public sealed class ConnectorRiskEntry
{
    [JsonPropertyName("match")] public string Match { get; set; } = string.Empty;
    [JsonPropertyName("tier")] public string Tier { get; set; } = ConsequenceTier.High;
    [JsonPropertyName("categories")] public string[] Categories { get; set; } = Array.Empty<string>();
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}

/// <summary>Where a connector's tier came from — for explainability.</summary>
public static class RiskSource
{
    public const string Catalog = "CATALOG";
    public const string Heuristic = "HEURISTIC";
    public const string Default = "DEFAULT";
}

public sealed class ConnectorRiskAssessment
{
    public required string Connector { get; init; }
    public required string Tier { get; init; }
    public required string Source { get; init; }
    public string[] Categories { get; init; } = Array.Empty<string>();
    public required IReadOnlyList<string> Reasons { get; init; }
}

/// <summary>
/// Versioned, data-driven connector risk catalog. Classification order (all explainable): an explicit
/// catalog entry (authoritative) → the shared keyword heuristic → HIGH (fail closed). The catalog is
/// curated data — updatable like a threat-intel feed without a code change. It classifies CONNECTORS (what
/// an agent could reach), distinct from <see cref="RiskClassifier"/> which classifies target systems.
/// </summary>
public sealed class ConnectorRiskCatalog
{
    public string Version { get; }
    public IReadOnlyList<ConnectorRiskEntry> Entries { get; }

    public ConnectorRiskCatalog(string catalogPath)
    {
        if (!File.Exists(catalogPath))
        {
            Version = "0.0.0";
            Entries = Array.Empty<ConnectorRiskEntry>();
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var root = doc.RootElement;
        Version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "0.0.0" : "0.0.0";
        Entries = root.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(x => x.Deserialize<ConnectorRiskEntry>()!).Where(x => x is not null).ToList()
            : Array.Empty<ConnectorRiskEntry>();
    }

    public ConnectorRiskAssessment Classify(string connector)
    {
        connector ??= string.Empty;
        var key = connector.Trim().ToLowerInvariant();

        var entry = Entries.FirstOrDefault(en =>
            !string.IsNullOrWhiteSpace(en.Match) && key.Contains(en.Match.ToLowerInvariant()));
        if (entry is not null)
            return new ConnectorRiskAssessment
            {
                Connector = connector, Tier = ConsequenceTier.Normalize(entry.Tier), Source = RiskSource.Catalog,
                Categories = entry.Categories,
                Reasons = new[] { $"Catalog v{Version}: {(string.IsNullOrWhiteSpace(entry.Reason) ? entry.Match : entry.Reason)}." }
            };

        var (tier, hits) = RiskKeywords.Classify(connector);
        if (tier is not null)
            return new ConnectorRiskAssessment
            {
                Connector = connector, Tier = tier, Source = RiskSource.Heuristic,
                Reasons = new[] { $"Not in catalog; matched {tier} keyword(s): {string.Join(", ", hits)}." }
            };

        return new ConnectorRiskAssessment
        {
            Connector = connector, Tier = ConsequenceTier.High, Source = RiskSource.Default,
            Reasons = new[] { "Unknown connector — defaulted to HIGH (secure by default; add a catalog entry after review)." }
        };
    }
}
