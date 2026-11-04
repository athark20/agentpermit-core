import { useEffect, useMemo, useState } from 'react'
import { api, ApiError, reasonOf } from '../api'
import { useAsync } from '../hooks'
import { useAccess } from '../access'
import { useToast } from '../components/Toast'
import { PageHeader, Loading, Drawer } from '../components/ui'
import { Intro, prettyAction, plainCondition, timeAgo } from '../plain'
import type { BusinessAction, Agent } from '../types'

// ---- plain vocabulary -------------------------------------------------------
// The three outcomes, in everyday words. These map to the enforcement decisions
// the gateway already understands (ALLOW / REQUIRE_APPROVAL / DENY).
const OUTCOMES = [
  { v: 'ALLOW', label: 'Allow it', help: 'The agent goes ahead on its own.', cls: 'allow' },
  { v: 'REQUIRE_APPROVAL', label: 'Ask me first', help: 'It pauses and waits for a person to approve.', cls: 'approval' },
  { v: 'DENY', label: 'Stop it', help: 'The action is blocked completely.', cls: 'deny' },
]
// Friendly "when" checks. Each maps to an operator the engine understands.
const CHECKS = [
  { v: 'greaterThan', label: 'is over' },
  { v: 'greaterThanOrEqual', label: 'is at least' },
  { v: 'lessThan', label: 'is under' },
  { v: 'lessThanOrEqual', label: 'is up to' },
  { v: 'equals', label: 'is exactly' },
  { v: 'notEquals', label: 'is not' },
  { v: 'in', label: 'is one of' },
  { v: 'notIn', label: 'is not one of' },
  { v: 'exists', label: 'is filled in' },
]
// Common things a rule's conditions look at, in plain words (with a free-text escape hatch).
// (Which agents a rule applies to is a separate "attach to" control, not a generic condition.)
const THINGS = [
  { v: 'amount', label: 'Amount' },
  { v: 'currency', label: 'Currency' },
  { v: 'customerId', label: 'Customer' },
  { v: 'reason', label: 'Reason' },
]
const checksFor = (_field: string) => CHECKS
const defaultCheck = (_field: string) => 'greaterThan'
// Who can sign off when a rule says "Ask me first" — the four AgentPermit roles.
const APPROVERS = ['Admin', 'Approver', 'Agent owner']

// Plain-language section headers for the action picker, in a sensible order.
export const DOMAIN_LABEL: Record<string, string> = {
  Finance: 'Finance & accounting',
  Banking: 'Banking',
  Treasury: 'Treasury',
  CustomerService: 'Customer service',
  Procurement: 'Procurement & vendors',
  HR: 'HR & payroll',
  IdentityAccess: 'Identity & access',
  Sales: 'Sales & CRM',
  ITSM: 'IT service management',
  CloudOps: 'Cloud & DevOps',
  DataOps: 'Data operations',
  Communications: 'Communications',
  Healthcare: 'Healthcare',
  Government: 'Government',
}
export const DOMAIN_ORDER = Object.keys(DOMAIN_LABEL)

/** A windowed limit on a condition (e.g. the total over 24 hours). The editor keeps it as it is. */
interface Aggregate { function: string; window: string; groupBy?: string[]; actions?: string[]; scope?: string }
interface Cond { field: string; check: string; value: string; aggregate?: Aggregate | null }
interface Rule {
  id: string; name: string; actionId: string; decision: string; approver: string
  /** How many different people must approve ("Ask me first" only). */
  requiredApprovals: number
  conditions: Cond[]
  description?: string | null; reason?: string | null
}
interface RawCond { field: string; operator: string; value: unknown; aggregate?: Aggregate | null }
interface RawRule {
  id: string; name: string; priority: number; actionId?: string; decision: string; approverRoles?: string[]
  requiredApprovals?: number | null; description?: string | null; reason?: string | null; conditions?: RawCond[]
}
interface Active { policyCode: string; version: string; hash?: string; defaultDecision: string; requireSecondApprover?: boolean; rules: RawRule[] }
interface PolicyChange {
  changeId: string; status: string; policyCode: string; version: string; ruleCount: number
  basedOnVersion?: string; basedOnHash?: string
  proposedBy: string; proposedByEmail?: string; proposedAtUtc: string; expiresAtUtc: string
  decidedBy?: string; snapshot: { rules: RawRule[] }
}
interface Changes { requireSecondApprover: boolean; changes: PolicyChange[] }

// Which agents a rule is scoped to (via an "Agent is / is-one-of" check). Empty = applies to every agent.
function agentsForRule(r: Rule): string[] {
  const out: string[] = []
  for (const c of r.conditions) {
    if (c.field !== 'agentId') continue
    if (c.check === 'equals' && c.value.trim()) out.push(c.value.trim())
    else if (c.check === 'in') c.value.split(',').map((s) => s.trim()).filter(Boolean).forEach((v) => out.push(v))
  }
  return out
}
function toStr(v: unknown): string { return Array.isArray(v) ? v.join(', ') : v == null ? '' : String(v) }
function coerce(v: string): string | number { return /^-?\d+(\.\d+)?$/.test(v.trim()) ? Number(v) : v }
function condValue(c: Cond): unknown {
  if (c.check === 'exists') return true
  if (c.check === 'in' || c.check === 'notIn') return c.value.split(',').map((s) => coerce(s.trim()))
  return coerce(c.value)
}
function nextVersion(v: string): string {
  const m = v.match(/^(\d+)\.(\d+)\.(\d+)$/); if (m) return `${m[1]}.${m[2]}.${Number(m[3]) + 1}`
  const m2 = v.match(/^(\d+)\.(\d+)$/); if (m2) return `${m2[1]}.${Number(m2[2]) + 1}.0`
  return v + '.1'
}
function fromRaw(r: RawRule): Rule {
  return {
    id: r.id, name: r.name, actionId: r.actionId ?? '', decision: r.decision,
    approver: (r.approverRoles ?? [])[0] ?? 'Approver',
    requiredApprovals: r.requiredApprovals && r.requiredApprovals > 1 ? r.requiredApprovals : 1,
    description: r.description, reason: r.reason,
    conditions: (r.conditions ?? []).map((c) => ({ field: c.field, check: c.operator, value: toStr(c.value), aggregate: c.aggregate })),
  }
}

const AGG_FN: Record<string, string> = { sum: 'total', count: 'number of requests', distinct: 'number of different values', max: 'largest' }
/** "total over the last 24 hours, per agent and customer" — a windowed limit in plain words. */
function plainAggregate(a: Aggregate): string {
  const m = a.window.match(/^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?)?$/)
  const days = Number(m?.[1] ?? 0), hours = Number(m?.[2] ?? 0), mins = Number(m?.[3] ?? 0)
  const span = days ? `${days} day${days > 1 ? 's' : ''}` : hours ? `${hours} hour${hours > 1 ? 's' : ''}` : mins ? `${mins} minutes` : a.window
  const per = (a.groupBy ?? []).map((g) => g.replace(/Id$/, '').replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()).join(' and ')
  return `${AGG_FN[a.function] ?? a.function} over the last ${span}${per ? `, per ${per}` : ''}`
}

/** The fields that decide what a rule does, for comparing two versions of it. */
function ruleKey(r: RawRule): string {
  return JSON.stringify([r.name, r.actionId ?? '', r.decision, r.approverRoles ?? [], r.requiredApprovals ?? 1,
    (r.conditions ?? []).map((c) => [c.field, c.operator, c.value, c.aggregate ?? null])])
}
/** What a proposed rule set changes compared with the live one, by rule. */
function diffRules(live: RawRule[], proposed: RawRule[]) {
  const before = new Map(live.map((r) => [r.id, r]))
  const after = new Map(proposed.map((r) => [r.id, r]))
  return {
    added: proposed.filter((r) => !before.has(r.id)).map((r) => r.name || r.id),
    removed: live.filter((r) => !after.has(r.id)).map((r) => r.name || r.id),
    changed: proposed.filter((r) => before.has(r.id) && ruleKey(before.get(r.id)!) !== ruleKey(r)).map((r) => r.name || r.id),
  }
}

export function Rules() {
  const active = useAsync<Active>(() => api('/api/policies/active'))
  const changes = useAsync<Changes>(() => api('/api/policies/changes'))
  const catalog = useAsync<BusinessAction[]>(() => api('/api/admin/actions'))
  const agentList = useAsync<Agent[]>(() => api('/api/admin/agents'))
  const [rules, setRules] = useState<Rule[]>([])
  const [meta, setMeta] = useState({ policyCode: '', version: '', defaultDecision: 'DENY' })
  const [editing, setEditing] = useState<string>()
  const [dirty, setDirty] = useState(false)
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  const access = useAccess()
  const canEdit = access.canEdit

  useEffect(() => {
    if (!active.data) return
    setRules(active.data.rules.map(fromRaw))
    setMeta({ policyCode: active.data.policyCode, version: active.data.version, defaultDecision: active.data.defaultDecision })
    setDirty(false)
  }, [active.data])

  const actionLabel = useMemo(() => {
    const m: Record<string, string> = {}
    for (const a of catalog.data ?? []) m[a.actionId] = prettyAction(a.actionId, a.displayName)
    return (id: string) => id ? (m[id] ?? prettyAction(id)) : 'any action'
  }, [catalog.data])

  // agentId -> friendly name, so an "Agent is …" rule reads as a name, not a slug.
  const agentLabel = useMemo(() => {
    const m: Record<string, string> = {}
    for (const a of agentList.data ?? []) m[a.agentId] = a.displayName || a.agentId
    return (id: string) => m[id] ?? id
  }, [agentList.data])

  // Group the action catalog by domain for the picker, in a sensible order, actions sorted within.
  const actionGroups = useMemo(() => {
    const by = new Map<string, BusinessAction[]>()
    for (const a of catalog.data ?? []) {
      const d = a.domain || 'Other'
      if (!by.has(d)) by.set(d, [])
      by.get(d)!.push(a)
    }
    return [...by.entries()]
      .sort((x, y) => (DOMAIN_ORDER.indexOf(x[0]) + 1 || 99) - (DOMAIN_ORDER.indexOf(y[0]) + 1 || 99))
      .map(([d, list]) => [d, [...list].sort((p, q) =>
        prettyAction(p.actionId, p.displayName).localeCompare(prettyAction(q.actionId, q.displayName)))] as const)
  }, [catalog.data])

  function change(fn: (rs: Rule[]) => Rule[]) { setRules(fn); setDirty(true) }
  const patch = (id: string, p: Partial<Rule>) => change((rs) => rs.map((r) => r.id === id ? { ...r, ...p } : r))
  const patchCond = (id: string, i: number, p: Partial<Cond>) =>
    change((rs) => rs.map((r) => r.id === id ? { ...r, conditions: r.conditions.map((c, j) => j === i ? { ...c, ...p } : c) } : r))

  // Attach a rule to a set of agents (empty = every agent). Stored as one agentId condition (equals / is-one-of).
  const setRuleAgents = (id: string, ids: string[]) =>
    change((rs) => rs.map((r) => {
      if (r.id !== id) return r
      const others = r.conditions.filter((c) => c.field !== 'agentId')
      const agentCond: Cond[] = ids.length
        ? [{ field: 'agentId', check: ids.length === 1 ? 'equals' : 'in', value: ids.join(',') }]
        : []
      return { ...r, conditions: [...agentCond, ...others] }
    }))

  function addRule(agentId?: string) {
    const id = `rule-${Date.now().toString(36)}`
    const conditions: Cond[] = agentId ? [{ field: 'agentId', check: 'equals', value: agentId }] : []
    change((rs) => [...rs, { id, name: '', actionId: '', decision: 'REQUIRE_APPROVAL', approver: 'Approver', requiredApprovals: 1, conditions }])
    setEditing(id)
  }
  function removeRule(id: string) { change((rs) => rs.filter((r) => r.id !== id)); if (editing === id) setEditing(undefined) }

  function buildSnapshot(nextVer: string) {
    return {
      policyCode: meta.policyCode, version: nextVer, defaultDecision: meta.defaultDecision,
      rules: rules.map((r, i) => ({
        id: r.id, name: r.name.trim() || `Rule ${i + 1}`, priority: (i + 1) * 10,
        actionId: r.actionId, decision: r.decision,
        ...(r.decision === 'REQUIRE_APPROVAL' && r.approver ? { approverRoles: [r.approver] } : {}),
        ...(r.decision === 'REQUIRE_APPROVAL' && r.requiredApprovals > 1 ? { requiredApprovals: r.requiredApprovals } : {}),
        // Keep what the editor doesn't show, so saving never quietly drops part of a rule.
        ...(r.description ? { description: r.description } : {}),
        ...(r.reason ? { reason: r.reason } : {}),
        conditions: r.conditions
          .filter((c) => c.field.trim() && (c.check === 'exists' || c.value.trim()))
          .map((c) => ({ field: c.field.trim(), operator: c.check, value: condValue(c), ...(c.aggregate ? { aggregate: c.aggregate } : {}) })),
      })),
    }
  }

  async function save() {
    setBusy(true)
    const nv = nextVersion(meta.version)
    try {
      const res = await api<{ version: string; signed?: boolean; status?: string; message?: string }>('/api/policies/publish', { method: 'POST', body: JSON.stringify(buildSnapshot(nv)) })
      if (res.status === 'PendingApproval') {
        // Two-person rule: nothing is live until a different administrator approves it.
        toast.success('Sent for approval', res.message ?? 'A different administrator must approve these rules before they go live.')
        setDirty(false); active.reload(); changes.reload()
      } else {
        toast.success('Rules saved', `Live now — version ${res.version}. No restart needed.`)
        setDirty(false); active.reload(); changes.reload()
      }
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not save rules', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    } finally { setBusy(false) }
  }

  async function decideChange(c: PolicyChange, approve: boolean) {
    setBusy(true)
    try {
      await api(`/api/policies/changes/${c.changeId}/${approve ? 'approve' : 'reject'}`, { method: 'POST', body: JSON.stringify({}) })
      toast.success(approve ? 'Rules approved' : 'Change rejected',
        approve ? `Live now — version ${c.version}.` : `Version ${c.version} will not go live.`)
      active.reload(); changes.reload()
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not record your decision', reasonOf(e), { code: err?.code, traceId: err?.traceId })
      changes.reload()
    } finally { setBusy(false) }
  }

  // Plain one-line summary of a rule's conditions (the agents it's attached to are shown separately).
  const ruleSentence = (r: Rule) => {
    const conds = r.conditions.filter((c) => c.field !== 'agentId')
    return conds.length ? conds.map((c) => plainCondition(c.field, c.check, c.check === 'exists' ? '' : c.value)).join(' and ') : 'every time'
  }

  // The rule editor form — shown inside the drawer when a rule card is clicked.
  const renderEditor = (r: Rule) => {
    const attached = agentsForRule(r)
    return (
      <div className="rule-form">
        <label>Give this rule a short name
          <input value={r.name} onChange={(e) => patch(r.id, { name: e.target.value })} placeholder="e.g. Big refunds need sign-off" />
        </label>

        <label>Which agent action does this watch?
          <select value={r.actionId} onChange={(e) => patch(r.id, { actionId: e.target.value })}>
            <option value="">Any action</option>
            {actionGroups.map(([domain, list]) => (
              <optgroup key={domain} label={`${DOMAIN_LABEL[domain] ?? domain} (${list.length})`}>
                {list.map((a) => <option key={a.actionId} value={a.actionId}>{prettyAction(a.actionId, a.displayName)}</option>)}
              </optgroup>
            ))}
          </select>
        </label>

        <h3 className="sec">Attach to which agents?</h3>
        <p className="muted small" style={{ margin: '-4px 0 10px' }}>Leave it on <b>All agents</b> to apply everywhere, or pick specific agents.</p>
        <div className="attach-chips">
          <button type="button" className={`chip-toggle ${attached.length === 0 ? 'active' : ''}`} onClick={() => setRuleAgents(r.id, [])}>All agents</button>
          {(agentList.data ?? []).map((a) => {
            const on = attached.includes(a.agentId)
            return <button key={a.agentId} type="button" className={`chip-toggle ${on ? 'active' : ''}`}
              onClick={() => setRuleAgents(r.id, on ? attached.filter((x) => x !== a.agentId) : [...attached, a.agentId])}>
              {a.displayName || a.agentId}</button>
          })}
        </div>

        <h3 className="sec">When should it step in?</h3>
        <p className="muted small" style={{ margin: '-4px 0 10px' }}>Leave this empty to apply the rule every single time.</p>
        {r.conditions.map((c, i) => ({ c, i })).filter((x) => x.c.field !== 'agentId').map(({ c, i }) => (
          <div key={i}>
          <div className="row cond">
            <select value={THINGS.some((t) => t.v === c.field) ? c.field : '__custom'} onChange={(e) => {
              const nf = e.target.value === '__custom' ? '' : e.target.value
              patchCond(r.id, i, { field: nf, check: defaultCheck(nf), value: '' })
            }}>
              {THINGS.map((t) => <option key={t.v} value={t.v}>{t.label}</option>)}
              <option value="__custom">Something else…</option>
            </select>
            {!THINGS.some((t) => t.v === c.field) && (
              <input value={c.field} onChange={(e) => patchCond(r.id, i, { field: e.target.value })} placeholder="what to check" />
            )}
            <select value={c.check} onChange={(e) => patchCond(r.id, i, { check: e.target.value })}>
              {checksFor(c.field).map((o) => <option key={o.v} value={o.v}>{o.label}</option>)}
            </select>
            <input value={c.value} onChange={(e) => patchCond(r.id, i, { value: e.target.value })} placeholder={c.check === 'in' || c.check === 'notIn' ? 'e.g. USD, AED' : 'value'} disabled={c.check === 'exists'} />
            <button className="btn ghost danger small" onClick={() => patch(r.id, { conditions: r.conditions.filter((_, j) => j !== i) })} aria-label="Remove this check"><svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M6 6l12 12M18 6 6 18"/></svg></button>
          </div>
          {c.aggregate && <p className="muted small cond-note">Checks the {plainAggregate(c.aggregate)}, not just this one request.</p>}
          </div>
        ))}
        <button className="btn ghost small" style={{ marginTop: 8 }} onClick={() => patch(r.id, { conditions: [...r.conditions, { field: 'amount', check: 'greaterThan', value: '' }] })}>+ Add a check</button>

        <h3 className="sec">What should happen?</h3>
        <div className="outcome-cards">
          {OUTCOMES.map((o) => (
            <button key={o.v} className={`outcome-card ${o.cls} ${r.decision === o.v ? 'active' : ''}`} onClick={() => patch(r.id, { decision: o.v })}>
              <span className="oc-title">{o.label}</span>
              <span className="oc-help">{o.help}</span>
            </button>
          ))}
        </div>

        {r.decision === 'REQUIRE_APPROVAL' && (
          <>
            <label style={{ marginTop: 14 }}>Who can approve it?
              <select value={r.approver} onChange={(e) => patch(r.id, { approver: e.target.value })}>
                {APPROVERS.map((a) => <option key={a}>{a}</option>)}
              </select>
            </label>
            <label style={{ marginTop: 14 }}>How many people must approve it?
              <select value={r.requiredApprovals} onChange={(e) => patch(r.id, { requiredApprovals: Number(e.target.value) })}>
                <option value={1}>One person</option>
                <option value={2}>Two different people</option>
                <option value={3}>Three different people</option>
              </select>
            </label>
            {r.requiredApprovals > 1 && (
              <p className="muted small" style={{ margin: '6px 0 0' }}>It runs only after {r.requiredApprovals} different approvers say yes. The person who asked can’t be one of them, and one “no” stops it.</p>
            )}
          </>
        )}
      </div>
    )
  }

  // The rule whose editor is open below the grid.
  const editRule = rules.find((r) => r.id === editing)
  // With two-person rule changes on, saving sends the rules for a second administrator's approval.
  const saveLabel = active.data?.requireSecondApprover ? 'Send for approval' : 'Save & make live'

  if (active.loading) return <div><PageHeader title="Rules" /><Loading what="your rules" /></div>

  return (
    <div>
      <PageHeader title="Rules" actions={canEdit ? <button className="btn primary" disabled={busy || !dirty} onClick={save}>{busy ? 'Saving…' : saveLabel}</button> : undefined} />
      <Intro>The limits your agents must follow. Each card is a rule — it <b>allows</b>, <b>asks you first</b>, or <b>stops</b> an action, and shows the agents it’s attached to. Click a rule to edit it. Anything no rule covers is stopped by default, to stay safe.</Intro>
      {!access.loading && !canEdit && (
        <p className="persona-note">You can read the rules, but only administrators can change them.</p>
      )}
      {changes.data?.requireSecondApprover && (
        <p className="persona-note">Rule changes need a second administrator: saving sends them for approval, and they go live once someone else approves.</p>
      )}

      <PendingChanges
        changes={(changes.data?.changes ?? []).filter((c) => c.status === 'Pending')}
        live={active.data?.rules ?? []} liveHash={active.data?.hash}
        me={access.email} canDecide={canEdit} busy={busy} onDecide={decideChange} />

      {rules.length === 0 && (
        <section className="card" style={{ textAlign: 'center', padding: '32px 20px' }}>
          <p className="muted">No rules yet.{canEdit ? ' Add your first one to start guiding your agents.' : ''}</p>
          {canEdit && <button className="btn primary" onClick={() => addRule()}>+ Add a rule</button>}
        </section>
      )}

      {/* A grid of rule cards — each shows the agents it's attached to. Click one to edit it below. */}
      {rules.length > 0 && (
        <div className="rules-grid">
          {rules.map((r) => (
            <RuleTile key={r.id} r={r} actionLabel={actionLabel} sentence={ruleSentence(r)}
              agents={agentsForRule(r).map((id) => agentLabel(id))}
              active={editing === r.id} onClick={() => setEditing(editing === r.id ? undefined : r.id)} />
          ))}
          {canEdit && (
            <button type="button" className="rule-tile add-tile" onClick={() => addRule()}>
              <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round"><path d="M12 5v14M5 12h14" /></svg>
              <span>Add a rule</span>
            </button>
          )}
        </div>
      )}

      {/* Clicking a rule card opens its editor in a side drawer. */}
      {editRule && (
        <Drawer wide title={editRule.name.trim() || 'Edit rule'} onClose={() => setEditing(undefined)}
          footer={canEdit ? (
            <div className="row" style={{ margin: 0, gap: 10 }}>
              <button className="btn danger" onClick={() => removeRule(editRule.id)}>Remove</button>
              <button className="btn" onClick={() => setEditing(undefined)}>Done</button>
              <button className="btn primary" style={{ marginLeft: 'auto' }} disabled={busy || !dirty} onClick={save}>{busy ? 'Saving…' : saveLabel}</button>
            </div>
          ) : undefined}>
          {renderEditor(editRule)}
        </Drawer>
      )}

      {rules.length > 0 && canEdit && (
        <div className="row" style={{ marginTop: 18 }}>
          <button className="btn primary" style={{ marginLeft: 'auto' }} disabled={busy || !dirty} onClick={save}>{busy ? 'Saving…' : saveLabel}</button>
        </div>
      )}

      {dirty && <p className="muted small" style={{ marginTop: 10 }}>You have unsaved changes. Click <b>{saveLabel}</b> to apply them.</p>}
    </div>
  )
}

/** Rule changes waiting for a second administrator: what each one changes, and approve / reject. */
function PendingChanges({ changes, live, liveHash, me, canDecide, busy, onDecide }: {
  changes: PolicyChange[]; live: RawRule[]; liveHash?: string; me: string; canDecide: boolean; busy: boolean
  onDecide: (c: PolicyChange, approve: boolean) => void
}) {
  if (!changes.length) return null
  return (
    <section className="card pending-changes">
      <h3 className="sec" style={{ marginTop: 0 }}>Waiting for a second administrator</h3>
      <div className="stack">
        {changes.map((c) => {
          const d = diffRules(live, c.snapshot.rules ?? [])
          const mine = !!me && c.proposedByEmail === me.toLowerCase()
          const stale = !!liveHash && !!c.basedOnHash && liveHash.toLowerCase() !== c.basedOnHash.toLowerCase()
          return (
            <div key={c.changeId} className="permit-row">
              <div className="row" style={{ margin: 0, alignItems: 'flex-start' }}>
                <div>
                  <b>Version {c.version}</b> <span className="muted">· {c.ruleCount} rules · proposed by {c.proposedBy} {timeAgo(c.proposedAtUtc)}</span>
                  <ul className="rules">
                    {d.added.length > 0 && <li>Adds: {d.added.join(', ')}</li>}
                    {d.changed.length > 0 && <li>Changes: {d.changed.join(', ')}</li>}
                    {d.removed.length > 0 && <li>Removes: {d.removed.join(', ')}</li>}
                    {!d.added.length && !d.changed.length && !d.removed.length && <li>No rule changes (only the version number)</li>}
                  </ul>
                  {stale && <p className="muted small">The live rules changed after this was proposed, so it can’t be approved. Ask for it to be proposed again.</p>}
                  {mine && !stale && <p className="muted small">You proposed this, so a different administrator must approve it.</p>}
                </div>
                {canDecide && (
                  <div className="row" style={{ margin: 0, marginLeft: 'auto', gap: 8 }}>
                    <button className="btn primary" disabled={busy || mine || stale} onClick={() => onDecide(c, true)}>Approve &amp; make live</button>
                    <button className="btn danger" disabled={busy} onClick={() => onDecide(c, false)}>Reject</button>
                  </div>
                )}
              </div>
            </div>
          )
        })}
      </div>
    </section>
  )
}

// One rule card in the grid: outcome tone, the rule summary, and the AGENTS it's attached to. Click to edit below.
function RuleTile({ r, actionLabel, sentence, agents, active, onClick }: {
  r: Rule; actionLabel: (id: string) => string; sentence: string; agents: string[]; active: boolean; onClick: () => void
}) {
  const out = OUTCOMES.find((o) => o.v === r.decision) ?? OUTCOMES[0]
  return (
    <button type="button" className={`rule-tile tone-${out.cls} ${active ? 'active' : ''}`} onClick={onClick} aria-pressed={active}>
      <span className="tile-accent" />
      <div className="row spread" style={{ margin: 0, alignItems: 'flex-start', gap: 10 }}>
        <span className="rt-name">{r.name.trim() || 'Untitled rule'}</span>
        <span className={`pill ${out.cls}`}>{r.decision === 'REQUIRE_APPROVAL' && r.requiredApprovals > 1 ? `Ask ${r.requiredApprovals} people first` : out.label}</span>
      </div>
      <div className="rt-desc">For <b>{actionLabel(r.actionId)}</b> — when <b>{sentence}</b></div>
      <div className="rt-attach">
        <span className="rt-attach-label">Attached to</span>
        <div className="rt-agents">
          {agents.length === 0
            ? <span className="agent-chip all">All agents</span>
            : agents.map((a) => <span key={a} className="agent-chip">{a}</span>)}
        </div>
      </div>
    </button>
  )
}
