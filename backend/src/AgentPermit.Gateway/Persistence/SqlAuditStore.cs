using Dapper;
using Microsoft.Data.SqlClient;

namespace AgentPermit.Gateway.Persistence;

/// <summary>Azure SQL persistence for the append-only audit trail (dbo.AuditEvent).</summary>
public sealed class SqlAuditStore(string connectionString) : IAuditStore
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

    public async Task AppendAsync(AuditEventRecord e, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.AuditEvent (AuditEventId, OccurredUtc, Actor, ActorId, Action, TargetType, TargetId, Summary)
            VALUES (@AuditEventId, @OccurredUtc, @Actor, @ActorId, @Action, @TargetType, @TargetId, @Summary);
            """,
            new
            {
                e.AuditEventId, OccurredUtc = e.OccurredUtc.UtcDateTime, e.Actor, e.ActorId,
                e.Action, e.TargetType, e.TargetId, e.Summary
            });
    }

    public async Task<IReadOnlyList<AuditEventRecord>> ListRecentAsync(int take, CancellationToken ct)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<Row>(
            "SELECT TOP (@take) * FROM dbo.AuditEvent ORDER BY OccurredUtc DESC",
            new { take = take <= 0 ? 100 : take });
        return rows.Select(r => r.Map()).ToList();
    }

    private sealed class Row
    {
        public Guid AuditEventId { get; set; }
        public DateTime OccurredUtc { get; set; }
        public string Actor { get; set; } = "";
        public string? ActorId { get; set; }
        public string Action { get; set; } = "";
        public string TargetType { get; set; } = "";
        public string? TargetId { get; set; }
        public string? Summary { get; set; }

        public AuditEventRecord Map() => new()
        {
            AuditEventId = AuditEventId,
            OccurredUtc = new DateTimeOffset(DateTime.SpecifyKind(OccurredUtc, DateTimeKind.Utc)),
            Actor = Actor, ActorId = ActorId, Action = Action, TargetType = TargetType,
            TargetId = TargetId, Summary = Summary
        };
    }
}
