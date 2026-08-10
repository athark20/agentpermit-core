using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AgentPermit.Gateway.ControlPlane;

/// <summary>Azure SQL persistence for the Target System Registry (dbo.TargetSystem).</summary>
public sealed class SqlTargetSystemStore(string connectionString) : ITargetSystemStore
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

    public async Task<IReadOnlyList<TargetSystemRecord>> ListAsync(CancellationToken ct)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<Row>("SELECT * FROM dbo.TargetSystem ORDER BY TargetSystemCode");
        return rows.Select(r => r.Map()).ToList();
    }

    public async Task<TargetSystemRecord?> GetAsync(string code, CancellationToken ct)
    {
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<Row>(
            "SELECT * FROM dbo.TargetSystem WHERE TargetSystemCode = @code", new { code });
        return row?.Map();
    }

    public async Task UpsertAsync(TargetSystemRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            MERGE dbo.TargetSystem AS t
            USING (SELECT @TargetSystemCode AS TargetSystemCode) AS s ON t.TargetSystemCode = s.TargetSystemCode
            WHEN MATCHED THEN UPDATE SET
                DisplayName=@DisplayName, AdapterType=@AdapterType, BaseUrl=@BaseUrl, ExecutePath=@ExecutePath,
                AuthMode=@AuthMode, CredentialRef=@CredentialRef, TimeoutSeconds=@TimeoutSeconds, MaxRetries=@MaxRetries,
                AllowedActionsJson=@AllowedActionsJson, Environment=@Environment, DataResidency=@DataResidency,
                OperationalOwner=@OperationalOwner, ConsequenceTier=@ConsequenceTier, Enabled=@Enabled, UpdatedUtc=SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (TargetSystemCode, DisplayName, AdapterType, BaseUrl, ExecutePath, AuthMode, CredentialRef,
                 TimeoutSeconds, MaxRetries, AllowedActionsJson, Environment, DataResidency, OperationalOwner, ConsequenceTier, Enabled)
            VALUES
                (@TargetSystemCode, @DisplayName, @AdapterType, @BaseUrl, @ExecutePath, @AuthMode, @CredentialRef,
                 @TimeoutSeconds, @MaxRetries, @AllowedActionsJson, @Environment, @DataResidency, @OperationalOwner, @ConsequenceTier, @Enabled);
            """,
            new
            {
                r.TargetSystemCode, r.DisplayName, r.AdapterType, r.BaseUrl, r.ExecutePath, r.AuthMode, r.CredentialRef,
                r.TimeoutSeconds, r.MaxRetries,
                AllowedActionsJson = r.AllowedActions.Length == 0 ? null : JsonSerializer.Serialize(r.AllowedActions),
                r.Environment, r.DataResidency, r.OperationalOwner, r.ConsequenceTier, r.Enabled
            });
    }

    public async Task<bool> DeleteAsync(string code, CancellationToken ct)
    {
        await using var c = Open();
        return await c.ExecuteAsync("DELETE FROM dbo.TargetSystem WHERE TargetSystemCode = @code", new { code }) > 0;
    }

    private sealed class Row
    {
        public string TargetSystemCode { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string AdapterType { get; set; } = "";
        public string? BaseUrl { get; set; }
        public string? ExecutePath { get; set; }
        public string? AuthMode { get; set; }
        public string? CredentialRef { get; set; }
        public int TimeoutSeconds { get; set; }
        public int MaxRetries { get; set; }
        public string? AllowedActionsJson { get; set; }
        public string? Environment { get; set; }
        public string? DataResidency { get; set; }
        public string? OperationalOwner { get; set; }
        public string? ConsequenceTier { get; set; }
        public bool Enabled { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public TargetSystemRecord Map() => new()
        {
            TargetSystemCode = TargetSystemCode, DisplayName = DisplayName, AdapterType = AdapterType,
            BaseUrl = BaseUrl, ExecutePath = ExecutePath, AuthMode = AuthMode, CredentialRef = CredentialRef,
            TimeoutSeconds = TimeoutSeconds, MaxRetries = MaxRetries,
            AllowedActions = string.IsNullOrEmpty(AllowedActionsJson)
                ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(AllowedActionsJson) ?? Array.Empty<string>(),
            Environment = Environment, DataResidency = DataResidency, OperationalOwner = OperationalOwner,
            ConsequenceTier = ConsequenceTier,
            Enabled = Enabled, UpdatedUtc = new DateTimeOffset(DateTime.SpecifyKind(UpdatedUtc, DateTimeKind.Utc))
        };
    }
}
