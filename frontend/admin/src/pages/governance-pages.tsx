import { useState } from 'react'
import { api, ApiError } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatCard, Loading, EmptyState } from '../components/ui'

const TIER_CLS: Record<string, string> = { LOW: 'allow', MEDIUM: 'approval', HIGH: 'deny', CRITICAL: 'deny' }
function TierPill({ tier }: { tier: string }) {
  return <span className={`pill ${TIER_CLS[(tier || '').toUpperCase()] ?? 'neutral'}`}>{tier || '—'}</span>
}

// ---------------------------------------------------------------- Kill switch + circuit breaker

interface Safety {
  killSwitch: { frozen: boolean; reason?: string; actor?: string; sinceUtc?: string }
  circuitBreaker: { enabled: boolean; maxExecutionsPerWindow: number; windowSeconds: number }
}

export function SafetyControls() {
  const s = useAsync<Safety>(() => api('/api/admin/safety'))
  const [busy, setBusy] = useState(false)

  async function freeze() {
    const reason = prompt('Reason for freezing ALL action requests?', 'Manual freeze')
    if (reason === null) return
    setBusy(true)
    try { await api('/api/admin/safety/freeze', { method: 'POST', body: JSON.stringify({ reason }) }); s.reload() }
    catch (e) { alert(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e)) }
    finally { setBusy(false) }
  }
  async function unfreeze() {
    setBusy(true)
    try { await api('/api/admin/safety/unfreeze', { method: 'POST' }); s.reload() }
    catch (e) { alert(String(e)) } finally { setBusy(false) }
  }

  const k = s.data?.killSwitch
  const cb = s.data?.circuitBreaker
  return (
    <div>
      <PageHeader title="Kill switch & circuit breaker" subtitle="The always-on safety net — enforced before policy, never warms up" />
      {s.loading ? <Loading what="safety" /> : (
        <>
          <section className={`card ${k?.frozen ? 'frozen-banner' : ''}`} style={{ borderLeft: `4px solid var(--${k?.frozen ? 'deny' : 'allow'})` }}>
            <div className="row" style={{ margin: 0, alignItems: 'center' }}>
              <div>
                <h3 style={{ margin: '0 0 4px' }}>System is {k?.frozen ? 'FROZEN' : 'live'}</h3>
                <p className="muted small" style={{ margin: 0 }}>
                  {k?.frozen
                    ? `All action requests are denied (SYSTEM_FROZEN)${k.reason ? ` — ${k.reason}` : ''}${k.actor ? ` · by ${k.actor}` : ''}`
                    : 'Action requests are evaluated normally.'}
                </p>
              </div>
              <div style={{ marginLeft: 'auto' }}>
                {k?.frozen
                  ? <button className="btn" disabled={busy} onClick={unfreeze}>Lift freeze</button>
                  : <button className="btn danger" disabled={busy} onClick={freeze}>Freeze all requests</button>}
              </div>
            </div>
          </section>
          <div className="tiles">
            <StatCard label="Circuit breaker" value={cb?.enabled ? 'On' : 'Off'} tone={cb?.enabled ? 'allow' : 'neutral'} />
            <StatCard label="Max executions / window" value={cb?.maxExecutionsPerWindow ?? '—'} sub="per agent" />
            <StatCard label="Window" value={`${cb?.windowSeconds ?? '—'}s`} />
          </div>
          <p className="muted small" style={{ padding: '0 4px' }}>
            The circuit breaker caps how many actions an agent can execute per window (runaway protection) and
            trips CIRCUIT_BREAKER. Both controls override policy and warm-up.
          </p>
        </>
      )}
    </div>
  )
}

// ---------------------------------------------------------------- Observation feeds

interface Feed {
  name: string; source: string; mode: string; enabled: boolean
  lastRunUtc?: string; totalIngested: number; lastBatch: number; lastError?: string
}

export function Feeds() {
  const f = useAsync<Feed[]>(() => api('/api/admin/observations/feeds'))
  return (
    <div>
      <PageHeader title="Observation feeds" subtitle="Where rollout evidence comes from — gateway is always on; external feeds are opt-in" />
      <section className="card">
        {f.loading ? <Loading what="feeds" /> : (f.data?.length ? (
          <table className="table">
            <thead><tr><th>Feed</th><th>Source</th><th>Mode</th><th>Status</th><th>Ingested</th><th>Last run</th><th>Error</th></tr></thead>
            <tbody>
              {f.data.map((x) => (
                <tr key={x.name}>
                  <td><b>{x.name}</b></td>
                  <td className="muted">{x.source}</td>
                  <td className="muted">{x.mode}</td>
                  <td><span className={`pill ${x.enabled ? 'allow' : 'neutral'}`}>{x.enabled ? 'Enabled' : 'Off'}</span></td>
                  <td>{x.totalIngested}</td>
                  <td className="muted small">{x.lastRunUtc ? new Date(x.lastRunUtc).toLocaleString() : '—'}</td>
                  <td className="muted small">{x.lastError ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No feeds" />)}
      </section>
      <p className="muted small" style={{ padding: '0 4px' }}>
        External feeds (Purview audit, Power Automate) are free APIs but need M365/Power Platform licensing;
        they stay off until configured per tenant, so no external cost is incurred implicitly.
      </p>
    </div>
  )
}

// ---------------------------------------------------------------- Connector risk catalog

interface CatalogEntry { match: string; tier: string; categories: string[]; reason: string }
interface Catalog { version: string; count: number; entries: CatalogEntry[] }

export function RiskCatalog() {
  const c = useAsync<Catalog>(() => api('/api/admin/risk-catalog'))
  const [q, setQ] = useState('')
  const [probe, setProbe] = useState<{ tier: string; source: string; reasons: string[] }>()

  async function classify() {
    if (!q.trim()) return
    try { setProbe(await api(`/api/admin/risk-catalog/classify?connector=${encodeURIComponent(q.trim())}`)) }
    catch (e) { alert(String(e)) }
  }

  return (
    <div>
      <PageHeader title="Connector risk" subtitle={`Catalog v${c.data?.version ?? '…'} · ${c.data?.count ?? 0} connectors`} />
      <section className="card">
        <div className="row" style={{ margin: '0 0 12px' }}>
          <input className="search" type="search" aria-label="Connector id to classify" placeholder="Classify a connector id, e.g. shared_sql" value={q}
            onChange={(e) => setQ(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && classify()} />
          <button className="btn" onClick={classify}>Classify</button>
          {probe && <span style={{ marginLeft: 8 }}><TierPill tier={probe.tier} /> <span className="muted small">{probe.source} — {probe.reasons[0]}</span></span>}
        </div>
        {c.loading ? <Loading what="catalog" /> : (
          <table className="table">
            <thead><tr><th>Connector match</th><th>Tier</th><th>Categories</th><th>Reason</th></tr></thead>
            <tbody>
              {c.data?.entries.map((e) => (
                <tr key={e.match}>
                  <td className="mono">{e.match}</td>
                  <td><TierPill tier={e.tier} /></td>
                  <td className="muted small">{e.categories.join(', ') || '—'}</td>
                  <td className="muted small">{e.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  )
}

// ---------------------------------------------------------------- DLP generator

interface Decision { connector: string; tier: string; source: string; group: string; reviewRequired: boolean; reason: string }
interface DlpResult { environment: string; catalogVersion: string; defaultGroup: string; decisions: Decision[]; policyDocument: unknown }

export function DlpGenerator() {
  const [env, setEnv] = useState('AgentPermit DEV')
  const [text, setText] = useState('shared_sql\nshared_office365\nshared_teams\nshared_stripe')
  const [result, setResult] = useState<DlpResult>()
  const [busy, setBusy] = useState(false)

  async function generate() {
    const connectors = text.split(/[\s,]+/).map((s) => s.trim()).filter(Boolean)
    setBusy(true)
    try { setResult(await api('/api/admin/dlp/generate', { method: 'POST', body: JSON.stringify({ environment: env, connectors }) })) }
    catch (e) { alert(e instanceof ApiError ? `${e.status}: ${e.message}` : String(e)) }
    finally { setBusy(false) }
  }

  return (
    <div>
      <PageHeader title="DLP generator" subtitle="Generate a zero-bypass Power Platform DLP policy from an environment's connectors" />
      <section className="card">
        <label>Environment<input value={env} onChange={(e) => setEnv(e.target.value)} /></label>
        <label>Connectors (one per line or comma-separated)
          <textarea rows={4} value={text} onChange={(e) => setText(e.target.value)} style={{ width: '100%', fontFamily: 'monospace' }} />
        </label>
        <button className="btn primary" disabled={busy} onClick={generate}>Generate DLP policy</button>
      </section>

      {result && (
        <>
          <section className="card">
            <h3 style={{ marginTop: 0 }}>Decisions <span className="muted small">· catalog v{result.catalogVersion} · default {result.defaultGroup}</span></h3>
            <table className="table">
              <thead><tr><th>Connector</th><th>Tier</th><th>Group</th><th>Review</th><th>Reason</th></tr></thead>
              <tbody>
                {result.decisions.map((d) => (
                  <tr key={d.connector}>
                    <td className="mono">{d.connector}</td>
                    <td><TierPill tier={d.tier} /></td>
                    <td><span className={`pill ${d.group === 'BUSINESS' ? 'allow' : 'deny'}`}>{d.group}</span></td>
                    <td>{d.reviewRequired ? <span className="pill approval">Review</span> : '—'}</td>
                    <td className="muted small">{d.reason}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>
          <section className="card">
            <h3 style={{ marginTop: 0 }}>Policy document (deploy to Power Platform)</h3>
            <pre className="mono" style={{ whiteSpace: 'pre-wrap', fontSize: 12 }}>{JSON.stringify(result.policyDocument, null, 2)}</pre>
          </section>
        </>
      )}
    </div>
  )
}
