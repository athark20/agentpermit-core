# Contributing to AgentPermit

Thanks for your interest in improving AgentPermit. This project is the open core of a governance
platform for AI agents — contributions to the enforcement gateway, policy engine, connector, SDK,
docs, and action-catalog schema are welcome.

## Ground rules

AgentPermit is a **security** product. Contributions must not weaken the enforcement invariants
listed in `SECURITY.md` (derived identity, caller-owned idempotency, always-binding high/critical
actions, APIM front door, zero-bypass DLP). PRs that relax these to "make a call succeed" will be
declined — fix the integration instead.

## Developer Certificate of Origin (DCO)

We use the [DCO](https://developercertificate.org/) instead of a CLA. Every commit must be signed off:

```
git commit -s -m "Your message"
```

This appends a `Signed-off-by: Your Name <you@example.com>` line, certifying you wrote the change or
have the right to submit it under the project's license (Apache-2.0).

## Development setup

Prerequisites: .NET 10 SDK, Node.js 20+, Docker (for the quickstart).

```bash
# backend
dotnet build backend/src/AgentPermit.Gateway
dotnet test  tests/AgentPermit.Gateway.Tests     # 226 tests should pass

# frontend
cd frontend/admin && npm install && npm run build

# full local stack
docker compose up      # gateway + console + a demo Copilot flow
```

## Pull requests

1. Open an issue first for anything non-trivial, so we can agree on the approach.
2. Keep PRs focused; match the surrounding code's style, comment density, and naming.
3. Add or update tests — the gateway has a strong test suite; keep it green.
4. Update docs when you change behavior.
5. Sign off every commit (DCO, above).

## What belongs here vs. the commercial edition

This repo is the open core. Hosted control-plane / Fleet Ops, the signed-receipt attestation service,
and certified compliance policy packs are part of the commercial edition and are out of scope for PRs
here. See `OPEN-SOURCE.md` for the boundary.

## Security issues

Do **not** file security vulnerabilities as public issues — follow `SECURITY.md`.
