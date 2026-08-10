import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatusPill, Loading } from '../components/ui'

interface Check { name: string; status: string; description?: string; data?: Record<string, unknown> }
interface Health { status: string; totalDurationMs: number; checks: Check[] }

// Friendly labels for the gateway's dependency checks.
const LABELS: Record<string, string> = {
  'policy-snapshot': 'Policy snapshot',
  'adapters': 'Target adapters',
  'receipt-signing': 'Receipt signing (Key Vault)',
  'sql': 'Azure SQL',
}

export function SystemHealth() {
  const health = useAsync<Health>(() => api('/health/deps'))

  return (
    <div>
      <PageHeader title="System Health"
        subtitle="Live status of the gateway and its critical dependencies."
        actions={<button className="btn ghost" onClick={() => health.reload()}>Refresh</button>} />

      {health.loading ? <Loading /> : health.error ? <p className="error">{health.error}</p> : (
        <>
          <div className="card health-summary">
            <StatusPill s={health.data!.status} />
            <span className="muted">Overall — checked in {Math.round(health.data!.totalDurationMs)} ms</span>
          </div>

          <div className="tiles">
            {health.data!.checks.map((c) => (
              <div key={c.name} className="stat neutral static">
                <div className="stat-value sm"><StatusPill s={c.status} /></div>
                <div className="stat-label">{LABELS[c.name] ?? c.name}</div>
                {c.description && <div className="stat-sub">{c.description}</div>}
              </div>
            ))}
          </div>

          <p className="muted note">
            Per-target integration health, latency and circuit-breaker state activate with the Integration
            Health module.
          </p>
        </>
      )}
    </div>
  )
}
