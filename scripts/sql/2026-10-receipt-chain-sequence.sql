-- AgentPermit — migration: receipt chain gets a sequence number, a kind, and fork-proof constraints.
--
-- Before: the chain head was "the receipt with the latest CreatedUtc". Two gateway replicas appending at the
--         same instant both read the same head and wrote two receipts with the same PreviousReceiptHash — a
--         fork the verifier could not detect. Only executions were receipted.
-- After:  every receipt has a SequenceNumber (genesis 0, +1 each) and a Kind (EXECUTION | DECISION). A UNIQUE
--         index on SequenceNumber and a filtered UNIQUE index on PreviousReceiptHash make a fork impossible:
--         the loser of a race gets a unique-key violation and retries on the new head.
--
-- Idempotent: safe to run more than once. Run BEFORE deploying the build that includes this change.

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- 1. Columns.
IF COL_LENGTH(N'dbo.ActionReceipt', N'SequenceNumber') IS NULL
    ALTER TABLE dbo.ActionReceipt ADD SequenceNumber BIGINT NULL;

IF COL_LENGTH(N'dbo.ActionReceipt', N'Kind') IS NULL
    ALTER TABLE dbo.ActionReceipt ADD Kind NVARCHAR(16) NOT NULL CONSTRAINT DF_ActionReceipt_Kind DEFAULT N'EXECUTION';

-- 2. Back-fill sequence numbers for existing receipts in their historical order (genesis = 0).
-- Dynamic SQL: SQL Server compiles a whole batch before running it, so a statement that names the column added
-- above in the same batch fails with "Invalid column name" unless it is compiled separately.
EXEC sys.sp_executesql N'
;WITH ordered AS (
    SELECT ActionReceiptId,
           ROW_NUMBER() OVER (ORDER BY CreatedUtc ASC, ReceiptNumber ASC) - 1 AS Seq
    FROM dbo.ActionReceipt
    WHERE SequenceNumber IS NULL
)
UPDATE r SET r.SequenceNumber = o.Seq
FROM dbo.ActionReceipt r
JOIN ordered o ON o.ActionReceiptId = r.ActionReceiptId;
';

-- 3. Make the column mandatory now that every row has a value.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ActionReceipt') AND name = N'SequenceNumber' AND is_nullable = 1)
    ALTER TABLE dbo.ActionReceipt ALTER COLUMN SequenceNumber BIGINT NOT NULL;

-- 4. Fork-proof constraints.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ActionReceipt') AND name = N'UX_ActionReceipt_SequenceNumber')
    EXEC sys.sp_executesql N'CREATE UNIQUE INDEX UX_ActionReceipt_SequenceNumber ON dbo.ActionReceipt (SequenceNumber);';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ActionReceipt') AND name = N'UX_ActionReceipt_PreviousReceiptHash')
    CREATE UNIQUE INDEX UX_ActionReceipt_PreviousReceiptHash ON dbo.ActionReceipt (PreviousReceiptHash)
        WHERE PreviousReceiptHash IS NOT NULL;

COMMIT TRANSACTION;

-- Note: if a pre-migration fork already exists (two receipts sharing a PreviousReceiptHash), step 4 fails and the
-- transaction rolls back. Inspect with:
--   SELECT PreviousReceiptHash, COUNT(*) FROM dbo.ActionReceipt GROUP BY PreviousReceiptHash HAVING COUNT(*) > 1;
-- and resolve (keep both rows; the back-filled sequence already orders them) by dropping the filtered index
-- line above for the first run, then re-adding it once the chain is clean.
