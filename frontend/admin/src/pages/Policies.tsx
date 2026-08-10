import { useEffect, useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, DecisionPill, Loading } from '../components/ui'

const OPERATORS = ['equals', 'notEquals', 'greaterThan', 'greaterThanOrEqual', 'lessThan', 'lessThanOrEqual', 'in', 'notIn', 'exists']
const DECISIONS = [
  { v: 'ALLOW', label: 'Allow' },
  { v: 'REQUIRE_APPROVAL', label: 'Step-up' },
  { v: 'DENY', label: 'Deny' },
]

interface Cond { field: string; operator: string; value: string }
interface Rule { id: string; name: string; priority: number; actionId: string; decision: string; approverRoles: string; description: string; conditions: Cond[] }
interface Active { policyCode: string; version: string; defaultDecision: string; rules: RawRule[] }
interface RawRule { id: string; name: string; priority: number; actionId?: string; decision: string; approverRoles?: string[]; description?: string; conditions?: { field: string; operator: string; value: unknown }[] }

function toStr(v: unknown): string { return Array.isArray(v) ? v.join(', ') : v == null ? '' : String(v) }
function coerce(v: string): string | number { return /^-?\d+(\.\d+)?$/.test(v.trim()) ? Number(v) : v }
function condValue(c: Cond): unknown {
  if (c.operator === 'exists') return true
  if (c.operator === 'in' || c.operator === 'notIn') return c.value.split(',').map((s) => coerce(s.trim()))
  return coerce(c.value)
}
function nextVersion(v: string): string {
  const m = v.match(/^(\d+)\.(\d+)\.(\d+)$/)
  if (m) return `${m[1]}.${m[2]}.${Number(m[3]) + 1}`
  const m2 = v.match(/^(\d+)\.(\d+)$/)
  if (m2) return `${m2[1]}.${Number(m2[2]) + 1}.0`
  return v + '.1'
}
function fromRaw(r: RawRule): Rule {
  return {
    id: r.id, name: r.name, priority: r.priority, actionId: r.actionId ?? '', decision: r.decision,
    approverRoles: (r.approverRoles ?? []).join(', '), description: r.description ?? '',
    conditions: (r.conditions ?? []).map((c) => ({ field: c.field, operator: c.operator, value: toStr(c.value) })),
  }
}

export function Policies() {
  const active = useAsync<Active>(() => api('/api/policies/active'))
  const [rules, setRules] = useState<Rule[]>([])
  const [policyCode, setPolicyCode] = useState('')
  const [version, setVersion] = useState('')
  const [defaultDecision, setDefaultDecision] = useState('DENY')
  const [selId, setSelId] = useState<string>()
  const [msg, setMsg] = useState<{ ok: boolean; text: string }>()
  const [busy, setBusy] = useState(false)
  const [sim, setSim] = useState<string>()

  useEffect(() => {
    if (!active.data) return
    setRules(active.data.rules.map(fromRaw))
    setPolicyCode(active.data.policyCode)
    setVersion(active.data.version)
    setDefaultDecision(active.data.defaultDecision)
    setSelId(active.data.rules[0]?.id)
  }, [active.data])

  const sel = rules.find((r) => r.id === selId)
  const patch = (p: Partial<Rule>) => setRules((rs) => rs.map((r) => r.id === selId ? { ...r, ...p } : r))
  const patchCond = (i: number, p: Partial<Cond>) => patch({ conditions: sel!.conditions.map((c, j) => j === i ? { ...c, ...p } : c) })

  function addRule() {
    const id = `rule-${Date.now().toString(36)}`
    setRules((rs) => [...rs, { id, name: 'New rule', priority: (rs.length + 1) * 10, actionId: 'Refund.Issue', decision: 'ALLOW', approverRoles: '', description: '', conditions: [] }])
    setSelId(id)
  }
  function delRule() { setRules((rs) => rs.filter((r) => r.id !== selId)); setSelId(rules[0]?.id) }

  function buildSnapshot(nextVer: string) {
    return {
      policyCode, version: nextVer, defaultDecision,
      rules: rules.map((r) => ({
        id: r.id, name: r.name, priority: r.priority, actionId: r.actionId, decision: r.decision,
        ...(r.description ? { description: r.description } : {}),
        ...(r.approverRoles.trim() ? { approverRoles: r.approverRoles.split(',').map((s) => s.trim()).filter(Boolean) } : {}),
        conditions: r.conditions.map((c) => ({ field: c.field, operator: c.operator, value: condValue(c) })),
      })),
    }
  }

  async function publish() {
    setBusy(true); setMsg(undefined)
    const nv = nextVersion(version)
    try {
      const res = await api<{ version: string; signed: boolean }>('/api/policies/publish', { method: 'POST', body: JSON.stringify(buildSnapshot(nv)) })
      setMsg({ ok: true, text: `Published v${res.version} — signed=${res.signed}. Enforced now, no redeploy.` })
      active.reload()
    } catch (e) { setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) }) }
    finally { setBusy(false) }
  }
  async function dryRun() {
    if (!sel) return
    setSim(undefined)
    try {
      const r = await api<{ decision: string; reason: string }>('/api/policies/simulate', {
        method: 'POST', body: JSON.stringify({ actionId: sel.actionId, agentId: 'copilot-refund-demo', parameters: { amount: 750 } }),
      })
      setSim(`Sample (amount 750) → ${r.decision} · ${r.reason}`)
    } catch (e) { setSim(String(e)) }
  }

  if (active.loading) return <div><PageHeader title="Policies" subtitle="Author & version permit rules" /><Loading what="policy" /></div>

  return (
    <div>
      <PageHeader title="Policies" subtitle="Author & version permit rules" />
      <div className="policy-cols">
        <aside className="card policy-lib">
          <div className="lib-head">Policy library<div className="muted small">{rules.length} rules · v{version} active</div></div>
          {rules.map((r) => (
            <button key={r.id} className={`lib-item ${selId === r.id ? 'active' : ''}`} onClick={() => setSelId(r.id)}>
              <div className="lib-name">{r.name}</div>
              <div className="lib-meta"><DecisionPill d={r.decision} /> <span className="muted small">priority {r.priority}</span></div>
            </button>
          ))}
          <button className="btn ghost" style={{ width: '100%', marginTop: 10 }} onClick={addRule}>+ New rule</button>
        </aside>

        <section className="card">
          {!sel ? <p className="muted">Select a rule, or add one.</p> : (
            <>
              <div className="row spread" style={{ marginTop: 0 }}>
                <div>
                  <h2 style={{ fontSize: 18 }}>{sel.name || 'Untitled rule'}</h2>
                  <div className="muted small">{policyCode} · priority {sel.priority}</div>
                </div>
                <button className="btn primary" disabled={busy} onClick={publish}>Publish v{nextVersion(version)}</button>
              </div>

              <div className="row">
                <label>Rule name<input value={sel.name} onChange={(e) => patch({ name: e.target.value })} /></label>
                <label className="narrow">Priority<input type="number" value={sel.priority} onChange={(e) => patch({ priority: Number(e.target.value) })} /></label>
              </div>
              <label>Description<input value={sel.description} onChange={(e) => patch({ description: e.target.value })} placeholder="What this permit rule does" /></label>

              <h3 className="sec">Outcome</h3>
              <div className="segment">
                {DECISIONS.map((d) => (
                  <button key={d.v} className={`seg ${sel.decision === d.v ? 'active' : ''}`} aria-pressed={sel.decision === d.v} onClick={() => patch({ decision: d.v })}>{d.label}</button>
                ))}
              </div>

              <h3 className="sec">Who this applies to</h3>
              <label>Business action<input value={sel.actionId} onChange={(e) => patch({ actionId: e.target.value })} placeholder="Refund.Issue (blank = any action)" /></label>

              <h3 className="sec">Conditions <span className="muted small">— all must match for the permit to apply</span></h3>
              {sel.conditions.map((c, i) => (
                <div className="row cond" key={i}>
                  <input value={c.field} onChange={(e) => patchCond(i, { field: e.target.value })} placeholder="amount" />
                  <select value={c.operator} onChange={(e) => patchCond(i, { operator: e.target.value })}>{OPERATORS.map((o) => <option key={o}>{o}</option>)}</select>
                  <input value={c.value} onChange={(e) => patchCond(i, { value: e.target.value })} placeholder="value" disabled={c.operator === 'exists'} />
                  <button className="btn ghost danger small" onClick={() => patch({ conditions: sel.conditions.filter((_, j) => j !== i) })} aria-label="Close"><svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M6 6l12 12M18 6 6 18"/></svg></button>
                </div>
              ))}
              <button className="btn ghost small" style={{ marginTop: 8 }} onClick={() => patch({ conditions: [...sel.conditions, { field: 'amount', operator: 'lessThan', value: '0' }] })}>+ Add condition</button>

              {sel.decision === 'REQUIRE_APPROVAL' && (
                <>
                  <h3 className="sec">Step-up &amp; escalation</h3>
                  <label>Approver roles (comma-separated)<input value={sel.approverRoles} onChange={(e) => patch({ approverRoles: e.target.value })} placeholder="RefundApprover" /></label>
                  <p className="muted small">Matching requests require owner sign-off before the permit is granted (human-in-the-loop).</p>
                </>
              )}

              <div className="row" style={{ marginTop: 20, borderTop: '1px solid var(--line)', paddingTop: 16 }}>
                <button className="btn ghost" onClick={dryRun}>Dry-run</button>
                <button className="btn ghost danger" onClick={delRule}>Delete rule</button>
                <button className="btn primary" style={{ marginLeft: 'auto' }} disabled={busy} onClick={publish}>Publish v{nextVersion(version)}</button>
              </div>
              {sim && <p className="muted mono small">{sim}</p>}
              {msg && <p className={msg.ok ? 'success' : 'error'}>{msg.text}</p>}
            </>
          )}
        </section>
      </div>
    </div>
  )
}
