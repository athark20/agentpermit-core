import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatCard, StatusStrip, Loading } from '../components/ui'
import { Intro } from '../plain'
import type { Metrics, ExceptionItem, Agent } from '../types'

interface Infra {
  healthy: boolean
  checks: { name: string; healthy: boolean; detail?: string }[]
  infrastructure: { memoryMb: number; managedMemoryMb: number; processors: number; uptimeHours: number; machine: string; dotnet: string }
}
interface RolloutRow { agentId: string; posture: string }
interface Approval { approvalRequestId: string; requestedAtUtc: string }

// Plain names for the technical health checks.
const CHECK_NAME: Record<string, string> = {
  'sql': 'Database', 'receipt-signing': 'Evidence signing', 'policy-snapshot': 'Rules loaded', 'adapters': 'Connections',
}

export function Dashboard() {
  const infra = useAsync<Infra>(() => api('/api/admin/infra'))
  const metrics = useAsync<Metrics>(() => api('/api/admin/metrics'))
  const exceptions = useAsync<ExceptionItem[]>(() => api('/api/admin/exceptions'))
  const approvals = useAsync<Approval[]>(() => api('/api/approvals/pending'))
  const agents = useAsync<Agent[]>(() => api('/api/admin/agents'))
  const rollout = useAsync<{ agents: RolloutRow[] }>(() => api('/api/admin/rollout'))

  const m = metrics.data
  const activeAgents = (agents.data ?? []).filter((a) => a.enabled).length
  const stale = (approvals.data ?? []).filter((a) => Date.now() - new Date(a.requestedAtUtc).getTime() > 86400000).length
  const failed = (exceptions.data ?? []).length
  const problems = failed + stale
  const inf = infra.data?.infrastructure
  const settling = countStage(rollout.data?.agents, 'OBSERVE') + countStage(rollout.data?.agents, 'WARN')

  return (
    <div>
      <PageHeader title="Dashboard" />
      <Intro>A quick health check of everything — is it all running, what your agents did today, and anything that needs a look.</Intro>

      {/* Reassurance-first: are the agents protected right now? */}
      {!infra.loading && (
        <StatusStrip
          safe={!!infra.data?.healthy && problems === 0}
          title={infra.data?.healthy && problems === 0 ? 'Your agents are protected' : 'A few things need your attention'}
          detail={infra.data?.healthy && problems === 0
            ? 'Protection is active — every governed action is being checked before it runs. Last checked just now.'
            : `${problems} item${problems === 1 ? '' : 's'} to review — see the details below.`}
        />
      )}

      {/* Needs your attention — pulled to the top, prominent and actionable */}
      {problems > 0 && (
        <section className={`attention-panel ${failed > 0 ? 'urgent' : ''}`}>
          <div className="attention-head">
            <span className="status-icon">
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
                <path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9" /><path d="M13.7 21a2 2 0 0 1-3.4 0" />
              </svg>
            </span>
            <div>
              <div className="attention-title">Needs your attention</div>
              <p className="attention-sub">{problems} thing{problems === 1 ? '' : 's'} to look at right now.</p>
            </div>
            <a href="#/approvals" className="btn primary attention-cta">Review now</a>
          </div>
          <ul className="attention-list">
            {failed > 0 && <li>{failed} action{failed === 1 ? '' : 's'} recently failed to complete — <a href="#/activity">see what happened</a>.</li>}
            {stale > 0 && <li>{stale} sign-off{stale === 1 ? '' : 's'} waiting longer than a day — <a href="#/approvals">open approvals</a>.</li>}
          </ul>
        </section>
      )}

      {/* Is everything healthy? */}
      <section className="card">
        <b style={{ fontSize: 15 }}>Is everything healthy?</b>
        {infra.loading ? <Loading /> : (
          <div className="tiles" style={{ marginTop: 10 }}>
            <StatCard label="Overall" value={infra.data?.healthy ? 'All good' : 'Attention'} tone={infra.data?.healthy ? 'allow' : 'deny'} />
            {(infra.data?.checks ?? []).map((c) => (
              <StatCard key={c.name} label={CHECK_NAME[c.name] ?? c.name} value={c.healthy ? 'OK' : 'Down'} tone={c.healthy ? 'allow' : 'deny'} />
            ))}
          </div>
        )}
      </section>

      {/* Today's activity */}
      <section className="card">
        <b style={{ fontSize: 15 }}>Today’s activity</b>
        {metrics.loading ? <Loading /> : (
          <div className="tiles" style={{ marginTop: 10 }}>
            <StatCard label="Allowed" value={m?.todayAllowed ?? 0} tone="allow" caption="Actions that passed policy and ran." />
            <StatCard label="Need sign-off" value={m?.todayApproval ?? 0} tone="approval" caption="Waiting on a human decision." />
            <StatCard label="Stopped" value={m?.todayDenied ?? 0} tone="deny" caption="Unsafe actions blocked before running." />
            <StatCard label="Agents active" value={activeAgents} caption="Agents being governed right now." />
          </div>
        )}
      </section>

      {/* Azure infrastructure */}
      <section className="card">
        <b style={{ fontSize: 15 }}>Behind the scenes (Azure)</b>
        {infra.loading ? <Loading /> : inf && (
          <div className="tiles" style={{ marginTop: 10 }}>
            <StatCard label="Memory in use" value={`${inf.memoryMb} MB`} />
            <StatCard label="Running for" value={inf.uptimeHours >= 24 ? `${Math.round(inf.uptimeHours / 24)} days` : `${inf.uptimeHours} hrs`} />
            <StatCard label="Processors" value={inf.processors} />
            <StatCard label="Agents" value={`${countStage(rollout.data?.agents, 'ENFORCE')} live · ${settling} settling`} />
          </div>
        )}
        <p className="muted small" style={{ margin: '8px 0 0' }}>Hosted on Microsoft Azure.</p>
      </section>
    </div>
  )
}

function countStage(rows: RolloutRow[] | undefined, posture: string): number {
  return (rows ?? []).filter((r) => (r.posture || 'ENFORCE').toUpperCase() === posture).length
}
