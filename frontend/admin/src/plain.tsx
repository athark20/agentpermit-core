// Plain-language helpers — the whole console speaks to non-technical people.
// No engineering terms ever reach the screen; everything is translated here.

export interface Plain { label: string; cls: string; hint?: string }

/** An agent's stage in plain words: Learning -> Testing -> Live (or Paused when off). */
export function agentStage(enabled: boolean | undefined, posture: string | undefined): Plain {
  if (enabled === false) return { label: 'Paused', cls: 'neutral', hint: 'Turned off — this agent is not doing anything right now.' }
  switch ((posture || 'ENFORCE').toUpperCase()) {
    case 'OBSERVE': return { label: 'Learning', cls: 'neutral', hint: 'We’re watching how it behaves. Risky actions are already blocked; smaller ones are just recorded for now.' }
    case 'WARN': return { label: 'Testing', cls: 'approval', hint: 'The rules are being tried out — you get a heads-up, but it isn’t fully blocking yet.' }
    default: return { label: 'Live', cls: 'allow', hint: 'Fully in control — the rules are enforced on every action.' }
  }
}

/** The stage that a posture maps to (for the 3-step progress). */
export const STAGES = ['Learning', 'Testing', 'Live'] as const
export function stageIndex(posture: string | undefined): number {
  switch ((posture || 'ENFORCE').toUpperCase()) {
    case 'OBSERVE': return 0
    case 'WARN': return 1
    default: return 2
  }
}

/** What happened to an action, in plain words. */
export function outcome(decisionOrStatus: string | undefined): Plain {
  switch ((decisionOrStatus || '').toUpperCase()) {
    case 'ALLOW': case 'SUCCEEDED': case 'EXECUTED': return { label: 'Allowed', cls: 'allow' }
    case 'REQUIRE_APPROVAL': case 'AWAITING_APPROVAL': case 'PENDINGAPPROVAL': return { label: 'Needs your sign-off', cls: 'approval' }
    case 'DENY': case 'DENIED': case 'REJECTED': case 'FAILED': return { label: 'Stopped', cls: 'deny' }
    default: return { label: (decisionOrStatus || '—'), cls: 'neutral' }
  }
}

/** Risk in plain words. */
export function riskWord(risk: string | undefined): Plain {
  switch ((risk || '').toLowerCase()) {
    case 'high': case 'critical': case 'financial': return { label: 'High', cls: 'deny' }
    case 'medium': return { label: 'Medium', cls: 'approval' }
    case 'low': return { label: 'Low', cls: 'allow' }
    default: return { label: risk || '—', cls: 'neutral' }
  }
}

export function Tag({ p }: { p: Plain }) { return <span className={`pill ${p.cls}`}>{p.label}</span> }

/** Turn an action id like "Refund.Issue" into "Issue a refund". */
export function prettyAction(actionId: string, displayName?: string): string {
  if (displayName && displayName.trim()) return displayName
  const parts = (actionId || '').split('.')
  const noun = parts[0] ?? actionId
  const verb = parts[1] ?? ''
  if (verb) return `${verb} ${noun}`.replace(/([a-z])([A-Z])/g, '$1 $2')
  return actionId
}

const FIELD: Record<string, string> = { agentId: 'the agent', amount: 'the amount', currency: 'the currency', customerId: 'the customer', reason: 'the reason' }
const OP: Record<string, string> = {
  lessThan: 'is under', lessThanOrEqual: 'is up to', greaterThan: 'is over', greaterThanOrEqual: 'is at least',
  equals: 'is', notEquals: 'is not', in: 'is one of', notIn: 'is not one of', exists: 'is provided',
}
/** "amount is over 1000" style plain condition. */
export function plainCondition(field: string, operator: string, value: unknown): string {
  const f = FIELD[field] ?? field
  const o = OP[operator] ?? operator
  const v = Array.isArray(value) ? value.join(', ') : String(value ?? '')
  return `${f} ${o}${v ? ' ' + v : ''}`.trim()
}

/** Rule decision in plain words. */
export function ruleOutcome(decision: string): string {
  switch ((decision || '').toUpperCase()) {
    case 'ALLOW': return 'it goes ahead automatically'
    case 'DENY': return 'it is stopped'
    case 'REQUIRE_APPROVAL': return 'it waits for your sign-off'
    default: return decision
  }
}

export function timeAgo(iso?: string): string {
  if (!iso) return 'never'
  const s = Math.floor((Date.now() - new Date(iso).getTime()) / 1000)
  if (s < 60) return 'just now'
  if (s < 3600) return `${Math.floor(s / 60)} min ago`
  if (s < 86400) return `${Math.floor(s / 3600)} hr ago`
  return `${Math.floor(s / 86400)} days ago`
}

/** A friendly one-line intro shown at the top of every journey screen. */
export function Intro({ children }: { children: React.ReactNode }) {
  return <p className="page-sub" style={{ marginTop: -8, marginBottom: 18, maxWidth: 760 }}>{children}</p>
}
