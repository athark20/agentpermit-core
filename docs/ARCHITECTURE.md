# AgentPermit — Architecture & Technical Document

**Audience:** engineers, architects, security reviewers.
**Status:** reflects the implemented system (DEV `week9b`).
**Diagram:** see [`architecture-diagram.svg`](architecture-diagram.svg).

---

## 1. What AgentPermit is

AgentPermit is a **customer-hosted, single-tenant governance gateway** for AI agents. It sits **inline** between an agent and the systems it wants to act on. Every high-consequence action an agent requests is evaluated against signed policy, returns a deterministic **ALLOW / REQUIRE_APPROVAL / DENY**, is executed (when permitted) against the real target, and is recorded as a **tamper-evident, RS256-signed receipt**.

The product's defensibility is three properties that observability tools don't have:

- **Enforce** — an inline decision *before* the action runs, not a log after.
- **Prove** — signed, hash-chained evidence for every decision and execution.
- **Secure** — the agent cannot bypass the gateway (identity binding + target-side lockdown + auto-DLP).

### Enforcement contract (invariants — never weakened)
1. **Agent identity is always required** and derived from the authenticated caller — never trusted from the request body alone.
2. **Idempotency keys are caller-owned and required** (≥ 8 chars); the gateway never invents or derives them. Keys are **private to the agent that owns them**: lookups are scoped to the resolved agent, so another agent (or an unauthenticated caller) can neither replay nor reserve them.
3. **APIM schema validation stays on** for action requests.
4. **High/critical-consequence actions are always binding** — never eligible for a warm-up downgrade.
5. **External feeds are off by default** — no implicit external cost or data egress.

---

## 2. Technology

| Layer | Choice |
|---|---|
| Gateway | ASP.NET Core (C#), stateless, horizontally scaled on **Azure Container Apps** |
| Front door | **Azure API Management** (Entra JWT validation, identity injection, rate-limit, schema) |
| Persistence | **Azure SQL** (serverless) via Dapper — control plane + audit + business data |
| Crypto | **Azure Key Vault** — RS256 keys for receipt signing (private key never leaves KV) |
| Durable policy | **Azure Blob** (WORM-capable) for signed policy snapshots |
| Identity | **Microsoft Entra** — API auth, admin RBAC, roles ↔ security groups |
| Console | React + Vite + TypeScript SPA (MSAL sign-in), plain-language |
| Agent transports | Microsoft Copilot custom connector · **MCP** (JSON-RPC) · REST |

---

## 3. Request lifecycle

An action flows through a fixed, ordered pipeline. Any stage can end it.

```
Agent → APIM (authn, identity, schema) → Gateway:
  1. Identity binding      resolve the agent from the token; reject spoofed agentId
  2. Idempotency           caller-owned key, scoped to the resolved agent; replay-safe
  3. Safety gate           kill switch · per-agent quarantine · velocity + deny-velocity breakers
  4. Action & schema       actionId must be catalogued; parameters validated
  5. Policy engine         evaluate signed snapshot → ALLOW / REQUIRE_APPROVAL / DENY
  6. Enforcement gate      apply rollout posture; HIGH/CRITICAL always binding
  7. Approval workflow     (if REQUIRE_APPROVAL) freeze payload hash, await human
  8. Execution (adapter)   run against the target system (SQL / REST / delegated)
  9. Signed receipt        RS256 sign in Key Vault; sequence + hash-chain; DECISION receipts for DENY / ASK too
→ response: decision, status, receipt, result data, correlation id
```

Both front doors — REST `POST /api/actions/request` and MCP `POST /mcp` (`tools/call`) — enter the **same** pipeline. MCP is a transport, not a bypass; identity, idempotency, and policy all apply identically.

---

## 4. Core components

### Policy engine
Policies are **data, not code**: an immutable, versioned **`PolicySnapshot`** (priority-ordered rules; operators `equals/gt/gte/lt/lte/in/…`; default-deny). Rules match on `actionId` + parameter conditions and yield a decision + optional approver role. Changing a threshold = publishing a new snapshot, no redeploy.

**Windowed aggregates (limits, not just thresholds).** A condition may carry an `aggregate`, which makes it read the field *summed / counted / de-duplicated / maximised over a look-back window* instead of from this request alone — including the request being decided:

```jsonc
{ "field": "amount", "operator": "greaterThan", "value": 50000,
  "aggregate": { "function": "sum", "window": "PT24H", "groupBy": ["agentId", "customerId"] } }
```

This is what closes salami-slicing: a per-request limit of 1,000 says nothing about a hundred 999s. `groupBy` scopes the window to what must match this request (`agentId`, `rootAgentId`, `initiatingUserId`, or any request parameter such as a customer or beneficiary), `actions` aggregates across several actions, and `scope` chooses committed exposure (ran or pending — the default) or every request including denials. Values are computed once per distinct spec, before the synchronous engine runs, by `PolicyAggregateProvider`; **a snapshot with no aggregate conditions makes no store call at all**, so the engine keeps its per-request cost for everyone else. Aggregates are validated at publish time (function, ISO-8601 window, group-by and field names, numeric operator) and the computed values are written into the decision trace, so an auditor sees the number the verdict rested on. Failure is closed: a store error fails the request rather than letting a limit rule evaluate against a missing value. Under true concurrency two requests can read the same prior total, so the velocity breakers remain the hard backstop.

### Policy publisher (signed, live-swappable)
`POST /api/policies/publish` validates → hashes → **RS256-signs in Key Vault** → writes an immutable versioned snapshot to Blob → atomically swaps the runtime-active snapshot. On startup the gateway loads and **re-verifies the signature**; a tampered snapshot is refused. The plain-language **Rules** editor in the console produces these snapshots.

### Enforcement gate & consequence tiers
Each action/target carries a **consequence tier** (LOW/MEDIUM/HIGH/CRITICAL), classified **at import time** (danger is a property of the target, not learned by watching). During warm-up (rollout posture OBSERVE/WARN) a low/medium block may be recorded but not applied — **but HIGH/CRITICAL is always enforced from day one**. Unset posture = ENFORCE, unset tier = HIGH (secure by default).

### Identity binding
APIM forwards the authenticated app id (`azp`) as `X-Agent-Client-Id`; the gateway resolves the registered agent from it. A body `agentId` that disagrees → **DENY `AGENT_IDENTITY_MISMATCH`**. A shared connector may assert a specific agent's badge (`agentIdentity`), trusted only when the caller is a registered connector app. Direct-to-backend calls lacking the APIM-origin secret → **DENY `NOT_FROM_APIM`**.

### Idempotency & state machine
The caller supplies a unique `idempotencyKey` (≥ 8 chars). Same key + same payload → replay of the prior result (no double execution). Same key + **changed** payload → **DENY `IDEMPOTENCY_PAYLOAD_MISMATCH`**. Keys are an **agent-private namespace**: the lookup runs *after* identity binding and is scoped to the resolved agent (`UNIQUE (ExecutingAgentId, IdempotencyKey)`), so agent B presenting agent A's key gets its own fresh request, never A's replayed receipt, and an unauthenticated probe cannot reserve a key that a real agent will use later — unresolved-identity denials are filed under a gateway-generated key with the supplied key kept in the decision trace. Requests move through a validated canonical state machine (RECEIVED → … → SUCCEEDED/FAILED/EXPIRED/CANCELLED).

### Execution & adapters
The gateway is the **only** holder of downstream credentials. `IActionAdapter` implementations run the action against a target resolved from the registry: a real **SQL adapter**, a production **REST adapter**, and a **delegated adapter** (routes through Power Automate / Logic Apps to reach 1400+ Microsoft connectors). Target-side business rules (e.g., "vendor on hold") surface as a clean `ALLOW / status: FAILED` — defense in depth — not a crash.

### Signed receipts (evidence)
Every **verdict** of a resolved agent produces an `ActionReceipt`, not only every execution: an **EXECUTION** receipt when the gateway ran the action, a **DECISION** receipt for a DENY or REQUIRE_APPROVAL (with the reason code inside the signed content). What an agent *tried* is in the same tamper-evident chain as what it did — the evidence every 2026 incident post-mortem was missing. (`Receipts:SignDecisions=false` turns decision receipts off if signing cost matters more.) Unresolved-identity probes are audited but not signed, so an anonymous caller cannot grow the chain.

Each receipt carries a canonical content hash, an **RS256 signature created in Key Vault** (private key never exported), the signing key id, `previousReceiptHash` and a **`sequenceNumber`** (genesis 0, strictly +1). Chaining is **serialised**: a single-writer gate per replica, and across replicas `UNIQUE (SequenceNumber)` plus a filtered `UNIQUE (PreviousReceiptHash)` in SQL — a writer that loses the race gets a conflict, re-reads the head and retries, so two replicas can never fork the chain. The verify endpoint recomputes the canonical hash, verifies the signature, and checks **continuity** (the receipt's predecessor at `sequence − 1` hashes to its `previousReceiptHash`). The shape is aligned with the IETF SCITT AI-agent action-receipt draft (sequence + previous-hash chain, verdict tokens) so receipts can later be registered with a transparency service.

### Approvals (human-in-the-loop)
A REQUIRE_APPROVAL decision opens an approval with a **frozen payload hash**. Approve → re-verify hash → execute (dup-guarded) → receipt. Reject → no execution. Tampered payload at approval time → **409 `PayloadChanged`**. Sign-off happens in the console ("Things needing you") and/or a Teams card.

**Who may decide.** The approver's identity is derived from the authenticated caller. Two checks run before anything else: (1) **approver role** — when the matched rule names an approver role (`approverRoles: ["FinanceController"]`), the approver must be a member of the **approver group** of that name (`/api/admin/approver-groups`); with `Approval:RequireNamedApproverGroups=true` a missing group fails closed (**403 `ApproverRoleRequired`**), otherwise it falls back to the generic Approver persona with a warning. (2) **Separation of duties** — the approver may not be the request's `initiatingUserId` nor the agent's registered owner (**403 `SeparationOfDuties`**). The approval summary and the Teams payload carry the **complete frozen payload**, so the approver sees every field they are signing (a bank-detail change shows the account, not just an amount).

### Always-on safety net
Independent of policy and warm-up, checked as soon as the agent is known: a **global kill switch** (freeze → `SYSTEM_FROZEN`), a **per-agent quarantine** (`AGENT_QUARANTINED`, persisted, lifted by an admin), a **per-agent execution-velocity breaker** (`CIRCUIT_BREAKER`, too many ALLOWs in a window) and a **deny-velocity breaker** (too many denials in a window → automatic quarantine). The deny-velocity breaker exists because the 2026 incident record shows agents treating a refusal as a puzzle to solve: identity mismatches, unknown actions, schema failures, idempotency misuse and policy denials for a *resolved* agent all count; denials of an unresolved identity do not, so an unauthenticated probe can never quarantine a real agent by claiming its name. Safety-layer denials are stored as `GATEWAY_SAFETY` observations and are excluded from rollout evidence, so rejected traffic can't earn an agent a promotion.

---

## 5. Connector-governance pipeline (onboarding at scale)

Because you can't hand-inspect thousands of agents, governance scales with the **shared targets/connectors**, classified once:

1. **Discover** — design-time (Dataverse connection references, solution/bot components) + runtime observation feeds (Purview audit, Power Automate run history) → an append-only **Observation Store**. The delta (observed − governed) is the enforcement work-list.
2. **Risk-classify** — a signed, explainable engine over a versioned **Connector Risk Catalog** + operation semantics + observed data classification. Unknown → HIGH (fail closed).
3. **Auto-generate** — a zero-bypass **Power Platform DLP** policy (HIGH/unknown → Blocked; MEDIUM → Business+review; governed connector → allowed), plus the signed action-policy and Entra bindings.
4. **Roll out** — staged Observe → Warn → Enforce per agent/env, with drift detection and audited, time-boxed exceptions.

The **real wall** against bypass is **target-side identity lockdown** (the target accepts only the gateway's identity), backstopped by DLP.

---

## 6. Control plane

- **Admin console** — a plain-language React SPA (Entra/MSAL sign-in) for non-technical governance owners: your agents, Rules (form-filling policy editor), approvals, roles & access, and signed evidence. No jargon, no JSON in the open.
- **Roles → Entra** — four fixed roles (Admin, Approver, Agent owner, Viewer) managed in-app and pushed **one-way** into Entra security groups via Microsoft Graph (AgentPermit is the source of truth; membership is never read back as authority). Gated on a one-time admin consent.
- **Config store** — a generic `ConfigEntity` table (type/id/json) backs environments, retention, roles, safety state, observation cursors — no schema churn per feature.

---

## 7. Transports (front doors)

| Transport | Endpoint | Use |
|---|---|---|
| Microsoft Copilot connector | via APIM → `/api/actions/request` | Low-code Copilot Studio agents (drop-in) |
| REST | `POST /api/actions/request` | Pro-code agents (any language) |
| **MCP** | `POST /mcp` (JSON-RPC: `initialize`, `tools/list`, `tools/call`) | MCP hosts / IDEs / Claude / custom — governed actions exposed as MCP tools |

All three resolve identity from the same APIM-forwarded headers and run the same enforcement pipeline. See [`MCP.md`](MCP.md).

---

## 8. Deployment & environments

- **Customer-hosted, single-tenant** in the customer's own Azure — data residency by design. Reference DEV/PROD run in **UAE North**.
- Gateway on Container Apps (min-scale ≥1; serverless SQL auto-pauses in dev). IaC in `infrastructure/bicep` (dev/prod profiles: prod = zone-redundant, private Key Vault, WORM storage, StandardV2 APIM).
- Managed identity for ACR pull, Key Vault (Secrets + Crypto User), and Blob.
- Config/secrets via Container App env + Key Vault references; nothing sensitive in the image.

## 9. Threat model highlights

| Threat | Control |
|---|---|
| Agent spoofs another agent's permissions | Identity bound to authenticated token; body `agentId` must match |
| Agent calls the target directly, around the gateway | Target-side identity lockdown + auto-DLP; `NOT_FROM_APIM` on direct hits |
| Replay / double execution | Caller-owned idempotency + dup-guarded execution |
| Payload swapped between request and approval | Frozen payload hash; `PayloadChanged` → 409 |
| Wrong person approves (any Approver signs a Finance Controller decision; initiator approves own request; owner approves own agent) | Approver groups matched to the rule's role; separation-of-duties checks; full payload shown to the approver |
| Tampered evidence | RS256 signatures + hash chain; offline verifiable |
| Deleted or forked evidence (a receipt removed between two others; two replicas appending to the same head) | Sequence numbers in the signed content; unique sequence + previous-hash constraints; verifier checks continuity |
| Attempts not evidenced (only successes were signed) | DECISION receipts for DENY / REQUIRE_APPROVAL of a resolved agent |
| Tampered policy | Signed snapshots, re-verified at load; tampered = refused |
| Runaway agent / emergency | Velocity breaker + global kill switch, ahead of policy |
| Agent probes a block (retries around a refusal, tries other actions/parameters, mis-asserts identity) | Deny-velocity breaker → automatic per-agent quarantine until an admin lifts it; unresolved identities never count |
| Salami-slicing under a per-request limit (100 × 999 instead of one 99,900); fan-out across many counterparties | Windowed aggregate conditions (sum / count / distinct / max per agent, user or counterparty), enforced on the request that would breach |
