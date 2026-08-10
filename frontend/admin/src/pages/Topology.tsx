import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, Loading } from '../components/ui'
import type { Agent, Integration, AuditEvent } from '../types'

export function Topology() {
  const agents = useAsync<Agent[]>(() => api('/api/admin/agents'))
  const integrations = useAsync<Integration[]>(() => api('/api/admin/integrations'))
  const events = useAsync<AuditEvent[]>(() => api('/api/admin/audit?take=6'))

  const agentList = agents.data ?? []
  const targetList = integrations.data ?? []
  const healthy = agentList.filter((a) => a.enabled).length
  const blocked = agentList.filter((a) => !a.enabled).length
  const targetsDown = targetList.filter((t) => t.status !== 'Healthy').length

  return (
    <div>
      <PageHeader title="Agent Map" subtitle="Live view of your agents and the systems they can reach"
        actions={<span className="chip live"><i className="ldot live" /> Live</span>} />

      <div className="topo">
        <section className="topo-canvas">
          {(agents.loading || integrations.loading) ? <Loading what="topology" /> : (
            <>
              <div className="topo-control"><div className="topo-node control"><i className="ldot" /> Control plane</div></div>
              <div className="topo-connector" />

              <div className="topo-tier-label">Agents ({agentList.length})</div>
              <div className="topo-grid">
                {agentList.map((a) => (
                  <div key={a.agentId} className={`topo-node ${a.enabled ? 'ok' : 'bad'}`}>
                    <div className="n-name"><span className={`n-dot dot ${a.enabled ? 'ok' : 'bad'}`} />{a.agentId}</div>
                    <div className="n-meta">{a.enabled ? 'permitted' : 'blocked'}</div>
                  </div>
                ))}
                {agentList.length === 0 && <div className="muted">No agents registered.</div>}
              </div>

              <div className="topo-connector" />
              <div className="topo-tier-label">Target systems ({targetList.length})</div>
              <div className="topo-grid">
                {targetList.map((t) => {
                  const cls = t.status === 'Healthy' ? 'ok' : t.status === 'Disabled' ? '' : 'warn'
                  const dot = t.status === 'Healthy' ? 'ok' : t.status === 'Disabled' ? 'warn' : 'bad'
                  return (
                    <div key={t.targetSystemCode} className={`topo-node ${cls}`}>
                      <div className="n-name"><span className={`n-dot dot ${dot}`} />{t.targetSystemCode}</div>
                      <div className="n-meta">{t.adapterType} · {t.status}</div>
                    </div>
                  )
                })}
                {targetList.length === 0 && <div className="muted">No target systems registered.</div>}
              </div>
            </>
          )}
        </section>

        <aside>
          <section className="card">
            <h3 style={{ marginBottom: 12 }}>Fleet summary</h3>
            <div className="kv"><span>Total agents</span><b>{agentList.length}</b></div>
            <div className="kv"><span>Target systems</span><b>{targetList.length}</b></div>
            <div style={{ marginTop: 16 }}>
              <div className="muted small" style={{ marginBottom: 8 }}>HEALTH BREAKDOWN</div>
              <div className="dotlist">
                <div className="dotrow"><span className="dot ok" /><span>Healthy · <b>{healthy}</b></span></div>
                <div className="dotrow"><span className="dot warn" /><span>Targets degraded · <b>{targetsDown}</b></span></div>
                <div className="dotrow"><span className="dot bad" /><span>Blocked · <b>{blocked}</b></span></div>
              </div>
            </div>
          </section>

          <section className="card">
            <h3 style={{ marginBottom: 12 }}>Recent events</h3>
            {events.loading ? <Loading /> : (events.data?.length ? (
              <div className="dotlist">
                {events.data.map((e) => (
                  <div className="dotrow" key={e.auditEventId}>
                    <span className="dot ok" />
                    <span className="small">{e.actor} · {e.action}<div className="muted">{new Date(e.occurredUtc).toLocaleTimeString()}</div></span>
                  </div>
                ))}
              </div>
            ) : <p className="muted small">No recent events.</p>)}
          </section>
        </aside>
      </div>
    </div>
  )
}
