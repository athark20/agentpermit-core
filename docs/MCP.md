# AgentPermit MCP (Model Context Protocol) front-door

AgentPermit exposes its governed actions as **MCP tools** over JSON-RPC 2.0, so any MCP-capable
agent — pro-code (Python/TypeScript/C# SDKs, LangChain, custom apps) or low-code (Copilot Studio,
Claude Desktop, IDEs) — can discover and call them. Every `tools/call` runs through the **same
enforcement pipeline** as the REST path: policy bands, agent identity, caller-owned idempotency,
signed receipts, consequence tiers. **MCP is a transport, not an enforcement bypass.**

## Endpoint
- **Front door (use this):** `POST https://YOUR_APIM.azure-api.net/agentpermit/mcp` — behind APIM with Entra `validate-jwt`. Send `Authorization: Bearer <token>`; APIM forwards identity + origin to the gateway. `GET` returns 405 (no server SSE stream). No token → 401.
- Gateway route: `POST /mcp`. JSON-RPC methods: `initialize`, `tools/list`, `tools/call`, `ping` (+ `notifications/initialized`). Stateless (single JSON response per POST).

- **Auth / identity** is taken from the same APIM-forwarded headers as REST (`X-Agent-Client-Id` from
  the caller's token, `X-APIM-Origin`). A pro-code agent with its own app registration is resolved
  from its token automatically; an agent behind the shared connector app identifies itself with
  `agentId` / `agentIdentity` in the tool arguments (validated against the token — a mismatch is denied).
- **Idempotency** is a required tool input (`idempotencyKey`, ≥ 8 chars) — caller-owned, never invented.

## Tools
Each governed action becomes a tool (name = actionId). `tools/list` returns its JSON input schema
(the action's parameters + `idempotencyKey`). Current tools include `Refund.Issue`, `Vendor.Lookup`,
`Invoice.Lookup`, `Invoice.Approve`, `Payment.Issue`, `Vendor.UpdateBankDetails`.

## Example (JSON-RPC)
```jsonc
// initialize
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}
// list tools
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
// call a governed tool
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{
  "name":"Payment.Issue",
  "arguments":{"invoiceNumber":"INV-2026-0003","vendorCode":"V-1003","amount":15750,"currency":"AED","idempotencyKey":"<unique-id>"}}}
```
The `tools/call` result's content carries the governed response — `decision` (ALLOW / REQUIRE_APPROVAL /
DENY), `enforcementMode`, `reasonCode`, and (on execution) the signed receipt.

## Verified live (DEV `week9a`)
Direct on the gateway and **through the APIM front door**:
- `initialize` → protocol `2024-11-05`, server `AgentPermit 1.0.0`; `tools/list` → all 6 governed actions.
- `Vendor.Lookup` → ALLOW + real execution. `Payment.Issue` 15,750 → REQUIRE_APPROVAL (ENFORCED);
  68,000 → DENY. Missing `idempotencyKey` → rejected. Spoofed `agentId` → DENY `AGENT_IDENTITY_MISMATCH`.
- Through APIM: no token → 401; `GET /mcp` → 405.

## APIM wiring (done, DEV)
`POST /mcp` + `GET /mcp` operations on API `agentpermit-gateway`. The API policy sets `X-Agent-Client-Id`
(from the token's `azp`), `X-APIM-Origin`, and the backend, and applies `validate-content` to every path
**except** `/mcp` (JSON-RPC bodies are validated per-tool in the gateway; the action endpoints keep strict
schema validation). See `infrastructure/apim/api-policy.xml`.

## Remaining / optional
- **PROD:** create the same two `/mcp` operations + apply the conditional-`validate-content` policy on the
  PROD APIM if/when MCP is promoted (DEV-only today).
- Optional: Streamable-HTTP SSE + `Mcp-Session-Id` sessions for clients that require server-initiated
  streams (not needed for request/response tool use).
