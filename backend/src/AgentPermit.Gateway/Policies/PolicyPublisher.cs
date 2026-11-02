using System.Text.Json;
using AgentPermit.Gateway.Services;

namespace AgentPermit.Gateway.Policies;

/// <summary>Thrown when an authored policy fails validation before publish.</summary>
public sealed class PolicyValidationException(string message) : Exception(message);

public sealed class PublishResult
{
    public required string PolicyCode { get; init; }
    public required string Version { get; init; }
    public required string Hash { get; init; }
    public required bool Signed { get; init; }
    public required DateTimeOffset PublishedAtUtc { get; init; }
}

/// <summary>Where published snapshots live. File-based for now; a Blob store is the durable prod store.</summary>
public interface IPolicySnapshotStore
{
    PolicySnapshot? LoadCurrent();
    void SaveCurrent(PolicySnapshot snapshot);
    IReadOnlyList<string> ListVersions();
}

public sealed class FilePolicySnapshotStore : IPolicySnapshotStore
{
    private readonly string _dir;
    private readonly string _versionsDir;
    private readonly string _currentPath;

    public FilePolicySnapshotStore(string dir)
    {
        _dir = dir;
        _versionsDir = Path.Combine(dir, "versions");
        _currentPath = Path.Combine(dir, "current.json");
        Directory.CreateDirectory(_versionsDir);
    }

    public PolicySnapshot? LoadCurrent()
        => File.Exists(_currentPath) ? JsonSerializer.Deserialize<PolicySnapshot>(File.ReadAllText(_currentPath)) : null;

    public void SaveCurrent(PolicySnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        // Immutable per-version file + a "current" pointer.
        var safeVersion = string.Concat(snapshot.Version.Split(Path.GetInvalidFileNameChars()));
        File.WriteAllText(Path.Combine(_versionsDir, $"{safeVersion}.json"), json);
        File.WriteAllText(_currentPath, json);
    }

    public IReadOnlyList<string> ListVersions()
        => Directory.Exists(_versionsDir)
            ? Directory.GetFiles(_versionsDir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(v => v).ToList()
            : Array.Empty<string>();
}

/// <summary>A snapshot provider whose active snapshot can be replaced at runtime by a publish.</summary>
public interface IPublishablePolicySnapshotProvider : IPolicySnapshotProvider
{
    void Set(PolicySnapshot snapshot, string hash);
}

/// <summary>
/// Holds the active immutable snapshot in memory (thread-safe). Loads the last published snapshot
/// from the store at startup (verifying its signature — a tampered snapshot is refused), falling
/// back to the seed. A publish swaps the active snapshot with no redeploy.
/// </summary>
public sealed class PublishablePolicySnapshotProvider : IPublishablePolicySnapshotProvider
{
    private readonly object _lock = new();
    private PolicySnapshot _current;
    private string _hash;

    public PublishablePolicySnapshotProvider(
        IPolicySnapshotStore store, PolicySnapshot seed, IReceiptSigner signer,
        ILogger<PublishablePolicySnapshotProvider> logger)
    {
        var published = store.LoadCurrent();
        _current = published ?? seed;
        _hash = FilePolicySnapshotProvider.ComputeHash(_current);

        if (published is not null && !string.IsNullOrEmpty(published.Signature))
        {
            var valid = signer.VerifyAsync(_hash, published.Signature!, CancellationToken.None).GetAwaiter().GetResult();
            if (!valid)
                throw new InvalidOperationException($"Published policy snapshot v{published.Version} signature is INVALID — refusing to load a tampered policy.");
            logger.LogInformation("Loaded + verified published policy snapshot v{Version} (hash {Hash}).", published.Version, _hash);
        }
        else
        {
            logger.LogInformation("Using {Source} policy snapshot v{Version} (hash {Hash}).",
                published is null ? "seed" : "published-unsigned", _current.Version, _hash);
        }
    }

    public PolicySnapshot Current { get { lock (_lock) return _current; } }
    public string ActiveHash { get { lock (_lock) return _hash; } }

    public void Set(PolicySnapshot snapshot, string hash)
    {
        lock (_lock) { _current = snapshot; _hash = hash; }
    }
}

public interface IPolicyPublisher
{
    Task<PublishResult> PublishAsync(PolicySnapshot authored, string? publisher, CancellationToken ct);
}

/// <summary>
/// Compiles an authored policy into an immutable, versioned, signed snapshot and publishes it:
/// validate → stamp metadata → hash → sign (Key Vault) → persist → swap the runtime's active
/// snapshot. This is the bridge that turns authored config into provably-enforced policy.
/// </summary>
public sealed class PolicyPublisher(
    IPolicySnapshotStore store,
    IPublishablePolicySnapshotProvider provider,
    IReceiptSigner signer,
    ILogger<PolicyPublisher> logger) : IPolicyPublisher
{
    private static readonly HashSet<string> ValidOperators = new(StringComparer.Ordinal)
        { "equals", "notEquals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual", "in", "notIn", "exists" };
    private static readonly HashSet<string> ValidDecisions = new(StringComparer.Ordinal)
        { "ALLOW", "DENY", "REQUIRE_APPROVAL" };

    public async Task<PublishResult> PublishAsync(PolicySnapshot authored, string? publisher, CancellationToken ct)
    {
        Validate(authored);

        authored.PublishedAtUtc = DateTimeOffset.UtcNow;
        authored.Publisher = publisher ?? "unknown";

        var hash = FilePolicySnapshotProvider.ComputeHash(authored);
        authored.Hash = hash;

        var signature = await signer.SignAsync(hash, ct);
        authored.Signature = signature?.Value;
        authored.SignatureAlgorithm = signature?.Algorithm;
        authored.SigningKeyId = signature?.KeyId;

        store.SaveCurrent(authored);
        provider.Set(authored, hash);

        logger.LogInformation("Published policy {Code} v{Version} (hash {Hash}, signed={Signed}) by {Publisher}.",
            authored.PolicyCode, authored.Version, hash, signature is not null, authored.Publisher);

        return new PublishResult
        {
            PolicyCode = authored.PolicyCode,
            Version = authored.Version,
            Hash = hash,
            Signed = signature is not null,
            PublishedAtUtc = authored.PublishedAtUtc.Value
        };
    }

    private static void Validate(PolicySnapshot s)
    {
        if (string.IsNullOrWhiteSpace(s.PolicyCode)) throw new PolicyValidationException("policyCode is required.");
        if (string.IsNullOrWhiteSpace(s.Version)) throw new PolicyValidationException("version is required.");
        if (s.Rules.Count == 0) throw new PolicyValidationException("at least one rule is required.");
        if (!string.IsNullOrEmpty(s.DefaultDecision) && !ValidDecisions.Contains(s.DefaultDecision))
            throw new PolicyValidationException($"invalid defaultDecision '{s.DefaultDecision}'.");

        foreach (var r in s.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Id)) throw new PolicyValidationException("every rule needs an id.");
            if (!ValidDecisions.Contains(r.Decision))
                throw new PolicyValidationException($"rule '{r.Id}': invalid decision '{r.Decision}'.");
            foreach (var c in r.Conditions)
            {
                if (!ValidOperators.Contains(c.Operator))
                    throw new PolicyValidationException($"rule '{r.Id}': invalid operator '{c.Operator}'.");
                if (c.Aggregate is { } agg) ValidateAggregate(r, c, agg);
            }
        }
    }

    /// <summary>
    /// A windowed-aggregate condition must be well formed at PUBLISH time — a bad window or an unknown
    /// function would otherwise fail every request the rule touches, at runtime, in production.
    /// </summary>
    private static void ValidateAggregate(PolicyRule r, PolicyCondition c, PolicyAggregate agg)
    {
        if (!PolicyAggregate.Functions.Contains(agg.Function, StringComparer.OrdinalIgnoreCase))
            throw new PolicyValidationException(
                $"rule '{r.Id}': invalid aggregate function '{agg.Function}' (expected {string.Join(", ", PolicyAggregate.Functions)}).");

        if (!PolicyAggregate.Scopes.Contains(agg.Scope, StringComparer.OrdinalIgnoreCase))
            throw new PolicyValidationException(
                $"rule '{r.Id}': invalid aggregate scope '{agg.Scope}' (expected {string.Join(", ", PolicyAggregate.Scopes)}).");

        try { PolicyAggregateProvider.ParseWindow(agg.Window); }
        catch (InvalidOperationException ex) { throw new PolicyValidationException($"rule '{r.Id}': {ex.Message}"); }

        if (agg.GroupBy is null || agg.GroupBy.Length == 0)
            throw new PolicyValidationException($"rule '{r.Id}': aggregate groupBy must name at least one field (e.g. agentId).");

        foreach (var g in agg.GroupBy)
            if (!SafeFieldName.IsMatch(g ?? string.Empty))
                throw new PolicyValidationException($"rule '{r.Id}': aggregate groupBy '{g}' is not a valid field name.");

        // count ignores the field; every other function needs one to aggregate over.
        if (!string.Equals(agg.Function, PolicyAggregate.Count, StringComparison.OrdinalIgnoreCase)
            && !SafeFieldName.IsMatch(c.Field ?? string.Empty))
            throw new PolicyValidationException(
                $"rule '{r.Id}': aggregate function '{agg.Function}' needs a valid field to aggregate (got '{c.Field}').");

        if (agg.Actions is { } acts && acts.Any(string.IsNullOrWhiteSpace))
            throw new PolicyValidationException($"rule '{r.Id}': aggregate actions must not contain blank entries.");

        // Ordering/equality against an aggregate only makes sense numerically.
        if (c.Operator is "in" or "notIn" or "exists")
            throw new PolicyValidationException(
                $"rule '{r.Id}': operator '{c.Operator}' cannot be used with an aggregate (aggregates yield a number).");
    }

    private static readonly System.Text.RegularExpressions.Regex SafeFieldName =
        new("^[A-Za-z0-9_.-]{1,100}$", System.Text.RegularExpressions.RegexOptions.Compiled);
}
