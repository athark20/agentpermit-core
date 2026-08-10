using System.Text.Json;
using AgentPermit.Gateway.Common;

namespace AgentPermit.Gateway.Policies;

public sealed class PolicySnapshotOptions
{
    /// <summary>Path (relative to the app base dir, or absolute) to the published snapshot JSON.</summary>
    public string SnapshotPath { get; set; } = "seed/policy-snapshot.json";

    /// <summary>When true, a snapshot whose stored hash doesn't match its rules is rejected.</summary>
    public bool VerifyHash { get; set; } = false;
}

/// <summary>Supplies the active, immutable policy snapshot to the runtime (cached).</summary>
public interface IPolicySnapshotProvider
{
    PolicySnapshot Current { get; }

    /// <summary>The integrity hash the runtime computed/verified for the active snapshot.</summary>
    string ActiveHash { get; }
}

/// <summary>
/// Loads a published snapshot from disk once at startup and caches it. This is the seam the
/// enterprise policy publisher targets: later, "load from file" becomes "load the latest
/// published, signed snapshot from Blob/Dataverse" without touching the engine.
/// </summary>
public sealed class FilePolicySnapshotProvider : IPolicySnapshotProvider
{
    public PolicySnapshot Current { get; }
    public string ActiveHash { get; }

    public FilePolicySnapshotProvider(PolicySnapshotOptions options, string baseDir, ILogger<FilePolicySnapshotProvider> logger)
    {
        var path = Path.IsPathRooted(options.SnapshotPath)
            ? options.SnapshotPath
            : Path.Combine(baseDir, options.SnapshotPath);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Policy snapshot not found: {path}");

        var json = File.ReadAllText(path);
        Current = JsonSerializer.Deserialize<PolicySnapshot>(json)
                  ?? throw new InvalidOperationException("Policy snapshot could not be parsed.");

        ActiveHash = ComputeHash(Current);

        if (!string.IsNullOrEmpty(Current.Hash) &&
            !string.Equals(Current.Hash, ActiveHash, StringComparison.OrdinalIgnoreCase))
        {
            var msg = $"Policy snapshot integrity hash mismatch (stored {Current.Hash}, computed {ActiveHash}).";
            if (options.VerifyHash) throw new InvalidOperationException(msg);
            logger.LogWarning("{Message} Continuing because Policy:VerifyHash is disabled.", msg);
        }

        logger.LogInformation("Loaded policy snapshot {Code} v{Version} ({Rules} rules), hash {Hash}.",
            Current.PolicyCode, Current.Version, Current.Rules.Count, ActiveHash);
    }

    /// <summary>Load a snapshot from a JSON file (used to seed the publishable provider).</summary>
    public static PolicySnapshot LoadFromFile(PolicySnapshotOptions options, string baseDir)
    {
        var path = Path.IsPathRooted(options.SnapshotPath) ? options.SnapshotPath : Path.Combine(baseDir, options.SnapshotPath);
        if (!File.Exists(path)) throw new FileNotFoundException($"Policy snapshot not found: {path}");
        return JsonSerializer.Deserialize<PolicySnapshot>(File.ReadAllText(path))
               ?? throw new InvalidOperationException("Policy snapshot could not be parsed.");
    }

    /// <summary>Canonical hash over the ordered rules — the snapshot's integrity identity.</summary>
    public static string ComputeHash(PolicySnapshot snapshot)
    {
        var canonicalRules = JsonSerializer.SerializeToElement(
            snapshot.Rules.OrderBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal));
        return CanonicalJson.Hash(canonicalRules);
    }
}
