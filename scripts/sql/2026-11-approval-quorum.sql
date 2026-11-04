-- AgentPermit — migration: approvals that need more than one person.
--
-- Before: every approval was decided by a single approver.
-- After:  a rule can require N different approvers (requiredApprovals). dbo.ApprovalRequest records how many
--         are needed, and dbo.ApprovalDecision records each person's decision. A UNIQUE index on
--         (ApprovalRequestId, ApproverKey) means one person can never count twice, even if they click twice at
--         the same moment or on two replicas.
--
-- Idempotent: safe to run more than once. Run BEFORE deploying the build that includes this change (the gateway
-- writes RequiredApprovals on every new approval).

SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- 1. How many approvers an approval needs. Existing rows were single-approver.
IF COL_LENGTH(N'dbo.ApprovalRequest', N'RequiredApprovals') IS NULL
    ALTER TABLE dbo.ApprovalRequest ADD RequiredApprovals INT NOT NULL
        CONSTRAINT DF_ApprovalRequest_RequiredApprovals DEFAULT 1;

-- 2. One row per person per approval.
IF OBJECT_ID(N'dbo.ApprovalDecision', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ApprovalDecision
    (
        ApprovalDecisionId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ApprovalDecision PRIMARY KEY,
        ApprovalRequestId  UNIQUEIDENTIFIER NOT NULL,
        ApproverId         NVARCHAR(400)    NOT NULL,   -- as recorded, e.g. "Fatima Hassan <fatima@bank.ae>"
        ApproverKey        NVARCHAR(320)    NOT NULL,   -- normalised identity (lower-case e-mail)
        Decision           NVARCHAR(16)     NOT NULL,   -- Approved | Rejected
        Comment            NVARCHAR(2000)   NULL,
        DecidedUtc         DATETIME2        NOT NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ApprovalDecision') AND name = N'UX_ApprovalDecision_Approver')
    CREATE UNIQUE INDEX UX_ApprovalDecision_Approver ON dbo.ApprovalDecision (ApprovalRequestId, ApproverKey);

COMMIT TRANSACTION;
