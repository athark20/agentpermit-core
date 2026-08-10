import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatusPill, Loading, EmptyState } from '../components/ui'
import type { DelegationEdge } from '../types'

export function Delegation() {
  const list = useAsync<DelegationEdge[]>(() => api('/api/admin/delegation'))
  return (
    <div>
      <PageHeader title="Delegation"
        subtitle="Agent-to-agent delegation observed from real requests (root agent → executing agent). Escalations are flagged." />
      <section className="card">
        {list.loading ? <Loading what="delegation" /> : (list.data?.length ? (
          <table className="table">
            <thead><tr><th>Root agent</th><th></th><th>Executing agent</th><th>Requests</th><th>Escalation</th><th>Last seen</th></tr></thead>
            <tbody>
              {list.data.map((e, i) => (
                <tr key={i}>
                  <td><b>{e.rootAgentId}</b></td>
                  <td className="muted">→</td>
                  <td><b>{e.executingAgentId}</b></td>
                  <td>{e.requests}</td>
                  <td>{e.escalation ? <StatusPill s="DEGRADED" /> : <StatusPill s="ACTIVE" />}</td>
                  <td className="muted">{new Date(e.lastSeenUtc).toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No delegation observed" hint="When one agent acts on behalf of another, the chain appears here." />)}
        <p className="muted note">Registered on-behalf-of scopes and formal escalation policy expand this module.</p>
      </section>
    </div>
  )
}
