# Two-person control

Banks and other regulated organisations expect some decisions to need two people: a large payment, a change
to a supplier's bank details, a change to the rules themselves. AgentPermit supports both kinds.

## Approvals that need several people

A rule that asks a person first (`REQUIRE_APPROVAL`) can say how many different people must approve:

```json
{
  "id": "bank-detail-changes",
  "name": "Bank-detail changes need two people",
  "actionId": "Vendor.UpdateBankDetails",
  "decision": "REQUIRE_APPROVAL",
  "approverRoles": ["FinanceController"],
  "requiredApprovals": 2,
  "conditions": []
}
```

In the console's Rules editor this is **How many people must approve it?**

How it behaves:

- The action runs only after `requiredApprovals` **different** people have approved. Until then each approval
  returns `202 ApprovalRecorded`, with `approvalsReceived` and `approvalsRequired`.
- One person counts once. The same person approving again, however their identity is written, gets
  `409 AlreadyApprovedByYou`. In SQL a unique index on `(ApprovalRequestId, ApproverKey)` enforces this, so it
  holds even for two clicks at the same moment on two gateway replicas.
- Each approver must pass the checks on their own: membership of the approver group the rule names, and
  separation of duties (not the person who asked, not the agent's owner).
- One rejection from any eligible approver stops the action.
- Every approval is tied to the frozen payload fingerprint, as for a single approver.
- `GET /api/approvals/pending` shows `requiredApprovals` and `approvedBy` for each request.
- `requiredApprovals` must be between 1 and 5, and only applies to `REQUIRE_APPROVAL` rules. Rules without it
  keep a single approver, and snapshots signed before this field existed verify unchanged.

## Rule changes need a second administrator

Set `Policy:RequireSecondApprover=true`. Then:

1. Publishing rules (the console's **Save & make live**, or `POST /api/policies/publish`) records a proposed
   change and returns `202 PendingApproval` with a `changeId`. Nothing changes yet.
2. A **different** administrator, signed in with their own Entra ID account, reviews it (the Rules page lists
   what each proposal adds, changes and removes) and approves it: `POST /api/policies/changes/{changeId}/approve`.
   Only then is the rule set signed and published. The snapshot's `publisher` names both people.
3. Any administrator, including the proposer, can reject it: `POST /api/policies/changes/{changeId}/reject`.

Safeguards:

- The proposer can't approve their own change (`403 Second administrator needed`).
- The shared admin key is a service, not a person: it can propose (for example rules kept as code) and reject,
  but it can't approve (`403 Sign in to approve`). Dev-open mode can't tell people apart either, so leave the
  setting off there.
- A proposal made against rules that have since changed is marked superseded and can't be published over the
  newer rules (`409`). Propose it again from the current rules.
- Proposals expire after `Policy:ChangeExpiryDays` (default 7).
- `GET /api/policies/changes` lists recent proposals and their status.

**The freeze switch stays one-person.** Freezing and unfreezing every agent (`/api/admin/safety/freeze`,
`/unfreeze`) never waits for a second administrator.

## Settings

| Setting | Default | What it does |
|---|---|---|
| `Policy:RequireSecondApprover` | `false` | Rule changes need a second administrator. Turn it on in production. |
| `Policy:ChangeExpiryDays` | `7` | Days a proposed rule change stays open. |

## Database

Run `scripts/sql/2026-11-approval-quorum.sql` **before** deploying this build. It adds
`ApprovalRequest.RequiredApprovals` and the `ApprovalDecision` table with its unique index. Proposed rule changes
are kept in the existing config store, so they need no migration.

## Not covered yet

- Changes to approver groups and to Roles & access still take one administrator.
- Signed receipts list the approval id; the names of all approvers are in the approval record and
  `ApprovalDecision`, not in the receipt itself.
