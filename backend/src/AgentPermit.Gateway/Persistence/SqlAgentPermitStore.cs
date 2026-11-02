using Dapper;
using Microsoft.Data.SqlClient;

namespace AgentPermit.Gateway.Persistence;

/// <summary>
/// Azure SQL implementation over the hand-authored dbo.* runtime schema
/// (database/001_runtime_schema.sql). Timestamps are stored as DATETIME2 (UTC)
/// and surfaced as UTC DateTimeOffset. Selected via configuration
/// (Persistence:Provider = "SqlServer").
/// </summary>
public sealed class SqlAgentPermitStore(string connectionString) : IAgentPermitStore
{
    private readonly string _connectionString = connectionString;

    // Transient SQL error numbers, including 40613 ("database not currently available"),
    // which is what a serverless Azure SQL database returns while auto-resuming from pause.
    private static readonly HashSet<int> TransientErrorNumbers =
    [
        40613, 40197, 40501, 40540, 49918, 49919, 49920,
        4060, 4221, 10928, 10929, 10053, 10054, 10060, 11001, 233, 64, 20, -2
    ];

    /// <summary>
    /// Opens a connection, retrying transient failures with capped exponential backoff.
    /// A paused serverless database takes tens of seconds to resume on first contact;
    /// this keeps the first request after idle from failing outright.
    /// </summary>
    private SqlConnection Open()
    {
        const int maxAttempts = 6;
        var delayMs = 3000;
        for (var attempt = 1; ; attempt++)
        {
            var c = new SqlConnection(_connectionString);
            try
            {
                c.Open();
                return c;
            }
            catch (SqlException ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                c.Dispose();
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 15000);
            }
            catch
            {
                c.Dispose();
                throw;
            }
        }
    }

    private static bool IsTransient(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => TransientErrorNumbers.Contains(e.Number));

    private static DateTimeOffset Utc(DateTime dt) =>
        new(DateTime.SpecifyKind(dt, DateTimeKind.Utc));

    public async Task<ActionRequestRecord?> FindByIdempotencyKeyAsync(string agentId, string idempotencyKey, CancellationToken ct)
    {
        // Scoped to the agent: the unique index is (ExecutingAgentId, IdempotencyKey) — see
        // scripts/sql/2026-10-idempotency-per-agent.sql for the migration from the key-only index.
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<Row.ActionRequest>(
            "SELECT * FROM dbo.ActionRequest WHERE ExecutingAgentId = @a AND IdempotencyKey = @k",
            new { a = agentId, k = idempotencyKey });
        return row?.Map();
    }

    public async Task<IReadOnlyList<ActionRequestRecord>> ListRecentActionRequestsAsync(int take, CancellationToken ct)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<Row.ActionRequest>(
            "SELECT TOP (@take) * FROM dbo.ActionRequest ORDER BY CreatedUtc DESC",
            new { take = take <= 0 ? 100 : take });
        return rows.Select(r => r.Map()).ToList();
    }

    public async Task<PlatformMetrics> GetMetricsAsync(CancellationToken ct)
    {
        await using var c = Open();
        var all = (await c.QueryAsync<(string Status, int Count)>(
            "SELECT Status, COUNT(*) AS Count FROM dbo.ActionRequest GROUP BY Status")).ToList();
        var today = (await c.QueryAsync<(string Status, int Count)>(
            "SELECT Status, COUNT(*) AS Count FROM dbo.ActionRequest WHERE CreatedUtc >= CAST(SYSUTCDATETIME() AS date) GROUP BY Status")).ToList();
        return MetricsBuilder.Build(all, today);
    }

    public async Task<IReadOnlyList<AgentTrailRow>> GetAgentTrailSummaryAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<AgentTrailRow>(@"
            SELECT ExecutingAgentId AS AgentId,
                   COUNT(*) AS Total,
                   SUM(CASE WHEN Status IN ('SUCCEEDED','EXECUTED','ALLOW') THEN 1 ELSE 0 END) AS Allowed,
                   SUM(CASE WHEN Status IN ('AWAITING_APPROVAL','PENDINGAPPROVAL') THEN 1 ELSE 0 END) AS StepUp,
                   SUM(CASE WHEN Status IN ('DENIED','REJECTED') THEN 1 ELSE 0 END) AS Denied,
                   SUM(CASE WHEN Status = 'FAILED' THEN 1 ELSE 0 END) AS Failed,
                   MAX(CreatedUtc) AS LastSeenUtc
            FROM dbo.ActionRequest
            WHERE CreatedUtc >= @since
            GROUP BY ExecutingAgentId
            ORDER BY Total DESC", new { since = since.UtcDateTime });
        return rows.ToList();
    }

    public async Task<ActionRequestRecord?> GetActionRequestAsync(Guid actionRequestId, CancellationToken ct)
    {
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<Row.ActionRequest>(
            "SELECT * FROM dbo.ActionRequest WHERE ActionRequestId = @id", new { id = actionRequestId });
        return row?.Map();
    }

    public async Task InsertActionRequestAsync(ActionRequestRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.ActionRequest
              (ActionRequestId, CorrelationId, IdempotencyKey, RootAgentId, ExecutingAgentId,
               InitiatingUserId, ActionId, RequestPayloadJson, RequestPayloadHash, Status, CreatedUtc, UpdatedUtc)
            VALUES
              (@ActionRequestId, @CorrelationId, @IdempotencyKey, @RootAgentId, @ExecutingAgentId,
               @InitiatingUserId, @ActionId, @RequestPayloadJson, @RequestPayloadHash, @Status,
               @CreatedUtc, @UpdatedUtc);
            """,
            new
            {
                r.ActionRequestId, r.CorrelationId, r.IdempotencyKey, r.RootAgentId, r.ExecutingAgentId,
                r.InitiatingUserId, r.ActionId, r.RequestPayloadJson, r.RequestPayloadHash, r.Status,
                CreatedUtc = r.CreatedUtc.UtcDateTime, UpdatedUtc = r.UpdatedUtc.UtcDateTime
            });
    }

    public async Task UpdateActionRequestStatusAsync(Guid actionRequestId, string status, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            "UPDATE dbo.ActionRequest SET Status = @status, UpdatedUtc = SYSUTCDATETIME() WHERE ActionRequestId = @id",
            new { id = actionRequestId, status });
    }

    public async Task InsertPolicyDecisionAsync(PolicyDecisionRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.PolicyDecision
              (PolicyDecisionId, ActionRequestId, Decision, PolicyCode, PolicyVersion, DecisionReason,
               EvaluationTraceJson, EvaluatedUtc)
            VALUES
              (@PolicyDecisionId, @ActionRequestId, @Decision, @PolicyCode, @PolicyVersion, @DecisionReason,
               @EvaluationTraceJson, @EvaluatedUtc);
            """,
            new
            {
                r.PolicyDecisionId, r.ActionRequestId, r.Decision, r.PolicyCode, r.PolicyVersion,
                r.DecisionReason, r.EvaluationTraceJson, EvaluatedUtc = r.EvaluatedUtc.UtcDateTime
            });
    }

    public async Task InsertApprovalRequestAsync(ApprovalRequestRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.ApprovalRequest
              (ApprovalRequestId, ActionRequestId, ApprovalType, ApproverId, ApproverRole, PayloadHash,
               Status, RequestedUtc, ExpiresUtc, RespondedUtc, ResponseComment)
            VALUES
              (@ApprovalRequestId, @ActionRequestId, @ApprovalType, @ApproverId, @ApproverRole, @PayloadHash,
               @Status, @RequestedUtc, @ExpiresUtc, @RespondedUtc, @ResponseComment);
            """,
            new
            {
                r.ApprovalRequestId, r.ActionRequestId, r.ApprovalType, r.ApproverId, r.ApproverRole,
                r.PayloadHash, r.Status, RequestedUtc = r.RequestedUtc.UtcDateTime,
                ExpiresUtc = r.ExpiresUtc?.UtcDateTime, RespondedUtc = r.RespondedUtc?.UtcDateTime,
                r.ResponseComment
            });
    }

    public async Task<ApprovalRequestRecord?> GetApprovalAsync(Guid approvalRequestId, CancellationToken ct)
    {
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<Row.Approval>(
            "SELECT * FROM dbo.ApprovalRequest WHERE ApprovalRequestId = @id", new { id = approvalRequestId });
        return row?.Map();
    }

    public async Task<IReadOnlyList<ApprovalRequestRecord>> GetPendingApprovalsAsync(CancellationToken ct)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<Row.Approval>(
            "SELECT * FROM dbo.ApprovalRequest WHERE Status = 'Pending' ORDER BY RequestedUtc ASC");
        return rows.Select(r => r.Map()).ToList();
    }

    public async Task UpdateApprovalAsync(ApprovalRequestRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            UPDATE dbo.ApprovalRequest
               SET Status = @Status, ApproverId = @ApproverId, ApproverRole = @ApproverRole,
                   RespondedUtc = @RespondedUtc, ResponseComment = @ResponseComment
             WHERE ApprovalRequestId = @ApprovalRequestId;
            """,
            new
            {
                r.ApprovalRequestId, r.Status, r.ApproverId, r.ApproverRole,
                RespondedUtc = r.RespondedUtc?.UtcDateTime, r.ResponseComment
            });
    }

    public async Task<PolicyDecisionRecord?> GetLatestPolicyDecisionAsync(Guid actionRequestId, CancellationToken ct)
    {
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<Row.PolicyDecision>(
            "SELECT TOP 1 * FROM dbo.PolicyDecision WHERE ActionRequestId = @id ORDER BY EvaluatedUtc DESC",
            new { id = actionRequestId });
        return row?.Map();
    }

    public async Task<ExecutionRecord?> FindSucceededExecutionAsync(Guid actionRequestId, CancellationToken ct)
    {
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<Row.Execution>(
            "SELECT TOP 1 * FROM dbo.Execution WHERE ActionRequestId = @id AND Status = 'Succeeded' ORDER BY StartedUtc DESC",
            new { id = actionRequestId });
        return row?.Map();
    }

    public async Task<IReadOnlyList<ExecutionRecord>> ListRecentExecutionsAsync(int take, CancellationToken ct)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<Row.Execution>(
            "SELECT TOP (@take) * FROM dbo.Execution ORDER BY StartedUtc DESC",
            new { take = take <= 0 ? 100 : take });
        return rows.Select(r => r.Map()).ToList();
    }

    public async Task InsertExecutionAsync(ExecutionRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.Execution
              (ExecutionId, ActionRequestId, AdapterCode, TargetResourceId, Status, AttemptNumber,
               StartedUtc, CompletedUtc, ResultJson, ErrorCode, ErrorMessage)
            VALUES
              (@ExecutionId, @ActionRequestId, @AdapterCode, @TargetResourceId, @Status, @AttemptNumber,
               @StartedUtc, @CompletedUtc, @ResultJson, @ErrorCode, @ErrorMessage);
            """,
            new
            {
                r.ExecutionId, r.ActionRequestId, r.AdapterCode, r.TargetResourceId, r.Status, r.AttemptNumber,
                StartedUtc = r.StartedUtc.UtcDateTime, CompletedUtc = r.CompletedUtc?.UtcDateTime,
                r.ResultJson, r.ErrorCode, r.ErrorMessage
            });
    }

    public async Task UpdateExecutionAsync(ExecutionRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            UPDATE dbo.Execution
               SET Status = @Status, TargetResourceId = @TargetResourceId, CompletedUtc = @CompletedUtc,
                   ResultJson = @ResultJson, ErrorCode = @ErrorCode, ErrorMessage = @ErrorMessage
             WHERE ExecutionId = @ExecutionId;
            """,
            new
            {
                r.ExecutionId, r.Status, r.TargetResourceId, CompletedUtc = r.CompletedUtc?.UtcDateTime,
                r.ResultJson, r.ErrorCode, r.ErrorMessage
            });
    }

    public async Task<string?> GetLatestReceiptHashAsync(CancellationToken ct)
    {
        await using var c = Open();
        return await c.QueryFirstOrDefaultAsync<string?>(
            "SELECT TOP 1 ReceiptHash FROM dbo.ActionReceipt ORDER BY CreatedUtc DESC, ReceiptNumber DESC");
    }

    public async Task<ActionReceiptRecord?> GetReceiptForRequestAsync(Guid actionRequestId, CancellationToken ct)
    {
        await using var c = Open();
        var row = await c.QueryFirstOrDefaultAsync<Row.Receipt>(
            "SELECT TOP 1 * FROM dbo.ActionReceipt WHERE ActionRequestId = @id ORDER BY CreatedUtc DESC",
            new { id = actionRequestId });
        return row?.Map();
    }

    public async Task<IReadOnlyList<ActionReceiptRecord>> ListRecentReceiptsAsync(int take, CancellationToken ct)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<Row.Receipt>(
            "SELECT TOP (@take) * FROM dbo.ActionReceipt ORDER BY CreatedUtc DESC, ReceiptNumber DESC",
            new { take = take <= 0 ? 50 : take });
        return rows.Select(x => x.Map()).ToList();
    }

    public async Task InsertReceiptAsync(ActionReceiptRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await c.ExecuteAsync(
            """
            INSERT INTO dbo.ActionReceipt
              (ActionReceiptId, ActionRequestId, ReceiptNumber, ReceiptJson, ReceiptHash,
               PreviousReceiptHash, SignatureValue, CreatedUtc)
            VALUES
              (@ActionReceiptId, @ActionRequestId, @ReceiptNumber, @ReceiptJson, @ReceiptHash,
               @PreviousReceiptHash, @SignatureValue, @CreatedUtc);
            """,
            new
            {
                r.ActionReceiptId, r.ActionRequestId, r.ReceiptNumber, r.ReceiptJson, r.ReceiptHash,
                r.PreviousReceiptHash, r.SignatureValue, CreatedUtc = r.CreatedUtc.UtcDateTime
            });
    }

    /// <summary>Row DTOs read DATETIME2 as DateTime, then project to UTC DateTimeOffset records.</summary>
    private static class Row
    {
        public sealed class ActionRequest
        {
            public Guid ActionRequestId { get; set; }
            public Guid CorrelationId { get; set; }
            public string IdempotencyKey { get; set; } = "";
            public string? RootAgentId { get; set; }
            public string ExecutingAgentId { get; set; } = "";
            public string? InitiatingUserId { get; set; }
            public string ActionId { get; set; } = "";
            public string RequestPayloadJson { get; set; } = "";
            public string RequestPayloadHash { get; set; } = "";
            public string Status { get; set; } = "";
            public DateTime CreatedUtc { get; set; }
            public DateTime UpdatedUtc { get; set; }

            public ActionRequestRecord Map() => new()
            {
                ActionRequestId = ActionRequestId, CorrelationId = CorrelationId, IdempotencyKey = IdempotencyKey,
                RootAgentId = RootAgentId, ExecutingAgentId = ExecutingAgentId, InitiatingUserId = InitiatingUserId,
                ActionId = ActionId, RequestPayloadJson = RequestPayloadJson, RequestPayloadHash = RequestPayloadHash,
                Status = Status, CreatedUtc = Utc(CreatedUtc), UpdatedUtc = Utc(UpdatedUtc)
            };
        }

        public sealed class Approval
        {
            public Guid ApprovalRequestId { get; set; }
            public Guid ActionRequestId { get; set; }
            public string ApprovalType { get; set; } = "";
            public string? ApproverId { get; set; }
            public string? ApproverRole { get; set; }
            public string PayloadHash { get; set; } = "";
            public string Status { get; set; } = "";
            public DateTime RequestedUtc { get; set; }
            public DateTime? ExpiresUtc { get; set; }
            public DateTime? RespondedUtc { get; set; }
            public string? ResponseComment { get; set; }

            public ApprovalRequestRecord Map() => new()
            {
                ApprovalRequestId = ApprovalRequestId, ActionRequestId = ActionRequestId, ApprovalType = ApprovalType,
                ApproverId = ApproverId, ApproverRole = ApproverRole, PayloadHash = PayloadHash, Status = Status,
                RequestedUtc = Utc(RequestedUtc),
                ExpiresUtc = ExpiresUtc is null ? null : Utc(ExpiresUtc.Value),
                RespondedUtc = RespondedUtc is null ? null : Utc(RespondedUtc.Value),
                ResponseComment = ResponseComment
            };
        }

        public sealed class PolicyDecision
        {
            public Guid PolicyDecisionId { get; set; }
            public Guid ActionRequestId { get; set; }
            public string Decision { get; set; } = "";
            public string PolicyCode { get; set; } = "";
            public string PolicyVersion { get; set; } = "";
            public string? DecisionReason { get; set; }
            public string? EvaluationTraceJson { get; set; }
            public DateTime EvaluatedUtc { get; set; }

            public PolicyDecisionRecord Map() => new()
            {
                PolicyDecisionId = PolicyDecisionId, ActionRequestId = ActionRequestId, Decision = Decision,
                PolicyCode = PolicyCode, PolicyVersion = PolicyVersion, DecisionReason = DecisionReason,
                EvaluationTraceJson = EvaluationTraceJson, EvaluatedUtc = Utc(EvaluatedUtc)
            };
        }

        public sealed class Execution
        {
            public Guid ExecutionId { get; set; }
            public Guid ActionRequestId { get; set; }
            public string AdapterCode { get; set; } = "";
            public string? TargetResourceId { get; set; }
            public string Status { get; set; } = "";
            public int AttemptNumber { get; set; }
            public DateTime StartedUtc { get; set; }
            public DateTime? CompletedUtc { get; set; }
            public string? ResultJson { get; set; }
            public string? ErrorCode { get; set; }
            public string? ErrorMessage { get; set; }

            public ExecutionRecord Map() => new()
            {
                ExecutionId = ExecutionId, ActionRequestId = ActionRequestId, AdapterCode = AdapterCode,
                TargetResourceId = TargetResourceId, Status = Status, AttemptNumber = AttemptNumber,
                StartedUtc = Utc(StartedUtc), CompletedUtc = CompletedUtc is null ? null : Utc(CompletedUtc.Value),
                ResultJson = ResultJson, ErrorCode = ErrorCode, ErrorMessage = ErrorMessage
            };
        }

        public sealed class Receipt
        {
            public Guid ActionReceiptId { get; set; }
            public Guid ActionRequestId { get; set; }
            public string ReceiptNumber { get; set; } = "";
            public string ReceiptJson { get; set; } = "";
            public string ReceiptHash { get; set; } = "";
            public string? PreviousReceiptHash { get; set; }
            public string? SignatureValue { get; set; }
            public DateTime CreatedUtc { get; set; }

            public ActionReceiptRecord Map() => new()
            {
                ActionReceiptId = ActionReceiptId, ActionRequestId = ActionRequestId, ReceiptNumber = ReceiptNumber,
                ReceiptJson = ReceiptJson, ReceiptHash = ReceiptHash, PreviousReceiptHash = PreviousReceiptHash,
                SignatureValue = SignatureValue, CreatedUtc = Utc(CreatedUtc)
            };
        }
    }
}
