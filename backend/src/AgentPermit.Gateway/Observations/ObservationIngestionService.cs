using Microsoft.Extensions.Hosting;

namespace AgentPermit.Gateway.Observations;

/// <summary>
/// Periodically polls every ENABLED pull feed, appends what it returns to the observation store, and
/// advances that feed's cursor so it resumes exactly where it left off. A single feed failing is isolated
/// (logged + surfaced in status) and never stops the others. With no external feeds enabled this loop does
/// nothing but idle — the always-on GATEWAY push source needs no polling.
/// </summary>
public sealed class ObservationIngestionService(
    IEnumerable<IObservationFeed> feeds,
    IObservationStore store,
    ObservationCursorStore cursors,
    FeedStatusTracker status,
    ObservationFeedOptions options,
    ILogger<ObservationIngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var feed in feeds) status.Register(feed);

        var enabled = feeds.Where(f => f.Enabled).ToList();
        if (enabled.Count == 0)
            logger.LogInformation("Observation ingestion: no external feeds enabled — gateway push source only.");
        else
            logger.LogInformation("Observation ingestion: {Count} feed(s) enabled: {Names}.",
                enabled.Count, string.Join(", ", enabled.Select(f => f.Name)));

        var interval = TimeSpan.FromSeconds(Math.Max(30, options.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var feed in feeds.Where(f => f.Enabled))
                await RunFeedAsync(feed, stoppingToken);

            try { await Task.Delay(interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task RunFeedAsync(IObservationFeed feed, CancellationToken ct)
    {
        try
        {
            var cursor = await cursors.GetAsync(feed.Name, ct);
            var result = await feed.PullAsync(cursor, ct);
            foreach (var o in result.Observations) await store.AppendAsync(o, ct);
            if (result.NextCursor is not null) await cursors.SetAsync(feed.Name, result.NextCursor, ct);
            status.RecordRun(feed.Name, result.Observations.Count, result.NextCursor, error: null);
            if (result.Observations.Count > 0)
                logger.LogInformation("Feed {Feed} ingested {Count} observation(s).", feed.Name, result.Observations.Count);
        }
        catch (Exception ex)
        {
            status.RecordRun(feed.Name, 0, null, ex.Message);
            logger.LogWarning(ex, "Observation feed {Feed} failed (isolated; other feeds continue).", feed.Name);
        }
    }
}
