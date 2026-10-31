-- AgentPermit — migration: idempotency keys become an agent-private namespace.
--
-- Before: dbo.ActionRequest had a UNIQUE index on IdempotencyKey alone, so two agents could not use the
--         same key and any caller could probe/replay a key regardless of identity.
-- After:  uniqueness is (ExecutingAgentId, IdempotencyKey). The gateway looks keys up scoped to the
--         resolved agent (see ActionRequestService) and files unresolved-identity denials under a
--         gateway-generated key, so an unauthenticated caller can never reserve a legitimate agent's key.
--
-- Idempotent: safe to run more than once. Run against the AgentPermit runtime database.

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- 1. Drop any existing UNIQUE index/constraint whose only key column is IdempotencyKey.
DECLARE @sql nvarchar(max) = N'';

SELECT @sql += CASE
        WHEN i.is_unique_constraint = 1
            THEN N'ALTER TABLE dbo.ActionRequest DROP CONSTRAINT ' + QUOTENAME(i.name) + N';' + CHAR(10)
        ELSE N'DROP INDEX ' + QUOTENAME(i.name) + N' ON dbo.ActionRequest;' + CHAR(10)
    END
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N'dbo.ActionRequest')
  AND i.is_unique = 1
  AND i.is_primary_key = 0
  AND (SELECT COUNT(*) FROM sys.index_columns ic
        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) = 1
  AND EXISTS (SELECT 1 FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = N'IdempotencyKey');

IF LEN(@sql) > 0 EXEC sp_executesql @sql;

-- 2. Create the agent-scoped unique index.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.ActionRequest') AND name = N'UX_ActionRequest_Agent_IdempotencyKey')
BEGIN
    CREATE UNIQUE INDEX UX_ActionRequest_Agent_IdempotencyKey
        ON dbo.ActionRequest (ExecutingAgentId, IdempotencyKey);
END

COMMIT TRANSACTION;
