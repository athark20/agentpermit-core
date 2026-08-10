using System.Text.RegularExpressions;

namespace AgentPermit.Gateway.Services;

/// <summary>
/// Shared, deterministic keyword heuristic for consequence classification. Tokenizes text on
/// non-alphanumeric boundaries and matches whole words (+ short plural/suffix) — never arbitrary
/// substrings, so "prod" never matches "product" and "hr" never matches "threshold". Used as the fallback
/// when a target or connector is not in an explicit catalog. Returns null when nothing matched so each
/// caller can apply its own (secure-by-default) fallback.
/// </summary>
public static class RiskKeywords
{
    private static readonly string[] High =
    {
        "refund", "payment", "payout", "pay", "bank", "wire", "transfer", "remit", "disburse",
        "settlement", "invoice", "billing", "charge", "card", "treasury", "finance", "financial", "ledger",
        "payroll", "hr", "patient", "health", "phi", "ssn", "kyc", "delete", "purge", "wipe", "terminate", "prod"
    };
    private static readonly string[] Medium =
    {
        "crm", "ticket", "case", "record", "update", "write", "create", "order", "inventory", "account", "user"
    };
    private static readonly string[] Low =
    {
        "lookup", "read", "get", "info", "status", "search", "query", "faq", "catalog", "report", "weather", "simulator"
    };

    /// <summary>Classify free text. Returns the tier (Domain.ConsequenceTier.*) + the matched keywords, or
    /// (null, empty) when nothing matched.</summary>
    public static (string? Tier, IReadOnlyList<string> Hits) Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, Array.Empty<string>());
        var tokens = Regex.Split(text.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);

        var high = High.Where(k => Matches(tokens, k)).ToList();
        if (high.Count > 0) return (Domain.ConsequenceTier.High, high);
        var medium = Medium.Where(k => Matches(tokens, k)).ToList();
        if (medium.Count > 0) return (Domain.ConsequenceTier.Medium, medium);
        var low = Low.Where(k => Matches(tokens, k)).ToList();
        if (low.Count > 0) return (Domain.ConsequenceTier.Low, low);
        return (null, Array.Empty<string>());
    }

    private static bool Matches(HashSet<string> tokens, string keyword) =>
        tokens.Any(t => t == keyword || (t.StartsWith(keyword, StringComparison.Ordinal) && t.Length - keyword.Length <= 2));
}
