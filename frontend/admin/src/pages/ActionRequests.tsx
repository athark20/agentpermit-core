import { useMemo, useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatusPill, Drawer, KV, Loading, EmptyState, rowActivate } from '../components/ui'
import type { ActionRequest } from '../types'

export function ActionRequests() {
  const list = useAsync<ActionRequest[]>(() => api('/api/admin/requests?take=200'))
  const [q, setQ] = useState('')
  const [status, setStatus] = useState('all')
  const [open, setOpen] = useState<ActionRequest>()

  const statuses = useMemo(() => Array.from(new Set((list.data ?? []).map((r) => r.status))).sort(), [list.data])
  const filtered = useMemo(() => (list.data ?? []).filter((r) => {
    if (status !== 'all' && r.status !== status) return false
    if (q && !(`${r.actionId} ${r.executingAgentId} ${r.correlationId} ${r.actionRequestId}`.toLowerCase().includes(q.toLowerCase()))) return false
    return true
  }), [list.data, q, status])

  return (
    <div>
      <PageHeader title="Action Requests"
        subtitle="Every governed request, with its decision and lifecycle status — the operational heart of AgentPermit."
        actions={<button className="btn ghost" onClick={() => list.reload()}>Refresh</button>} />

      <div className="toolbar">
        <input className="search" type="search" aria-label="Search action requests" placeholder="Search action, agent or correlation id…" value={q} onChange={(e) => setQ(e.target.value)} />
        <select value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="all">All statuses</option>
          {statuses.map((s) => <option key={s} value={s}>{s.replace(/_/g, ' ')}</option>)}
        </select>
        <span className="muted count">{filtered.length} of {list.data?.length ?? 0}</span>
      </div>

      <section className="card">
        {list.loading ? <Loading what="requests" /> : (filtered.length ? (
          <table className="table">
            <thead><tr><th>Action</th><th>Agent</th><th>Status</th><th>Created</th><th>Correlation</th><th></th></tr></thead>
            <tbody>
              {filtered.map((r) => (
                <tr key={r.actionRequestId} className="rowlink" {...rowActivate(() => setOpen(r), `View request for ${r.actionId}`)}>
                  <td><b>{r.actionId}</b></td>
                  <td>{r.executingAgentId}</td>
                  <td><StatusPill s={r.status} /></td>
                  <td className="muted">{new Date(r.createdUtc).toLocaleString()}</td>
                  <td className="mono ellipsis" title={r.correlationId}>{r.correlationId.slice(0, 8)}…</td>
                  <td className="right"><button className="btn ghost" onClick={(e) => { e.stopPropagation(); setOpen(r) }}>Open</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No action requests yet" hint="Requests agents send through the gateway appear here." />)}
      </section>

      {open && (
        <Drawer title={open.actionId} onClose={() => setOpen(undefined)}>
          <KV label="Status"><StatusPill s={open.status} /></KV>
          <KV label="Executing agent">{open.executingAgentId}</KV>
          <KV label="Root agent">{open.rootAgentId ?? '—'}</KV>
          <KV label="Initiating user">{open.initiatingUserId ?? '—'}</KV>
          <KV label="Created">{new Date(open.createdUtc).toLocaleString()}</KV>
          <KV label="Updated">{new Date(open.updatedUtc).toLocaleString()}</KV>
          <KV label="Request id" mono>{open.actionRequestId}</KV>
          <KV label="Correlation id" mono>{open.correlationId}</KV>
          <KV label="Payload hash" mono>{open.requestPayloadHash}</KV>
          <p className="muted small">The frozen payload hash links this request to its approval and signed receipt —
          the same hash is re-verified before execution.</p>
        </Drawer>
      )}
    </div>
  )
}
