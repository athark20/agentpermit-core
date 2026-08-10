using System.Collections.Concurrent;

namespace AgentPermit.Gateway.Observations;

/// <summary>
/// A pluggable source of observations. The built-in GATEWAY source is PUSH-based — written inline on every
/// decision, always on, zero external cost. External sources (Purview audit, Power Automate run history) are
/// PULL-based and polled by the ingestion service; they are OFF by default and enabled per customer only
/// when their licensing supports it, so no external cost is ever incurred implicitly.
/// </summary>
public interface IObservationFeed
{
    /// <summary>Stable feed id, e.g. "purview-audit".</summary>
    string Name { get; }

    /// <summary>Which <see cref="ObservationSource"/> this feed produces.</summary>
    string Source { get; }

    /// <summary>Config-gated. When false the ingestion service never calls this feed.</summary>
    bool Enabled { get; }

    /// <summary>Pull observations after <paramref name="cursor"/>; returns the batch and the next cursor.</summary>
    Task<FeedPullResult> PullAsync(string? cursor, CancellationToken ct);
}

public sealed class FeedPullResult
{
    public IReadOnlyList<ObservationRecord> Observations { get; init; } = Array.Empty<ObservationRecord>();
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>Options bound from "Observations" — the poll cadence and per-external-feed config.</summary>
public sealed class ObservationFeedOptions
{
    public int PollIntervalSeconds { get; set; } = 300;
    public PurviewFeedOptions Purview { get; set; } = new();
    public FlowRunFeedOptions FlowRun { get; set; } = new();
}

public sealed class PurviewFeedOptions
{
    /// <summary>OFF by default — enable only when the tenant has M365 audit (Standard is enough) + an app registration.</summary>
    public bool Enabled { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string ContentType { get; set; } = "Audit.General";
}

public sealed class FlowRunFeedOptions
{
    /// <summary>OFF by default — enable only when a Power Platform environment + app registration is configured.</summary>
    public bool Enabled { get; set; }
    public string? EnvironmentUrl { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary>How a feed is producing data — for the admin operations view.</summary>
public static class FeedMode
{
    public const string Push = "PUSH"; // written inline (gateway)
    public const string Pull = "PULL"; // polled by the ingestion service
}

public sealed class FeedStatus
{
    public required string Name { get; init; }
    public required string Source { get; init; }
    public required string Mode { get; init; }
    public required bool Enabled { get; init; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public string? LastCursor { get; set; }
    public long TotalIngested { get; set; }
    public int LastBatch { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Thread-safe, in-memory feed status for the admin operations view. Seeds the always-on GATEWAY
/// push source and tracks each pull feed's last run / cursor / count / error.</summary>
public sealed class FeedStatusTracker
{
    public const string GatewayFeedName = "gateway";
    private readonly ConcurrentDictionary<string, FeedStatus> _feeds = new(StringComparer.OrdinalIgnoreCase);

    public FeedStatusTracker()
    {
        _feeds[GatewayFeedName] = new FeedStatus
        {
            Name = GatewayFeedName, Source = ObservationSource.Gateway, Mode = FeedMode.Push, Enabled = true
        };
    }

    /// <summary>Register a pull feed so it shows in the status even before its first run.</summary>
    public void Register(IObservationFeed feed) => _feeds.TryAdd(feed.Name, new FeedStatus
    {
        Name = feed.Name, Source = feed.Source, Mode = FeedMode.Pull, Enabled = feed.Enabled
    });

    /// <summary>The gateway wrote one observation inline.</summary>
    public void RecordGatewayPush()
    {
        if (_feeds.TryGetValue(GatewayFeedName, out var s))
        {
            s.LastRunUtc = DateTimeOffset.UtcNow;
            s.TotalIngested++;
        }
    }

    public void RecordRun(string name, int batch, string? cursor, string? error)
    {
        if (!_feeds.TryGetValue(name, out var s)) return;
        s.LastRunUtc = DateTimeOffset.UtcNow;
        s.LastBatch = batch;
        s.TotalIngested += batch;
        if (cursor is not null) s.LastCursor = cursor;
        s.LastError = error;
    }

    public IReadOnlyList<FeedStatus> All() => _feeds.Values.OrderBy(f => f.Name).ToList();
}

/// <summary>Per-feed ingestion cursor, persisted in the config store (no dedicated table needed).</summary>
public sealed class ObservationCursorStore(AgentPermit.Gateway.Persistence.IConfigStore config)
{
    private const string Type = "observation-cursor";

    public async Task<string?> GetAsync(string feed, CancellationToken ct)
    {
        var rec = await config.GetAsync(Type, feed, ct);
        if (rec is null) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rec.Json);
            return doc.RootElement.TryGetProperty("cursor", out var c) ? c.GetString() : null;
        }
        catch { return null; }
    }

    public Task SetAsync(string feed, string? cursor, CancellationToken ct)
        => config.UpsertAsync(Type, feed,
            System.Text.Json.JsonSerializer.Serialize(new { cursor }), ct);
}
