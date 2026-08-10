import { useState } from 'react'
import { api, ApiError } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatCard, Loading, EmptyState, Drawer, rowActivate } from '../components/ui'

interface RolloutStatus {
  agentId: string
  displayName: string
  posture: string
  warmingUp: boolean
  fullyEnforced: boolean
  daysSinceUpdated?: number
  drift: string
  recommendation?: string | null
  observationsInWindow?: number
  wouldHaveBlockedInWindow?: number
  reasons: string[]
}
interface Overview { total: number; enforced: number; warmingUp: number; drifted: number; agents: RolloutStatus[] }

const POSTURE_CLS: Record<string, string> = { ENFORCE: 'allow', WARN: 'approval', OBSERVE: 'approval' }

export function Rollout() {
  const ov = useAsync<Overview>(() => api('/api/admin/rollout'))
  const [busy, setBusy] = useState<string>()
  const [evidence, setEvidence] = useState<string>()

  async function move(id: string, dir: 'promote' | 'demote') {
    setBusy(id + dir)
    try {
      await api(`/api/admin/rollout/${encodeURIComponent(id)}/${dir}`, { method: 'POST' })
      ov.reload()
    } catch (e) {
      alert(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e))
    } finally {
      setBusy(undefined)
    }
  }

  const o = ov.data
  return (
    <div>
      <PageHeader title="Rollout" subtitle="Warm agents up safely (OBSERVE → WARN), then graduate them to full enforcement" />

      {o && (
        <div className="tiles">
          <StatCard label="Agents" value={o.total} />
          <StatCard label="Fully enforced" value={o.enforced} tone="allow" />
          <StatCard label="Warming up" value={o.warmingUp} tone="approval" sub="high-consequence still enforced" />
          <StatCard label="Drifted" value={o.drifted} tone={o.drifted ? 'deny' : 'neutral'} sub="stale warm-up" />
        </div>
      )}

      <section className="card">
        {ov.loading ? <Loading what="rollout" /> : (o && o.agents.length ? (
          <table className="table">
            <thead>
              <tr>
                <th>Agent</th><th>Posture</th><th>Observations (7d)</th><th>Would-have-blocked</th>
                <th>Drift</th><th>Recommendation</th><th></th>
              </tr>
            </thead>
            <tbody>
              {o.agents.map((a) => (
                <tr key={a.agentId}>
                  <td className="rowlink" {...rowActivate(() => setEvidence(a.agentId), `View evidence for ${a.agentId}`)}><b>{a.agentId}</b></td>
                  <td><span className={`pill ${POSTURE_CLS[a.posture] ?? 'neutral'}`}>{a.posture}</span></td>
                  <td>{a.observationsInWindow ?? '—'}</td>
                  <td>{a.wouldHaveBlockedInWindow
                    ? <span className="pill deny">{a.wouldHaveBlockedInWindow}</span>
                    : (a.observationsInWindow != null ? '0' : '—')}</td>
                  <td>{a.drift === 'STALE_WARMUP' ? <span className="pill deny">Stale</span> : <span className="muted">—</span>}</td>
                  <td className="muted small">
                    {a.fullyEnforced ? 'Enforced'
                      : a.recommendation ? `Promote → ${a.recommendation}`
                      : (a.reasons[0] ?? 'Hold')}
                  </td>
                  <td className="row" style={{ margin: 0, gap: 6, justifyContent: 'flex-end' }}>
                    {a.posture !== 'ENFORCE' && (
                      <button className="btn primary" disabled={busy === a.agentId + 'promote'} onClick={() => move(a.agentId, 'promote')}>Promote</button>
                    )}
                    {a.posture !== 'OBSERVE' && (
                      <button className="btn" disabled={busy === a.agentId + 'demote'} onClick={() => move(a.agentId, 'demote')}>Roll back</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No agents" hint="Register or import agents to manage their rollout." />)}
      </section>

      <p className="muted small" style={{ padding: '0 4px' }}>
        During warm-up, low/medium-consequence blocks are recorded but not applied — high/critical-consequence
        actions stay enforced from day one. Promote once an agent has enough clean traffic with zero would-have-been-blocks.
      </p>

      {evidence && <EvidenceDrawer agentId={evidence} onClose={() => setEvidence(undefined)} />}
    </div>
  )
}

interface Obs {
  observationId: string; occurredUtc: string; actionId: string
  computedDecision: string; effectiveDecision: string; enforcementMode: string; wouldHaveBlocked: boolean
}
interface Evidence { agentId: string; windowDays: number; stats: { total: number; wouldHaveBlocked: number }; recent: Obs[] }

function EvidenceDrawer({ agentId, onClose }: { agentId: string; onClose: () => void }) {
  const ev = useAsync<Evidence>(() => api(`/api/admin/observations/${encodeURIComponent(agentId)}?limit=50`))
  const d = ev.data
  return (
    <Drawer title={`${agentId} · observations`} onClose={onClose}>
      {ev.loading ? <Loading what="observations" /> : d && (
        <>
          <div className="tiles">
            <StatCard label={`Observed (${d.windowDays}d)`} value={d.stats.total} />
            <StatCard label="Would-have-blocked" value={d.stats.wouldHaveBlocked} tone={d.stats.wouldHaveBlocked ? 'deny' : 'allow'} />
          </div>
          {d.recent.length ? (
            <table className="table">
              <thead><tr><th>When</th><th>Action</th><th>Computed</th><th>Effective</th><th>Mode</th></tr></thead>
              <tbody>
                {d.recent.map((r) => (
                  <tr key={r.observationId}>
                    <td className="muted small">{new Date(r.occurredUtc).toLocaleString()}</td>
                    <td>{r.actionId}</td>
                    <td>{r.computedDecision}</td>
                    <td>{r.effectiveDecision}</td>
                    <td>{r.wouldHaveBlocked
                      ? <span className="pill deny">{r.enforcementMode}</span>
                      : <span className="muted">{r.enforcementMode}</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : <EmptyState title="No observations yet" hint="Governed requests for this agent will appear here." />}
        </>
      )}
    </Drawer>
  )
}
