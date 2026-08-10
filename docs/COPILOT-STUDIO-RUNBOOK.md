# Runbook — governing a Copilot Studio agent with AgentPermit

**Mental model.** AgentPermit is a checkpoint. Wherever your Copilot Studio agent is about to *do*
something real — issue a refund, move money, grant access, release a record — it first calls AgentPermit
and gets back one of three answers: **ALLOW**, **REQUIRE_APPROVAL**, or **DENY**. The agent only proceeds
on ALLOW; on REQUIRE_APPROVAL a named human signs off (in Teams / the console) before it runs; on DENY it
stops. Every decision is written to a signed, tamper-evident receipt. The agent **never touches the target
system directly** — it goes through the checkpoint.

There are two ways to wire it, described at the end:
- **Pattern A — Decide-then-do:** AgentPermit decides; your agent's own flow performs the action.
- **Pattern B — Decide-and-execute (recommended):** AgentPermit decides *and* performs the action via a
  mapped adapter, so the agent never holds target-system credentials.

---

## Part 1 — One-time platform setup (AgentPermit admin, not the maker)
Do these once per environment before makers start.

1. **Import the AgentPermit custom connector** into the Power Platform environment (or share it).
   Definition lives at `power-platform/connector/` (`apiDefinition.swagger.json` + `apiProperties.json`).
   It points at the **APIM front door** (DEV: `https://YOUR_APIM.azure-api.net/agentpermit`)
   and authenticates with **OAuth 2.0 (Entra ID)** against the gateway API (`api://<gateway-app-id>`).
   Two operations:
   - `RequestAction` → `POST /api/actions/request`
   - `GetActionStatus` → `GET /api/actions/{actionRequestId}`
2. **Register the agent in AgentPermit** → **Your agents → Add agent**. Give it a plain name and bind it to
   the agent's **Entra application (client) id**. This is what lets the gateway trust the agent's identity —
   the gateway derives `agentId` from the *authenticated caller*, never from the request body.
3. **Write the rules** → **Rules**. Pick the action(s) from the catalog (grouped by domain), set the
   condition and outcome (Allow / Ask me first / Stop it). Anything with no rule is denied by default.
4. **(Pattern B / to execute)** Map the action's target system (`BANKING_CORE`, `HRIS`, `ERP_FINANCE`, …)
   to the customer's real system under **Settings → Connected systems**.
5. **(Zero-bypass)** Use **App connections (DLP)** to block the agent from reaching the target with raw
   connectors, so it *must* come through AgentPermit. Governance you can bypass isn't governance.

---

## Part 2 — Maker runbook (building the agent in Copilot Studio)

### Step 1 — Add the connection
In Copilot Studio, open your agent. The first time you add an AgentPermit action it will ask you to create a
connection — **sign in with the agent's identity** (OAuth consent to the gateway API). One connection per agent.

### Step 2 — Find the real action point
Locate the exact step in your topic/flow where the agent would otherwise perform the operation (call the
finance API, the HR system, etc.). You will insert the checkpoint **immediately before** it.

### Step 3 — Insert "AgentPermit — Request an action"
Add an action node → choose the AgentPermit connector → **Request an action** (`RequestAction`). Fill inputs:

| Input | What to put |
|---|---|
| `actionId` | The catalog id for what you're governing, e.g. `Payment.Issue`, `FundsTransfer.International`, `AccessGrant.Privileged` (see the **Rules** dropdown, or the action definitions under `config/catalog`) |
| `parameters.*` | The action's fields — e.g. `parameters.amount`, `parameters.currency`, `parameters.vendorCode`. Match the action's schema. |
| `idempotencyKey` | A **caller-owned, unique** id for *this* request, ≥ 8 chars (e.g. a GUID from your topic). A retry with the same key is de-duplicated — never reuse it for a different action. |

You normally do **not** set `agentId` — the gateway derives it from the signed-in identity. (It's accepted
only for trusted connectors.)

Outputs you get back:

| Output | Use |
|---|---|
| `decision` | **ALLOW** / **REQUIRE_APPROVAL** / **DENY** — branch on this |
| `reason` / `reasonCode` | Plain reason to show the user or log |
| `actionRequestId` | Handle for `GetActionStatus` (polling an approval) |
| `correlationId` | For support / tracing |
| `status` | Lifecycle status of the request |

### Step 4 — Branch on `decision`
Add a **Condition** on the `decision` output:

- **ALLOW** → proceed.
  - *Pattern A:* your flow now calls the real target action.
  - *Pattern B:* nothing else to do — AgentPermit already executed it and returned a receipt.
- **REQUIRE_APPROVAL** → tell the user *"This needs a quick sign-off — I've sent it to an approver."* and
  **stop the flow here**. A human approves in Teams / the console; on approval the action runs (Pattern B) or
  you resume via `GetActionStatus` (Pattern A). Do **not** perform the action in this branch.
- **DENY** → tell the user it isn't allowed, showing `reason`. Do **not** perform the action.

### Step 5 — Never call the target outside the ALLOW branch
The whole value is that the real action only happens after an ALLOW (or an approval). Keep the target call
inside that branch only.

### Step 6 — Test in DEV, then publish
- Trigger a **small** request → expect **ALLOW** → action runs → check the receipt in **What's happening**.
- Trigger one over your rule's threshold → expect **REQUIRE_APPROVAL** → approve in **Things needing you** →
  action runs.
- Trigger something a rule blocks → expect **DENY**.
Then publish the agent.

---

## The two patterns, side by side

| | Pattern A — Decide-then-do | Pattern B — Decide-and-execute *(recommended)* |
|---|---|---|
| Who performs the action | Your Copilot flow, after ALLOW | AgentPermit, via a mapped adapter |
| Agent holds target credentials? | Yes (needs DLP to prevent bypass) | **No** — agent never sees them |
| Approval handling | You poll `GetActionStatus` / resume | Runs automatically on approval |
| Evidence | Signed decision receipt | Signed decision **+ execution** receipt |
| Best for | Systems with no AgentPermit adapter yet | Regulated / high-value actions |

---

## Rules & gotchas (non-negotiables)
- **Identity is derived, not declared.** `agentId` comes from the authenticated caller via APIM; a spoofed
  body value is ignored. → the agent must be **registered** (Part 1, step 2) or requests are rejected.
- **Idempotency is required.** `idempotencyKey` ≥ 8 chars, caller-owned, unique per request. The gateway
  never invents one; reuse means "same request," so it's de-duplicated (safe retries).
- **High/critical actions are always binding.** Money movement, privileged access, record release, etc. are
  never auto-allowed during a warm-up — they enforce from the first call.
- **Warm-up rollout (optional).** New agents can start in *Observe → Warn → Enforce* for low/medium actions
  so you see what they'd do before it bites; high/critical stay enforced throughout.
- **APIM in front is part of the contract.** Calls arrive via APIM (validate-JWT, identity injection,
  rate-limit, content validation). Point the connector at APIM, not the gateway directly.

## One-line summary for a maker
*"Before your agent does the real thing, add **AgentPermit → Request an action**, pass the action id +
details + a unique idempotency key, and only continue on **ALLOW**; on **REQUIRE_APPROVAL** tell the user it's
been sent for sign-off; on **DENY** say it's not permitted. Register the agent in AgentPermit first, and let
AgentPermit execute the action (Pattern B) so your agent never holds the keys to the target system."*
