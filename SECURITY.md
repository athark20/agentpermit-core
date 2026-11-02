# Security Policy

AgentPermit is an enforcement and evidence gateway for AI agents. We take security reports
seriously and appreciate responsible disclosure.

## Reporting a vulnerability

**Do not open a public issue for security vulnerabilities.**

Report privately via one of:

- GitHub **Security Advisories** → "Report a vulnerability" on this repository, or
- Email **athar.khan@agentpermit.net** with:
  - a description of the issue and its impact,
  - steps to reproduce (proof-of-concept if available),
  - affected version / commit,
  - any suggested remediation.

We aim to acknowledge within **2 business days** and to provide a remediation timeline within
**10 business days**. Please give us a reasonable window to fix and release before public disclosure
(coordinated disclosure).

## Scope

In scope: the enforcement gateway, policy engine, connector, and SDK in this repository.

Out of scope: issues in your own deployment configuration (e.g. a misconfigured DLP policy,
weak APIM policy, or exposed admin key). Enforcement is only as strong as the deployment — see
the hardening notes in the docs.

## Enforcement invariants (please do not "work around" these)

AgentPermit's security model depends on invariants that must never be weakened:

- **Identity is derived, not declared** — `agentId` comes from the authenticated caller (via APIM),
  never from a request body.
- **Idempotency is caller-owned and agent-scoped** — keys are >= 8 chars, unique per request, looked up
  only after the agent's identity is resolved and only within that agent's namespace; the gateway never
  invents one.
- **High/critical actions are always binding** — they enforce from the first call, regardless of warm-up.
- **APIM front door is part of the contract** — JWT validation and identity injection happen there.
- **The zero-bypass DLP** is what makes governance binding — agents must reach targets only via AgentPermit.

Reports that rely on removing these invariants from a fork are not vulnerabilities in AgentPermit.

## Supported versions

Security fixes are provided for the latest released minor version. Older versions may not receive fixes.
