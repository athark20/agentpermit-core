import { useMemo, useState } from 'react'
import { api, ApiError } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, RiskLabel, Drawer, Loading, EmptyState, rowActivate } from '../components/ui'
import type { Agent, ActionRequest } from '../types'

type Status = 'Permitted' | 'Pending' | 'Blocked'
function statusOf(a: Agent): Status {
  if (!a.enabled) return 'Blocked'
  return a.entraClientId ? 'Permitted' : 'Pending'
}
const STATUS_CLS: Record<Status, string> = { Permitted: 'allow', Pending: 'approval', Blocked: 'deny' }

function ago(iso?: string): string {
  if (!iso) return 'never'
  const s = Math.floor((Date.now() - new Date(iso).getTime()) / 1000)
  if (s < 60) return `${s}s ago`
  if (s < 3600) return `${Math.floor(s / 60)}m ago`
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`
  return `${Math.floor(s / 86400)}d ago`
}

const BLANK: Agent = { agentId: '', displayName: '', enabled: true, entraClientId: '', type: 'Autonomous', owner: '', environment: '', riskTier: 'Low' }

export function Agents() {
  const list = useAsync<Agent[]>(() => api('/api/admin/agents'))
  const requests = useAsync<ActionRequest[]>(() => api('/api/admin/requests?take=200'))
  const [filter, setFilter] = useState<'all' | Status>('all')
  const [q, setQ] = useState('')
  const [form, setForm] = useState<Agent>()

  // Derive last-seen + permit count per agent from real requests.
  const activity = useMemo(() => {
    const m: Record<string, { last?: string; count: number }> = {}
    for (const r of requests.data ?? []) {
      const e = (m[r.executingAgentId] ??= { count: 0 })
      e.count++
      if (!e.last || r.createdUtc > e.last) e.last = r.createdUtc
    }
    return m
  }, [requests.data])

  const agents = list.data ?? []
  const counts = {
    all: agents.length,
    Permitted: agents.filter((a) => statusOf(a) === 'Permitted').length,
    Pending: agents.filter((a) => statusOf(a) === 'Pending').length,
    Blocked: agents.filter((a) => statusOf(a) === 'Blocked').length,
  }
  const rows = agents.filter((a) => {
    if (filter !== 'all' && statusOf(a) !== filter) return false
    if (q && !`${a.agentId} ${a.displayName} ${a.owner}`.toLowerCase().includes(q.toLowerCase())) return false
    return true
  })

  return (
    <div>
      <PageHeader title="Agents" subtitle="Fleet inventory & posture"
        actions={<>
          <button className="btn" onClick={() => { location.hash = '#/onboarding' }}>Import from Copilot Studio</button>
          <button className="btn primary" onClick={() => setForm(BLANK)}>+ Register agent</button>
        </>} />

      <div className="toolbar">
        <div className="segment">
          {(['all', 'Permitted', 'Pending', 'Blocked'] as const).map((f) => (
            <button key={f} className={`seg ${filter === f ? 'active' : ''}`} aria-pressed={filter === f} onClick={() => setFilter(f)}>
              {f === 'all' ? 'All' : f} ({f === 'all' ? counts.all : counts[f]})
            </button>
          ))}
        </div>
        <input className="search" type="search" aria-label="Search agents" style={{ marginLeft: 'auto' }} placeholder="Search agents…" value={q} onChange={(e) => setQ(e.target.value)} />
      </div>

      <section className="card">
        {list.loading ? <Loading what="agents" /> : (rows.length ? (
          <table className="table">
            <thead><tr><th>Agent</th><th>Type</th><th>Owner</th><th>Environment</th><th>Status</th><th>Risk tier</th><th>Last seen</th><th>Permits</th></tr></thead>
            <tbody>
              {rows.map((a) => {
                const st = statusOf(a); const act = activity[a.agentId]
                return (
                  <tr key={a.agentId} className="rowlink" {...rowActivate(() => setForm({ ...BLANK, ...a }), `Open agent ${a.agentId}`)}>
                    <td><b>{a.agentId}</b></td>
                    <td>{a.type ?? '—'}</td>
                    <td>{a.owner ?? '—'}</td>
                    <td>{a.environment ?? '—'}</td>
                    <td><span className={`pill ${STATUS_CLS[st]}`}>{st}</span></td>
                    <td><RiskLabel risk={a.riskTier} /></td>
                    <td className="muted">{ago(act?.last)}</td>
                    <td>{act?.count ?? 0} permits</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        ) : <EmptyState title="No agents" hint="Register an agent to bring it under governance." />)}
      </section>

      <div className="muted small" style={{ display: 'flex', gap: 20, padding: '0 4px' }}>
        <span><i className="ldot allow" /> {counts.Permitted} permitted &amp; healthy</span>
        <span><i className="ldot approval" /> {counts.Pending} pending owner review</span>
        <span><i className="ldot deny" /> {counts.Blocked} blocked</span>
        <span style={{ marginLeft: 'auto' }}>Showing {rows.length} of {agents.length}</span>
      </div>

      {form && <AgentDrawer agent={form} onClose={() => setForm(undefined)} onSaved={() => { setForm(undefined); list.reload() }} />}
    </div>
  )
}

function AgentDrawer({ agent, onClose, onSaved }: { agent: Agent; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState<Agent>(agent)
  const [msg, setMsg] = useState<string>()
  const isNew = !agent.agentId
  const set = (k: keyof Agent) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setF({ ...f, [k]: e.target.value })

  async function save() {
    setMsg(undefined)
    if (!f.agentId || !f.displayName) { setMsg('Agent id and display name are required'); return }
    try {
      await api(`/api/admin/agents/${encodeURIComponent(f.agentId)}`, { method: 'PUT', body: JSON.stringify(f) })
      onSaved()
    } catch (e) { setMsg(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e)) }
  }
  async function remove() {
    try { await api(`/api/admin/agents/${encodeURIComponent(f.agentId)}`, { method: 'DELETE' }); onSaved() }
    catch (e) { setMsg(String(e)) }
  }

  return (
    <Drawer title={isNew ? 'Register agent' : f.agentId} onClose={onClose}
      footer={<div className="row" style={{ margin: 0 }}>
        <button className="btn primary" onClick={save}>Save</button>
        {!isNew && <button className="btn danger" onClick={remove}>Revoke</button>}
      </div>}>
      <label>Agent id<input value={f.agentId} onChange={set('agentId')} disabled={!isNew} placeholder="billing-reconciler" /></label>
      <label>Display name<input value={f.displayName} onChange={set('displayName')} /></label>
      <label>Type<select value={f.type ?? ''} onChange={set('type')}><option>Autonomous</option><option>Copilot</option></select></label>
      <label>Owner<input value={f.owner ?? ''} onChange={set('owner')} placeholder="M. Chen" /></label>
      <label>Environment<input value={f.environment ?? ''} onChange={set('environment')} placeholder="prod-us-east" /></label>
      <label>Risk tier<select value={f.riskTier ?? ''} onChange={set('riskTier')}><option>Low</option><option>Medium</option><option>High</option></select></label>
      <label>Bound Entra client id (azp)<input value={f.entraClientId ?? ''} onChange={set('entraClientId')} placeholder="00000000-… (required to be Permitted)" /></label>
      <label className="check"><input type="checkbox" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.target.checked })} /> Enabled</label>
      {msg && <p className="error">{msg}</p>}
      <p className="muted small">An agent is <b>Permitted</b> once enabled and bound to an Entra identity; <b>Pending</b> until bound;
        <b> Blocked</b> when disabled. Requests are authorized against the bound identity, never a body-supplied id.</p>
    </Drawer>
  )
}
