using Dapper;
using Microsoft.Data.SqlClient;

namespace AgentPermit.Gateway.Persistence;

/// <summary>Azure SQL persistence for control-plane configuration documents (dbo.ConfigEntity).</summary>
public sealed class SqlConfigStore(string connectionString) : IConfigStore
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

    public async Task<IReadOnlyList<ConfigEntityRecord>> ListAsync(string type, CancellationToken ct)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<Row>(
            "SELECT * FROM dbo.ConfigEntity WHERE EntityType = @type ORDER BY EntityId", new { type });
        return rows.Select(r => r.Map()).ToList();
    }

    public async Task<ConfigEntityRecord?> GetAsync(string type, string id, CancellationToken ct)
    {
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<Row>(
            "SELECT * FROM dbo.ConfigEntity WHERE EntityType = @type AND EntityId = @id", new { type, id });
        return row?.Map();
    }

    public async Task UpsertAsync(string type, string id, string json, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            MERGE dbo.ConfigEntity AS t
            USING (SELECT @type AS EntityType, @id AS EntityId) AS s
              ON t.EntityType = s.EntityType AND t.EntityId = s.EntityId
            WHEN MATCHED THEN UPDATE SET Json = @json, UpdatedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (EntityType, EntityId, Json) VALUES (@type, @id, @json);
            """,
            new { type, id, json });
    }

    public async Task<bool> DeleteAsync(string type, string id, CancellationToken ct)
    {
        await using var c = Open();
        return await c.ExecuteAsync(
            "DELETE FROM dbo.ConfigEntity WHERE EntityType = @type AND EntityId = @id", new { type, id }) > 0;
    }

    private sealed class Row
    {
        public string EntityType { get; set; } = "";
        public string EntityId { get; set; } = "";
        public string Json { get; set; } = "{}";
        public DateTime UpdatedUtc { get; set; }

        public ConfigEntityRecord Map() => new()
        {
            EntityType = EntityType, EntityId = EntityId, Json = Json,
            UpdatedUtc = new DateTimeOffset(DateTime.SpecifyKind(UpdatedUtc, DateTimeKind.Utc))
        };
    }
}
