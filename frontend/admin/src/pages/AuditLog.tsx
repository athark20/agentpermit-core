import { useMemo, useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, Loading, EmptyState } from '../components/ui'
import type { AuditEvent } from '../types'

export function AuditLog({ embedded }: { embedded?: boolean } = {}) {
  const list = useAsync<AuditEvent[]>(() => api('/api/admin/audit?take=200'))
  const [q, setQ] = useState('')
  const filtered = useMemo(() => (list.data ?? []).filter((e) =>
    !q || `${e.actor} ${e.action} ${e.targetType} ${e.targetId} ${e.summary}`.toLowerCase().includes(q.toLowerCase())
  ), [list.data, q])

  return (
    <div>
      {!embedded && <PageHeader title="Audit Log"
        subtitle="Immutable record of every administrative change — who changed what, when and why."
        actions={<button className="btn ghost" onClick={() => list.reload()}>Refresh</button>} />}
      <div className="toolbar">
        <input className="search" type="search" aria-label="Search audit log" placeholder="Search actor, action or target…" value={q} onChange={(e) => setQ(e.target.value)} />
        <span className="muted count">{filtered.length} of {list.data?.length ?? 0}</span>
      </div>
      <section className="card">
        {list.loading ? <Loading what="audit trail" /> : (filtered.length ? (
          <table className="table">
            <thead><tr><th>When (UTC)</th><th>Actor</th><th>Action</th><th>Target</th><th>Summary</th></tr></thead>
            <tbody>
              {filtered.map((e) => (
                <tr key={e.auditEventId}>
                  <td className="muted">{new Date(e.occurredUtc).toISOString().replace('T', ' ').slice(0, 19)}</td>
                  <td>{e.actor}</td>
                  <td><span className="chip">{e.action}</span></td>
                  <td>{e.targetType}{e.targetId ? ` · ${e.targetId}` : ''}</td>
                  <td className="muted">{e.summary ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No audit events yet" hint="Policy publishes, target/agent changes and approvals are recorded here." />)}
      </section>
    </div>
  )
}
