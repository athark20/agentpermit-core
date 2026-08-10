import { useState } from 'react'
import { api, ApiError } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, KV, StatusPill, Loading } from '../components/ui'
import type { LicenseStatus } from '../types'

// Plain-language names for what each plan unlocks. Keys mirror the gateway's feature flags.
const FEATURE_NAMES: Record<string, string> = {
  '*': 'Everything in your plan',
  'connector-governance': 'App-connection governance',
  'observation-feeds': 'Activity feeds',
  'sso': 'Single sign-on (SSO)',
  'advanced-rbac': 'Advanced roles & access',
  'siem-export': 'Security log export (SIEM)',
  'ha': 'High availability',
}

function friendlyPlan(edition: string): string {
  if (edition.startsWith('Unlicensed (enforcement')) return 'Not enforced'
  if (edition.startsWith('Unlicensed')) return 'No active plan'
  return edition
}

// Turn an ApiError (ProblemDetails JSON) into one plain sentence.
function reason(e: unknown): string {
  if (e instanceof ApiError) {
    try {
      const p = JSON.parse(e.message)
      return p.detail || p.title || `Request failed (${e.status}).`
    } catch {
      return e.message || `Request failed (${e.status}).`
    }
  }
  return e instanceof Error ? e.message : String(e)
}

function fmtDate(iso?: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return isNaN(d.getTime()) ? '—' : d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
}

export function Plan() {
  const s = useAsync<LicenseStatus>(() => api('/api/admin/license'))
  const [token, setToken] = useState('')
  const [busy, setBusy] = useState(false)
  const [ok, setOk] = useState<string>()
  const [err, setErr] = useState<string>()

  const notEnforced = !!s.data && s.data.edition.startsWith('Unlicensed (enforcement')
  const locked = !!s.data && s.data.managementLocked

  async function activate() {
    setBusy(true); setOk(undefined); setErr(undefined)
    try {
      await api('/api/admin/license', { method: 'PUT', body: JSON.stringify({ token: token.trim() }) })
      setToken('')
      setOk('Your plan is now active.')
      await s.reload()
    } catch (e) {
      setErr(reason(e))
    } finally {
      setBusy(false)
    }
  }

  async function remove() {
    if (!window.confirm('Remove the current licence? Management will lock until a new licence is applied. Protection of what is already running is not affected.')) return
    setBusy(true); setOk(undefined); setErr(undefined)
    try {
      await api('/api/admin/license', { method: 'DELETE' })
      setOk('Licence removed.')
      await s.reload()
    } catch (e) {
      setErr(reason(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div>
      <PageHeader title="Your plan"
        subtitle="The plan this workspace is licensed for, and what it unlocks. Paste a licence key below to activate or change a plan — it takes effect immediately, with no restart." />

      {s.loading ? <Loading what="plan" /> : s.error ? <p className="error">{s.error}</p> : s.data && (
        <>
          {locked && (
            <div className="card" style={{ borderColor: '#f0c0c0', background: 'var(--deny-bg)' }}>
              <b>No active plan — management is locked.</b>
              <p className="muted small" style={{ margin: '6px 0 0' }}>
                You can still see everything, but adding or changing agents and settings is paused until a valid
                licence is applied. Protection of agents already running continues as normal.
              </p>
            </div>
          )}
          {notEnforced && (
            <div className="card" style={{ borderColor: 'var(--line)' }}>
              <b>Plan limits are currently turned off.</b>
              <p className="muted small" style={{ margin: '6px 0 0' }}>
                Everything is available in this workspace. Applying a licence takes effect once plan enforcement is
                switched on for this deployment.
              </p>
            </div>
          )}

          <div className="cols">
            <section className="card">
              <h2>Current plan</h2>
              <KV label="Plan"><b>{friendlyPlan(s.data.edition)}</b></KV>
              <KV label="Status">
                <StatusPill s={locked ? (s.data.expired ? 'EXPIRED' : 'SUSPENDED') : 'ACTIVE'} />
              </KV>
              <KV label="Licensed to">{s.data.customerId || '—'}</KV>
              <KV label="Agents allowed">{s.data.maxAgents == null ? 'Unlimited' : s.data.maxAgents}</KV>
              <KV label="Environments">{s.data.maxEnvironments || '—'}</KV>
              <KV label={s.data.expired ? 'Expired' : 'Renews / expires'}>{fmtDate(s.data.expiresUtc)}</KV>
            </section>

            <section className="card">
              <h2>What's included</h2>
              {s.data.features.length === 0
                ? <p className="muted small">Core governance only — no add-on features on this plan.</p>
                : s.data.features.map((f) => (
                    <div key={f} className="deliver"><span className="dot" />{FEATURE_NAMES[f] ?? f}</div>
                  ))}
            </section>
          </div>

          <section className="card">
            <h2>Apply a licence</h2>
            <p className="muted small" style={{ marginTop: 0 }}>
              Paste the licence key you were issued. We verify it's genuine, then activate the plan straight away.
            </p>
            <label>Licence key
              <textarea rows={4} value={token} placeholder="Paste the licence key here…"
                onChange={(e) => setToken(e.target.value)} style={{ fontFamily: 'var(--mono, monospace)' }} />
            </label>
            <div style={{ display: 'flex', gap: 10, alignItems: 'center', marginTop: 12 }}>
              <button className="btn primary" onClick={activate} disabled={busy || !token.trim()}>
                {busy ? 'Working…' : 'Activate plan'}
              </button>
              {!locked && !notEnforced && (
                <button className="btn danger" onClick={remove} disabled={busy}>Remove current licence</button>
              )}
            </div>
            {ok && <p className="success" style={{ marginBottom: 0 }}>{ok}</p>}
            {err && <p className="error" style={{ marginBottom: 0 }}>That licence wasn't accepted: {err}</p>}
          </section>

          <p className="muted note">
            Licence keys are issued by AgentPermit for your organisation. To upgrade, renew, or add agents, contact
            your AgentPermit representative for a new key and paste it here.
          </p>
        </>
      )}
    </div>
  )
}
