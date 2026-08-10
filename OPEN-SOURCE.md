# AgentPermit — Open-Core Plan

AgentPermit is released under an **open-core** model: the enforcement engine is open and
auditable (a governance product you can't inspect isn't trustworthy); the hard-to-self-run and
compliance-grade parts remain commercial.

## What is open (Apache-2.0) vs commercial

| Open source — `agentpermit-core` (Apache-2.0) | Commercial — `agentpermit-cloud` (BSL 1.1) |
|---|---|
| Enforcement gateway + policy engine (`backend/src/AgentPermit.Gateway` core) | Hosted control plane / multi-client **Fleet Ops** |
| Copilot Studio custom connector + schema (`power-platform/connector`) | **Signed-receipt attestation** service (AgentPermit as the notary) |
| Action-catalog **schema** + starter packs (`config/catalog` samples) | **Certified** CBUAE / PDPL / healthcare policy packs, kept current |
| Admin console (base) | Advanced monitoring/alerting, connector-governance pipeline |
| Self-host docs + `docker compose` quickstart | Support, SLA, indemnity, onboarding |

**Monetization is NOT a code flag.** Because the gateway is open, any licence check inside it can be
removed from a fork. Revenue comes from things that can't be forked away: the hosted control plane,
the signing/attestation authority (our key, not in the repo), certified compliance content, and support.
The open build runs fully, self-hosted, unlicensed.

## Pre-publication checklist (do ALL before the core repo goes public)

Publishing is irreversible. This repo stays **private** as the monorepo; a **new, clean** public repo
`agentpermit-core` is created from a curated export.

- [ ] **Parameterize real identifiers** — replace hardcoded values with placeholders / config:
      tenant id `<your-tenant-id>`, connector client id `<your-connector-client-id>`, Dataverse org `orgXXXXXXXX.crmN.dynamics.com`,
      SQL login `sqladmin`. Occurs in: `infrastructure/apim/api-policy*.xml`, `power-platform/connector/*`,
      `power-platform/approval-flow/.build/build.py`, `backend/.../appsettings.json`, tests, `outputs/`.
- [ ] **Exclude internal folders** from the public export: `outputs/`, `deliverables/`, `brand/` (keep the
      logo separately if desired), `infrastructure/bicep/*.bicepparam` (real infra), any `.build/` artifacts.
- [ ] **Run a history secret-scan** on the public export (`gitleaks detect`), starting from a fresh history
      (squash/`git init` the curated tree — do NOT carry this repo's full history into public).
- [ ] **Rotate** the connector OAuth app secret and any app registrations referenced, since the app/tenant
      ids become public (ids aren't secret, but rotate the paired secrets as hygiene).
- [ ] Add the OSS furniture: `LICENSE`, `NOTICE`, `SECURITY.md`, `CONTRIBUTING.md` (DCO), `CODE_OF_CONDUCT.md`,
      issue/PR templates, a public CI that builds + runs the 226 tests, and an OSS `README` with a
      5-minute `docker compose up` quickstart.
- [ ] Keep the **AgentPermit name + mark trademarked** — Apache code, registered trademark, so forks can't
      use the brand.
- [ ] Legal: confirm the copyright entity name in `LICENSE`/`NOTICE`.

## Why open source this (benefits)

1. **Trust & auditability** — the biggest lever for regulated buyers; "audit the wall yourself" beats a
   black box (vs Microsoft Purview / Entra).
2. **Distribution** — devs adopt the free connector + SDK bottom-up; sell the compliance/managed layer top-down.
3. **Standard-setting** — the action-catalog + receipt format become the way to govern Copilot agents.
4. **Community connectors & policy packs.**
5. **Credibility & hiring.**
6. **Faster procurement** — self-serve OSS bypasses new-vendor friction.

## Risks & mitigations

- **Cannibalization** → draw the free/paid line so a regulated customer still needs certified packs + attestation + support.
- **Fork-and-resell** → BSL on the cloud plane; the signing authority is ours.
- **Support/security burden** → `SECURITY.md` + a CVE process; disciplined scope.
