using AgentPermit.Gateway.Persistence;

namespace AgentPermit.Gateway.Tests;

/// <summary>Metrics normalize legacy status spellings into canonical buckets for a consistent dashboard.</summary>
public sealed class MetricsBuilderTests
{
    [Fact]
    public void FoldsLegacyAndCanonicalStatuses()
    {
        var all = new[] { ("Executed", 11), ("SUCCEEDED", 8), ("Denied", 16), ("REJECTED", 1), ("PendingApproval", 4), ("Failed", 3) };
        var today = all;

        var m = MetricsBuilder.Build(all, today);

        Assert.Equal(19, m.ByStatus["SUCCEEDED"]);      // Executed + SUCCEEDED merged
        Assert.Equal(16, m.ByStatus["DENIED"]);
        Assert.Equal(4, m.ByStatus["AWAITING_APPROVAL"]); // PendingApproval mapped
        Assert.Equal(43, m.TotalRequests);
        Assert.Equal(19, m.TodayAllowed);
        Assert.Equal(17, m.TodayDenied);                 // DENIED + REJECTED
        Assert.Equal(4, m.TodayApproval);
        Assert.Equal(3, m.TodayFailed);
    }
}
