import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatusPill, Loading, EmptyState } from '../components/ui'
import type { ExceptionItem } from '../types'

export function Exceptions() {
  const list = useAsync<ExceptionItem[]>(() => api('/api/admin/exceptions?take=200'))
  return (
    <div>
      <PageHeader title="Exceptions"
        subtitle="Requests and executions that need attention: execution failures and failed requests."
        actions={<button className="btn ghost" onClick={() => list.reload()}>Refresh</button>} />
      <section className="card">
        {list.loading ? <Loading what="exceptions" /> : (list.data?.length ? (
          <table className="table">
            <thead><tr><th>Kind</th><th>Detail</th><th>Code</th><th>Message</th><th>When</th><th>Reference</th></tr></thead>
            <tbody>
              {list.data.map((x, i) => (
                <tr key={i}>
                  <td><StatusPill s="FAILED" /> {x.kind}</td>
                  <td>{x.detail ?? '—'}</td>
                  <td className="mono">{x.code ?? '—'}</td>
                  <td className="muted">{x.message ?? '—'}</td>
                  <td className="muted">{new Date(x.occurredUtc).toLocaleString()}</td>
                  <td className="mono ellipsis" title={x.reference}>{x.reference.slice(0, 8)}…</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No exceptions" hint="Failed executions and requests will surface here for triage." />)}
      </section>
    </div>
  )
}
