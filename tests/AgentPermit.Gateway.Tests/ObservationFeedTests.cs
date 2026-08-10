using System.Text.Json;
using AgentPermit.Gateway.Observations;
using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Tests;

/// <summary>The feed-agnostic ingestion framework: status tracking, cursor persistence, external-feed
/// mappers, and the secure default that external feeds are OFF (no external cost incurred implicitly).</summary>
public sealed class ObservationFeedTests
{
    // ---- External feeds are disabled by default ----

    [Fact]
    public void ExternalFeeds_AreOffByDefault()
    {
        Assert.False(new PurviewFeedOptions().Enabled);
        Assert.False(new FlowRunFeedOptions().Enabled);
    }

    // ---- Status tracker ----

    [Fact]
    public void Tracker_SeedsGateway_AlwaysOnPush()
    {
        var t = new FeedStatusTracker();
        var gw = t.All().Single(f => f.Name == FeedStatusTracker.GatewayFeedName);
        Assert.True(gw.Enabled);
        Assert.Equal(FeedMode.Push, gw.Mode);

        t.RecordGatewayPush();
        t.RecordGatewayPush();
        Assert.Equal(2, t.All().Single(f => f.Name == FeedStatusTracker.GatewayFeedName).TotalIngested);
    }

    [Fact]
    public void Tracker_RecordsPullRun_AndErrors()
    {
        var t = new FeedStatusTracker();
        t.Register(new PurviewAuditFeed(new PurviewFeedOptions { Enabled = true }, null!));
        t.RecordRun("purview-audit", batch: 5, cursor: "2026-08-04T00:00:00", error: null);
        var s = t.All().Single(f => f.Name == "purview-audit");
        Assert.Equal(FeedMode.Pull, s.Mode);
        Assert.Equal(5, s.TotalIngested);
        Assert.Equal("2026-08-04T00:00:00", s.LastCursor);

        t.RecordRun("purview-audit", 0, null, "boom");
        Assert.Equal("boom", t.All().Single(f => f.Name == "purview-audit").LastError);
    }

    // ---- Cursor persistence (reuses the config store) ----

    [Fact]
    public async Task Cursor_RoundTrips()
    {
        var store = new ObservationCursorStore(new InMemoryConfigStore());
        Assert.Null(await store.GetAsync("purview-audit", default));
        await store.SetAsync("purview-audit", "2026-08-04T10:00:00", default);
        Assert.Equal("2026-08-04T10:00:00", await store.GetAsync("purview-audit", default));
    }

    // ---- External event mappers (the tenant-independent core) ----

    [Fact]
    public void Purview_MapsConnectorEvent_ToUngovernedObservation()
    {
        var e = JsonDocument.Parse("""
            {"Id":"evt-1","CreationTime":"2026-08-04T09:00:00","Operation":"CreateConnection",
             "UserId":"agent@contoso.com","ConnectorId":"shared_sql","Workload":"PowerPlatform"}
            """).RootElement;
        var o = PurviewAuditFeed.MapAuditRecord(e)!;
        Assert.NotNull(o);
        Assert.Equal(ObservationSource.PurviewAudit, o.Source);
        Assert.Equal("agent@contoso.com", o.AgentId);
        Assert.Equal("CreateConnection", o.ActionId);
        Assert.Equal("shared_sql", o.TargetSystem);
        Assert.Equal(ExternalObservation.Ungoverned, o.EffectiveDecision);
    }

    [Fact]
    public void Purview_SkipsRecordsMissingOperationOrActor()
    {
        var e = JsonDocument.Parse("""{"Id":"x","CreationTime":"2026-08-04T09:00:00"}""").RootElement;
        Assert.Null(PurviewAuditFeed.MapAuditRecord(e));
    }

    [Fact]
    public void FlowRun_MapsRun_ToUngovernedObservation()
    {
        var e = JsonDocument.Parse("""
            {"runId":"r1","flowName":"NightlyExport","connector":"shared_sql","status":"Succeeded","startTime":"2026-08-04T02:00:00"}
            """).RootElement;
        var o = FlowRunFeed.MapFlowRun(e)!;
        Assert.Equal(ObservationSource.FlowRun, o.Source);
        Assert.Equal("NightlyExport", o.AgentId);
        Assert.Equal("shared_sql", o.TargetSystem);
        Assert.Equal(ExternalObservation.Ungoverned, o.EffectiveDecision);
    }

    // ---- Enabled-but-unconfigured feeds fail loudly (surfaced in status), never silently ----

    [Fact]
    public async Task PurviewFeed_EnabledButUnconfigured_Throws()
    {
        var feed = new PurviewAuditFeed(new PurviewFeedOptions { Enabled = true }, new StubHttpFactory());
        await Assert.ThrowsAsync<InvalidOperationException>(() => feed.PullAsync(null, default));
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
