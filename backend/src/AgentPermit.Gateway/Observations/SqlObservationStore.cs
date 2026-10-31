using Dapper;
using Microsoft.Data.SqlClient;

namespace AgentPermit.Gateway.Observations;

/// <summary>Azure SQL persistence for the append-only observation trail (dbo.Observation).</summary>
public sealed class SqlObservationStore(string connectionString) : IObservationStore
{
    private SqlConnection Open()
    {
        for (var attempt = 1; ; attempt++)
        {
            var c = new SqlConnection(connectionString);
            try { c.Open(); return c; }
            catch (SqlException) when (attempt < 5) { c.Dispose(); Thread.Sleep(3000 * attempt); }
        }
    }

    public async Task AppendAsync(ObservationRecord o, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.Observation
                (ObservationId, OccurredUtc, Source, AgentId, ActionId, TargetSystem, ComputedDecision,
                 EffectiveDecision, EnforcementMode, ConsequenceTier, WouldHaveBlocked, CorrelationId)
            VALUES
                (@ObservationId, @OccurredUtc, @Source, @AgentId, @ActionId, @TargetSystem, @ComputedDecision,
                 @EffectiveDecision, @EnforcementMode, @ConsequenceTier, @WouldHaveBlocked, @CorrelationId);
            """,
            new
            {
                o.ObservationId, OccurredUtc = o.OccurredUtc.UtcDateTime, o.Source, o.AgentId, o.ActionId,
                o.TargetSystem, o.ComputedDecision, o.EffectiveDecision, o.EnforcementMode, o.ConsequenceTier,
                o.WouldHaveBlocked, o.CorrelationId
            });
    }

    public async Task<AgentObservationStats> GetAgentStatsAsync(string agentId, DateTimeOffset since, CancellationToken ct)
    {
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync(
            """
            SELECT
                COUNT(*)                                                        AS Total,
                SUM(CASE WHEN WouldHaveBlocked = 1 THEN 1 ELSE 0 END)           AS WouldHaveBlocked,
                SUM(CASE WHEN EffectiveDecision = 'ALLOW' THEN 1 ELSE 0 END)    AS EffectiveAllow,
                SUM(CASE WHEN EffectiveDecision = 'DENY' THEN 1 ELSE 0 END)     AS EffectiveDeny,
                SUM(CASE WHEN EffectiveDecision = 'REQUIRE_APPROVAL' THEN 1 ELSE 0 END) AS EffectiveApproval,
                MIN(OccurredUtc)                                                AS FirstObservedUtc,
                MAX(OccurredUtc)                                                AS LastObservedUtc
            FROM dbo.Observation
            WHERE AgentId = @agentId AND OccurredUtc >= @since AND Source <> @safety;
            """,
            new { agentId, since = since.UtcDateTime, safety = ObservationSource.GatewaySafety });

        int I(object? v) => v is null ? 0 : Convert.ToInt32(v);
        DateTimeOffset? D(object? v) => v is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;

        return new AgentObservationStats
        {
            AgentId = agentId,
            Total = I(row?.Total),
            WouldHaveBlocked = I(row?.WouldHaveBlocked),
            EffectiveAllow = I(row?.EffectiveAllow),
            EffectiveDeny = I(row?.EffectiveDeny),
            EffectiveApproval = I(row?.EffectiveApproval),
            FirstObservedUtc = D(row?.FirstObservedUtc),
            LastObservedUtc = D(row?.LastObservedUtc)
        };
    }

    public async Task<int> CountDenialsAsync(string agentId, DateTimeOffset since, CancellationToken ct)
    {
        await using var c = Open();
        return await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Observation WHERE AgentId = @agentId AND OccurredUtc >= @since AND EffectiveDecision = 'DENY'",
            new { agentId, since = since.UtcDateTime });
    }

    public async Task<IReadOnlyList<ObservationRecord>> ListForAgentAsync(string agentId, int limit, CancellationToken ct)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<Row>(
            "SELECT TOP (@limit) * FROM dbo.Observation WHERE AgentId = @agentId ORDER BY OccurredUtc DESC",
            new { agentId, limit });
        return rows.Select(r => r.Map()).ToList();
    }

    private sealed class Row
    {
        public Guid ObservationId { get; set; }
        public DateTime OccurredUtc { get; set; }
        public string Source { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string ActionId { get; set; } = "";
        public string? TargetSystem { get; set; }
        public string ComputedDecision { get; set; } = "";
        public string EffectiveDecision { get; set; } = "";
        public string EnforcementMode { get; set; } = "";
        public string ConsequenceTier { get; set; } = "";
        public bool WouldHaveBlocked { get; set; }
        public string? CorrelationId { get; set; }

        public ObservationRecord Map() => new()
        {
            ObservationId = ObservationId,
            OccurredUtc = new DateTimeOffset(DateTime.SpecifyKind(OccurredUtc, DateTimeKind.Utc)),
            Source = Source, AgentId = AgentId, ActionId = ActionId, TargetSystem = TargetSystem,
            ComputedDecision = ComputedDecision, EffectiveDecision = EffectiveDecision,
            EnforcementMode = EnforcementMode, ConsequenceTier = ConsequenceTier,
            WouldHaveBlocked = WouldHaveBlocked, CorrelationId = CorrelationId
        };
    }
}
