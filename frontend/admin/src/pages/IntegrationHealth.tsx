import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatusPill, Loading, EmptyState } from '../components/ui'
import type { Integration } from '../types'

export function IntegrationHealth() {
  const list = useAsync<Integration[]>(() => api('/api/admin/integrations'))
  return (
    <div>
      <PageHeader title="Integration Health"
        subtitle="Live health of every connected target system and its adapter."
        actions={<button className="btn ghost" onClick={() => list.reload()}>Re-check</button>} />
      <section className="card">
        {list.loading ? <Loading what="integrations" /> : (list.data?.length ? (
          <table className="table">
            <thead><tr><th>Target</th><th>Adapter</th><th>Status</th><th>Message</th><th>Checked</th></tr></thead>
            <tbody>
              {list.data.map((t) => (
                <tr key={t.targetSystemCode}>
                  <td><b>{t.targetSystemCode}</b><div className="muted">{t.displayName}</div></td>
                  <td><span className="chip">{t.adapterType}</span></td>
                  <td><StatusPill s={t.status} /></td>
                  <td className="muted">{t.message ?? '—'}</td>
                  <td className="muted">{new Date(t.checkedAtUtc).toLocaleTimeString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No target systems registered" />)}
        <p className="muted note">Latency and circuit-breaker state expand with per-adapter telemetry.</p>
      </section>
    </div>
  )
}
