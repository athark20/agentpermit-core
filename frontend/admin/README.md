# AgentPermit Admin (control-plane UI)

Our own admin UI — **not** a Microsoft-hosted app. Admins sign in with **Microsoft Entra ID**;
authorization is enforced server-side by Entra **group / app-role** membership (RBAC-as-Entra-groups).
The UI only calls the gateway's Entra-secured admin API; it never holds downstream credentials.

## What it does
- **Dashboard** — active policy, target-system, agent and receipt counts at a glance.
- **Policies** — inspect the active signed snapshot, list published versions, **publish a new snapshot**
  (validated → hashed → Key Vault-signed → enforced live, no redeploy) and **simulate** a decision.
- **Target Systems** — CRUD the Target System Registry (onboard a system as data; no code deploy).
- **Agents** — the registered agents and the Entra workload identity each is bound to.
- **Evidence** — the signed, hash-chained receipt trail; **re-verify** any receipt (recompute hash +
  validate signature) independently in the browser session.

## Stack
Vite + React + TypeScript + MSAL (`@azure/msal-browser` / `@azure/msal-react`). Zero backend of its own.

## Configure & run
```bash
cp .env.example .env.local     # fill in tenant, SPA client id, API scope, gateway base URL
npm install
npm run dev                    # http://localhost:5173
npm run build                  # static bundle in dist/  (host on Azure Static Web Apps / any static host)
```

All configuration is via `VITE_*` env vars, so one build serves dev and prod by swapping values.
The gateway must allow this app's origin via `AdminAuth:AllowedCorsOrigins`. See `docs/admin-ui.md`.
