import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatusPill, Loading, EmptyState } from '../components/ui'
import type { BusinessAction } from '../types'

export function BusinessActions() {
  const list = useAsync<BusinessAction[]>(() => api('/api/admin/actions'))
  return (
    <div>
      <PageHeader title="Agent Actions"
        subtitle="The operations your agents are allowed to request — each with a risk tier and the system it runs against." />
      <section className="card">
        {list.loading ? <Loading what="actions" /> : (list.data?.length ? (
          <table className="table">
            <thead><tr><th>Action</th><th>Domain</th><th>Risk</th><th>Target system</th></tr></thead>
            <tbody>
              {list.data.map((a) => (
                <tr key={a.actionId}>
                  <td><b>{a.actionId}</b><div className="muted">{a.displayName}</div></td>
                  <td>{a.domain || '—'}</td>
                  <td>{a.risk ? <StatusPill s={a.risk} /> : '—'}</td>
                  <td className="mono">{a.targetSystem || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No business actions registered" />)}
        <p className="muted note">Action schema editing, approval capability and policy coverage arrive with the
          full Business Actions module.</p>
      </section>
    </div>
  )
}
