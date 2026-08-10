import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatusPill, Loading, EmptyState } from '../components/ui'
import type { Execution } from '../types'

export function Executions() {
  const list = useAsync<Execution[]>(() => api('/api/admin/executions?take=200'))
  return (
    <div>
      <PageHeader title="Executions"
        subtitle="Every attempt to run an authorized action against a target system, with retries and outcomes."
        actions={<button className="btn ghost" onClick={() => list.reload()}>Refresh</button>} />
      <section className="card">
        {list.loading ? <Loading what="executions" /> : (list.data?.length ? (
          <table className="table">
            <thead><tr><th>Adapter</th><th>Status</th><th>Attempt</th><th>Target reference</th><th>Started</th><th>Error</th></tr></thead>
            <tbody>
              {list.data.map((e) => (
                <tr key={e.executionId}>
                  <td className="mono">{e.adapterCode}</td>
                  <td><StatusPill s={e.status} /></td>
                  <td>{e.attemptNumber}</td>
                  <td className="mono">{e.targetResourceId ?? '—'}</td>
                  <td className="muted">{new Date(e.startedUtc).toLocaleString()}</td>
                  <td className="muted">{e.errorCode ? `${e.errorCode}` : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No executions yet" hint="Authorized actions that run against a target appear here." />)}
      </section>
    </div>
  )
}
