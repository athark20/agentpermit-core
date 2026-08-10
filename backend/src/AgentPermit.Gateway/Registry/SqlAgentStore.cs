using Dapper;
using Microsoft.Data.SqlClient;

namespace AgentPermit.Gateway.Registry;

/// <summary>Azure SQL persistence for the Agent Registry (dbo.Agent).</summary>
public sealed class SqlAgentStore(string connectionString) : IAgentStore
{
    private SqlConnection Open()
    {
        // Small transient retry for serverless resume.
        for (var attempt = 1; ; attempt++)
        {
            var c = new SqlConnection(connectionString);
            try { c.Open(); return c; }
            catch (SqlException) when (attempt < 5) { c.Dispose(); Thread.Sleep(3000 * attempt); }
        }
    }

    public async Task<IReadOnlyList<AgentInfo>> ListAsync(CancellationToken ct)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<Row>("SELECT * FROM dbo.Agent ORDER BY AgentId");
        return rows.Select(r => r.Map()).ToList();
    }

    public async Task<AgentInfo?> GetAsync(string agentId, CancellationToken ct)
    {
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<Row>(
            "SELECT * FROM dbo.Agent WHERE AgentId = @agentId", new { agentId });
        return row?.Map();
    }

    public async Task UpsertAsync(AgentInfo a, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            MERGE dbo.Agent AS t
            USING (SELECT @AgentId AS AgentId) AS s ON t.AgentId = s.AgentId
            WHEN MATCHED THEN UPDATE SET
                DisplayName=@DisplayName, EntraClientId=@EntraClientId, Enabled=@Enabled,
                Type=@Type, Owner=@Owner, Environment=@Environment, RiskTier=@RiskTier,
                EnforcementPosture=@EnforcementPosture, UpdatedUtc=SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (AgentId, DisplayName, EntraClientId, Enabled, Type, Owner, Environment, RiskTier, EnforcementPosture)
            VALUES
                (@AgentId, @DisplayName, @EntraClientId, @Enabled, @Type, @Owner, @Environment, @RiskTier, @EnforcementPosture);
            """,
            new { a.AgentId, a.DisplayName, a.EntraClientId, a.Enabled, a.Type, a.Owner, a.Environment, a.RiskTier, a.EnforcementPosture });
    }

    public async Task<bool> DeleteAsync(string agentId, CancellationToken ct)
    {
        await using var c = Open();
        return await c.ExecuteAsync("DELETE FROM dbo.Agent WHERE AgentId = @agentId", new { agentId }) > 0;
    }

    private sealed class Row
    {
        public string AgentId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? EntraClientId { get; set; }
        public bool Enabled { get; set; }
        public string? Type { get; set; }
        public string? Owner { get; set; }
        public string? Environment { get; set; }
        public string? RiskTier { get; set; }
        public string? EnforcementPosture { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public AgentInfo Map() => new()
        {
            AgentId = AgentId, DisplayName = DisplayName, EntraClientId = EntraClientId, Enabled = Enabled,
            Type = Type, Owner = Owner, Environment = Environment, RiskTier = RiskTier,
            EnforcementPosture = EnforcementPosture,
            UpdatedUtc = new DateTimeOffset(DateTime.SpecifyKind(UpdatedUtc, DateTimeKind.Utc))
        };
    }
}
