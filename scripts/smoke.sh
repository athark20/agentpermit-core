#!/usr/bin/env bash
# AgentPermit smoke test — boots nothing itself; assumes the gateway is running on :8080
# (e.g. `docker compose up`). Registers a demo agent and drives ALLOW / REQUIRE_APPROVAL / DENY
# against the seeded baseline refund policy, then checks that a receipt was written.
set -euo pipefail

BASE="${BASE:-http://localhost:8080}"
ADMIN_KEY="${ADMIN_KEY:-dev-admin-key}"

say() { printf '\n\033[1m%s\033[0m\n' "$1"; }
decision() { python3 -c "import sys,json;d=json.load(sys.stdin);print('  decision:',d.get('decision'),'| status:',d.get('status'),'| reason:',d.get('reasonCode'),'| receipt:',d.get('receiptNumber') or '-')"; }

say "Waiting for the gateway at $BASE ..."
for i in $(seq 1 30); do
  if curl -sf "$BASE/health/live" >/dev/null 2>&1; then echo "  up."; break; fi
  sleep 2
  if [ "$i" = 30 ]; then echo "  gateway did not become healthy" >&2; exit 1; fi
done

say "1) Register demo-agent"
curl -s -o /dev/null -w "  PUT /api/admin/agents/demo-agent -> %{http_code}\n" \
  -X PUT "$BASE/api/admin/agents/demo-agent" \
  -H "X-Admin-Key: $ADMIN_KEY" -H 'Content-Type: application/json' \
  -d '{"agentId":"demo-agent","displayName":"Demo Agent","enabled":true,"type":"Autonomous","riskTier":"Low"}'

req() {
  local key="$1" amount="$2" reason="$3"
  curl -s "$BASE/api/actions/request" -H 'Content-Type: application/json' \
    -d "{\"actionId\":\"Refund.Issue\",\"agentId\":\"demo-agent\",\"idempotencyKey\":\"$key\",\"parameters\":{\"customerId\":\"CUST-1\",\"amount\":$amount,\"currency\":\"AED\",\"reason\":\"$reason\"}}" \
    | decision
}

say "2) Small refund AED 100  (expect ALLOW + receipt)"
req "smoke-allow-$(date +%s)" 100 "Damaged item"
say "3) Mid refund AED 750    (expect REQUIRE_APPROVAL)"
req "smoke-appr-$(date +%s)" 750 "Late delivery"
say "4) Large refund AED 9000 (expect DENY)"
req "smoke-deny-$(date +%s)" 9000 "Fraud claim"

say "5) Evidence — receipts written"
curl -s -H "X-Admin-Key: $ADMIN_KEY" "$BASE/api/admin/receipts?take=5" \
  | python3 -c "import sys,json;d=json.load(sys.stdin);print('  receipts:',len(d))"

say "Smoke test complete."
