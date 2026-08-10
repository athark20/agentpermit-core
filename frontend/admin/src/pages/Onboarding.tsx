import { useEffect, useMemo, useState } from 'react'
import { api, ApiError, reasonOf } from '../api'
import { useAsync } from '../hooks'
import { useToast } from '../components/Toast'
import { PageHeader, Loading, EmptyState, Drawer } from '../components/ui'
import type { OnboardingStatus, DiscoveredRow, Agent, TenantConnection } from '../types'

const STEPS = ['Connect', 'Add agents', 'Protect', 'Done']

export function Onboarding() {
  const status = useAsync<OnboardingStatus>(() => api('/api/admin/onboarding/status'))
  const agents = useAsync<Agent[]>(() => api('/api/admin/agents'))
  const [step, setStep] = useState(0)

  const connected = !!status.data?.connected
  // Skip past Connect automatically once we know the environment is connected.
  useEffect(() => { if (connected && step === 0) setStep(1) }, [connected]) // eslint-disable-line

  const go = (n: number) => setStep(Math.max(0, Math.min(STEPS.length - 1, n)))

  return (
    <div>
      <PageHeader title="Set up your agents"
        subtitle="A guided setup: connect your environment, bring your agents under control, and put the AgentPermit checkpoint in front of every risky action."
        actions={<button className="btn" onClick={() => { location.hash = '#/agents' }}>Skip to agents</button>} />

      <Stepper step={step} onJump={(n) => { if (n <= step) go(n) }} />

      {step === 0 && <ConnectStep status={status} onNext={() => go(1)} />}
      {step === 1 && <AddAgentsStep onNext={() => { agents.reload(); go(2) }} />}
      {step === 2 && <ProtectStep agents={agents} status={status} onNext={() => go(3)} onBack={() => go(1)} />}
      {step === 3 && <DoneStep agents={agents} />}
    </div>
  )
}

function Stepper({ step, onJump }: { step: number; onJump: (n: number) => void }) {
  return (
    <div className="wiz-steps">
      {STEPS.map((label, i) => (
        <button key={label} type="button" className={`wiz-step ${i === step ? 'current' : ''} ${i < step ? 'done' : ''}`}
          onClick={() => onJump(i)} disabled={i > step}>
          <span className="wiz-dot">{i < step
            ? <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={3} strokeLinecap="round" strokeLinejoin="round"><path d="M20 6 9 17l-5-5" /></svg>
            : i + 1}</span>
          {label}
        </button>
      ))}
    </div>
  )
}

// ---- Step 1: Connect ---------------------------------------------------------
function ConnectStep({ status, onNext }: { status: ReturnType<typeof useAsync<OnboardingStatus>>; onNext: () => void }) {
  const s = status.data
  return (
    <section className="card" style={{ borderLeft: `4px solid var(--${s?.connected ? 'allow' : 'deny'})` }}>
      {status.loading ? <Loading what="your environment" /> : s?.connected ? (
        <>
          <b style={{ fontSize: 15 }}>Connected to your Microsoft environment</b>
          <p className="muted small" style={{ margin: '4px 0 12px' }}>{s.environmentName || s.environmentUrl} · we can only read your agents, never their passwords or data.</p>
          <button className="btn primary" onClick={onNext}>Find my agents →</button>
        </>
      ) : (
        <>
          <b style={{ fontSize: 15 }}>Your Microsoft environment isn’t connected yet</b>
          <p className="muted small" style={{ margin: '4px 0 12px', maxWidth: 640 }}>
            {s?.configured ? `We couldn’t reach it: ${s?.message}` : 'Ask whoever set up AgentPermit to connect your Microsoft (Power Platform) environment — then your agents will show up here automatically.'}
          </p>
          <button className="btn" onClick={() => status.reload()}>Check again</button>
        </>
      )}
    </section>
  )
}

// ---- Step 2: Add agents ------------------------------------------------------
// The customer may run several separate tenants (Dev / UAT / Prod), each its own Entra directory.
// The tenant picker chooses which tenant to DISCOVER from, and becomes the agent's environment.
function AddAgentsStep({ onNext }: { onNext: () => void }) {
  const toast = useToast()
  const tenants = useAsync<TenantConnection[]>(() => api('/api/admin/onboarding/tenants'))
  const [tenant, setTenant] = useState<string>('')

  // Default to the first connected tenant (or the first listed) once tenants load.
  useEffect(() => {
    if (!tenant && tenants.data?.length) setTenant((tenants.data.find((t) => t.configured) ?? tenants.data[0]).key)
  }, [tenants.data]) // eslint-disable-line

  const conn = tenants.data?.find((t) => t.key === tenant)
  const qs = tenant ? `?tenant=${encodeURIComponent(tenant)}` : ''
  const status = useAsync<OnboardingStatus>(() => api(`/api/admin/onboarding/status${qs}`), [tenant])
  const found = useAsync<DiscoveredRow[]>(() => api(`/api/admin/onboarding/agents${qs}`), [tenant])

  const rows = found.data ?? []
  const addable = rows.filter((r) => !r.agent.alreadyImported)
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const [ids, setIds] = useState<Record<string, string>>({})   // manually-entered Entra app ids, keyed by botId
  const [busy, setBusy] = useState(false)
  const [connecting, setConnecting] = useState(false)
  const connected = !!status.data?.connected

  async function removeTenant() {
    if (!conn?.removable) return
    if (!confirm(`Remove the connection to “${conn.label}”? AgentPermit will stop discovering agents from this tenant. Agents already imported stay governed.`)) return
    try {
      await api(`/api/admin/onboarding/tenants/${encodeURIComponent(conn.key)}`, { method: 'DELETE' })
      toast.success('Tenant removed', `AgentPermit is no longer connected to ${conn.label}.`)
      setTenant(''); tenants.reload()
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not remove tenant', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    }
  }

  // The agent's bound identity: the one Microsoft provisioned, or the one the admin pasted in.
  const effId = (r: DiscoveredRow) => (r.agent.entraAgentId || ids[r.agent.botId] || '').trim()

  // Pre-select every addable agent that already has an identity; reset pasted ids when the tenant changes.
  useEffect(() => { setPicked(new Set(addable.filter((r) => r.agent.entraAgentId).map((r) => r.agent.botId))) }, [found.data]) // eslint-disable-line
  useEffect(() => { setIds({}) }, [tenant])
  const toggle = (id: string) => setPicked((s) => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n })
  const setId = (botId: string, v: string) => {
    setIds((m) => ({ ...m, [botId]: v }))
    setPicked((s) => { const n = new Set(s); v.trim() ? n.add(botId) : n.delete(botId); return n })   // typing an id opts the agent in
  }

  const chosen = useMemo(() => addable.filter((r) => picked.has(r.agent.botId)), [addable, picked])
  const ready = chosen.filter((r) => effId(r))
  const missingId = chosen.filter((r) => !effId(r))
  const anyImported = rows.some((r) => r.agent.alreadyImported)
  const label = conn?.label || 'this tenant'

  async function add() {
    if (ready.length === 0) { toast.warning('Nothing to add yet', 'Tick an agent that has an identity, or paste an agent’s Entra app id.'); return }
    setBusy(true)
    const payload = ready.map((r) => ({
      botId: r.agent.botId, name: r.agent.name, agentId: r.suggestedAgentId,
      entraClientId: effId(r), businessOwner: r.agent.owner, riskTier: 'Medium', environment: label,
    }))
    try {
      const res = await api<{ imported: string[]; rejected: { agentId: string; reason: string }[] }>('/api/admin/onboarding/import', { method: 'POST', body: JSON.stringify({ agents: payload }) })
      if (res.imported.length) toast.success(`Added ${res.imported.length} agent${res.imported.length === 1 ? '' : 's'} from ${label}`, 'They start in Learning mode — nothing risky is allowed while they settle in.')
      if (res.rejected.length) toast.warning('Some agents were skipped', res.rejected.map((x) => `${x.agentId}: ${x.reason}`).join(' · '))
      found.reload()
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not add agents', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    } finally { setBusy(false) }
  }

  return (
    <section className="card">
      {/* Which tenant to import from — Dev / UAT / Prod are separate tenants, each its own connection. */}
      <div className="import-scope">
        <label style={{ margin: 0 }}>Import from tenant
          {tenants.loading ? <div className="muted small" style={{ marginTop: 6 }}>Loading tenants…</div> : (tenants.data?.length ? (
            <select value={tenant} onChange={(e) => setTenant(e.target.value)} style={{ minWidth: 220 }}>
              {tenants.data.map((t) => <option key={t.key} value={t.key}>{t.label}{t.configured ? '' : ' — not connected'}</option>)}
            </select>
          ) : <div className="muted small" style={{ marginTop: 6 }}>No tenants connected yet.</div>)}
        </label>
        <div style={{ textAlign: 'right' }}>
          <span className={`pill ${connected ? 'allow' : status.loading ? 'neutral' : 'deny'}`}>
            {status.loading ? 'Checking…' : connected ? 'Connected' : 'Not connected'}
          </span>
          {status.data?.environmentUrl && <div className="muted small" style={{ marginTop: 4 }}>{status.data.environmentName || status.data.environmentUrl}</div>}
        </div>
      </div>

      <div className="row" style={{ gap: 12, margin: '0 0 6px' }}>
        <button className="btn ghost" onClick={() => setConnecting(true)}>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.4} strokeLinecap="round"><path d="M12 5v14M5 12h14" /></svg>
          Connect a tenant
        </button>
        {conn?.removable && <button className="btn ghost" onClick={removeTenant}>Remove this tenant</button>}
      </div>

      {connecting && <ConnectTenantDrawer onClose={() => setConnecting(false)}
        onSaved={(key) => { setConnecting(false); setTenant(key); tenants.reload() }} />}

      {/* Selected tenant isn't reachable — tell the admin exactly what to do, don't just show an empty list. */}
      {!status.loading && !connected && tenant ? (
        <div className="notice-warn">
          <b>This tenant isn’t connected yet.</b>
          <p className="muted small" style={{ margin: '4px 0 0' }}>
            {status.data?.message || 'AgentPermit can’t reach it.'} To govern its agents, register AgentPermit in this tenant
            (app registration + one-time admin consent + a Dataverse application user), then <b>Refresh</b>. Each tenant is a
            separate Microsoft directory, so Dev, UAT and Prod each need their own connection.
          </p>
          <button className="btn" style={{ marginTop: 10 }} onClick={() => { status.reload(); found.reload() }}>Check again</button>
        </div>
      ) : (<>
      <div className="row spread" style={{ marginTop: 4 }}>
        <b style={{ fontSize: 15 }}>{found.loading ? 'Looking for your agents…' : `We found ${rows.length} agent${rows.length === 1 ? '' : 's'} in ${label}`}</b>
        <button className="btn ghost" onClick={() => found.reload()}>Refresh</button>
      </div>
      {found.loading ? <Loading what="agents" /> : (rows.length ? (
        <>
          {addable.length > 0 && (
            <div className="row" style={{ margin: '4px 0 10px', gap: 12 }}>
              <button className="btn ghost" onClick={() => setPicked(new Set(addable.filter((r) => effId(r)).map((r) => r.agent.botId)))}>Select all ready</button>
              <button className="btn ghost" onClick={() => setPicked(new Set())}>Select none</button>
              <span className="muted small" style={{ marginLeft: 'auto' }}>{ready.length} ready to add</span>
            </div>
          )}
          <table className="table">
            <thead><tr><th style={{ width: 34 }}></th><th>Agent</th><th>Owner</th><th>Identity</th><th>Status</th></tr></thead>
            <tbody>
              {rows.map((r) => {
                const done = r.agent.alreadyImported
                const discovered = !!r.agent.entraAgentId
                const id = effId(r)
                return (
                  <tr key={r.agent.botId}>
                    <td><input type="checkbox" aria-label={`Select ${r.agent.name}`} style={{ width: 'auto', margin: 0 }} disabled={done} checked={picked.has(r.agent.botId)} onChange={() => toggle(r.agent.botId)} /></td>
                    <td><b>{r.agent.name}</b></td>
                    <td>{r.agent.owner || '—'}</td>
                    <td>
                      {done ? <span className="muted small mono">{r.agent.entraAgentId ? shortId(r.agent.entraAgentId) : 'bound'}</span>
                        : discovered ? <span className="pill allow" title={r.agent.entraAgentId!}>Identity found</span>
                        : <input className="id-input" aria-label={`Entra app id for ${r.agent.name}`} placeholder="Paste the agent’s Entra app id" value={ids[r.agent.botId] ?? ''} onChange={(e) => setId(r.agent.botId, e.target.value)} />}
                    </td>
                    <td>{done ? <span className="pill allow">Added</span> : id ? <span className="muted small">Ready to add</span> : <span className="pill approval">Needs an identity</span>}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          {missingId.length > 0 && (
            <p className="muted small" style={{ marginTop: 8 }}>
              {missingId.length} selected agent{missingId.length === 1 ? '' : 's'} still need{missingId.length === 1 ? 's' : ''} an Entra app id.
              Either wait for Microsoft to provision it (then <b>Refresh</b>), or paste it above — it’s the app (client) id of the agent’s <span className="mono">“… (Microsoft Copilot Studio)”</span> identity in Entra.
            </p>
          )}
          <div className="row" style={{ marginTop: 14, gap: 10 }}>
            <button className="btn primary" disabled={busy || ready.length === 0} onClick={add}>
              {busy ? 'Adding…' : ready.length === 0 ? 'Select an agent to add' : `Add ${ready.length} agent${ready.length === 1 ? '' : 's'} from ${label}`}
            </button>
            <button className="btn" style={{ marginLeft: 'auto' }} disabled={!anyImported} onClick={onNext}>Next: protect them →</button>
          </div>
        </>
      ) : <EmptyState title="No agents found yet" hint="Once you have AI agents in this tenant, they’ll appear here." />)}
      </>)}
    </section>
  )
}

function shortId(id: string) { return id.length > 12 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id }

// Self-serve "connect another tenant": prerequisites + details + a live test before it's saved.
type TestState = { state: 'idle' | 'testing' | 'ok' | 'fail'; message?: string; org?: string }
function ConnectTenantDrawer({ onClose, onSaved }: { onClose: () => void; onSaved: (key: string) => void }) {
  const toast = useToast()
  const [f, setF] = useState({ label: '', environmentUrl: '', tenantId: '', clientId: '', clientSecret: '' })
  const [test, setTest] = useState<TestState>({ state: 'idle' })
  const [saving, setSaving] = useState(false)
  const set = (k: keyof typeof f) => (e: React.ChangeEvent<HTMLInputElement>) => { setF({ ...f, [k]: e.target.value }); setTest({ state: 'idle' }) }
  const ready = f.label && f.environmentUrl && f.tenantId && f.clientId && f.clientSecret

  async function runTest() {
    if (!ready) { toast.warning('Fill in every field', 'Label, environment URL, tenant id, client id and client secret are all needed to test.'); return }
    setTest({ state: 'testing' })
    try {
      const s = await api<OnboardingStatus>('/api/admin/onboarding/tenants/test', { method: 'POST', body: JSON.stringify(f) })
      setTest(s.connected ? { state: 'ok', org: s.organizationId ?? undefined, message: s.environmentName || s.environmentUrl }
        : { state: 'fail', message: s.message ?? 'Could not reach this tenant.' })
    } catch (e) { setTest({ state: 'fail', message: reasonOf(e) }) }
  }

  async function save() {
    setSaving(true)
    try {
      const res = await api<{ key: string; label: string }>('/api/admin/onboarding/tenants', { method: 'POST', body: JSON.stringify(f) })
      toast.success(`Connected ${res.label}`, 'You can now import and govern this tenant’s agents.')
      onSaved(res.key)
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      setTest({ state: 'fail', message: reasonOf(e) })
      toast.error(err?.title ?? 'Could not connect tenant', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    } finally { setSaving(false) }
  }

  return (
    <Drawer title="Connect a tenant" onClose={onClose}
      footer={<div className="row" style={{ margin: 0, gap: 10 }}>
        <button className="btn" onClick={runTest} disabled={test.state === 'testing' || saving}>{test.state === 'testing' ? 'Testing…' : 'Test connection'}</button>
        <button className="btn primary" onClick={save} disabled={test.state !== 'ok' || saving}>{saving ? 'Saving…' : 'Save tenant'}</button>
      </div>}>
      <p className="muted small" style={{ marginTop: 0 }}>
        Each tenant is a separate Microsoft directory, so it needs its own one-time setup. Do these three things in the
        <b> new tenant</b>, then paste the details below.
      </p>
      <ol className="wiz-guide" style={{ marginBottom: 16 }}>
        <li><b>Register an app</b> in the tenant — <a href="https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade" target="_blank" rel="noreferrer">Entra → App registrations</a>. Copy its <b>Application (client) id</b> and <b>Directory (tenant) id</b>, and create a <b>client secret</b>.</li>
        <li><b>Grant admin consent</b> for Dynamics CRM / Dataverse <span className="mono">user_impersonation</span> (or <span className="mono">.default</span>) on that app.</li>
        <li><b>Add the app as an application user</b> with a security role — <a href="https://admin.powerplatform.microsoft.com" target="_blank" rel="noreferrer">Power Platform admin center → your environment → Settings → Application users</a>.</li>
      </ol>
      <p className="muted small" style={{ margin: '2px 0 8px' }}>All fields are required <span className="req" aria-hidden="true">*</span></p>
      <label>Name for this tenant <span className="req" aria-hidden="true">*</span>
        <input value={f.label} onChange={set('label')} required aria-required="true" placeholder="Accura Bank — Prod" /></label>
      <label>Dataverse environment URL <span className="req" aria-hidden="true">*</span>
        <input type="url" inputMode="url" spellCheck={false} autoComplete="off" value={f.environmentUrl} onChange={set('environmentUrl')} required aria-required="true" placeholder="https://orgXXXX.crm4.dynamics.com" /></label>
      <label>Directory (tenant) id <span className="req" aria-hidden="true">*</span>
        <input spellCheck={false} autoComplete="off" value={f.tenantId} onChange={set('tenantId')} required aria-required="true" placeholder="00000000-0000-0000-0000-000000000000" /></label>
      <label>Application (client) id <span className="req" aria-hidden="true">*</span>
        <input spellCheck={false} autoComplete="off" value={f.clientId} onChange={set('clientId')} required aria-required="true" placeholder="00000000-0000-0000-0000-000000000000" /></label>
      <label>Client secret <span className="req" aria-hidden="true">*</span>
        <input type="password" value={f.clientSecret} onChange={set('clientSecret')} required aria-required="true" placeholder="the app's client secret value" autoComplete="new-password" /></label>

      <div aria-live="polite">
        {test.state === 'ok' && (
          <p className="ok" role="status" style={{ marginTop: 10 }}>
            <b>Connected.</b> Reached {test.message}{test.org ? ` (org ${shortId(test.org)})` : ''}. You can save this tenant now.
          </p>
        )}
        {test.state === 'fail' && <p className="error" role="alert" style={{ marginTop: 10 }}>{test.message}</p>}
      </div>
      <p className="muted small" style={{ marginTop: 14 }}>
        The secret is used to connect and is stored in your own gateway’s database (encrypted at rest); it’s never shown again
        and never sent back to this screen.
      </p>
    </Drawer>
  )
}

// ---- Step 3: Protect (the connector-wall setup) -----------------------------
function ProtectStep({ agents, status, onNext, onBack }: {
  agents: ReturnType<typeof useAsync<Agent[]>>; status: ReturnType<typeof useAsync<OnboardingStatus>>; onNext: () => void; onBack: () => void
}) {
  const list = agents.data ?? []
  return (
    <>
      <section className="card">
        <b style={{ fontSize: 15 }}>Put the checkpoint in front of every risky action</b>
        <p className="muted small" style={{ margin: '4px 0 0', maxWidth: 760 }}>
          Your agents are added — but for AgentPermit to actually govern them, each agent must call AgentPermit
          right before it does something real (a payment, a record change, an access grant). Do these two things per agent,
          then lock the door so an agent can’t skip the checkpoint.
        </p>
      </section>

      {agents.loading ? <Loading what="your agents" /> : (list.length ? (
        <div className="stack">
          {list.map((a) => <AgentSetupCard key={a.agentId} agent={a} />)}
        </div>
      ) : <EmptyState title="No agents added yet" hint="Go back a step and add your agents first." />)}

      <LockDownCard status={status} />

      <div className="row" style={{ marginTop: 16, gap: 10 }}>
        <button className="btn ghost" onClick={onBack}>← Back</button>
        <button className="btn primary" style={{ marginLeft: 'auto' }} onClick={onNext}>Finish setup →</button>
      </div>
    </>
  )
}

function AgentSetupCard({ agent }: { agent: Agent }) {
  const [open, setOpen] = useState(false)
  const [done, setDone] = useState(false)
  const name = agent.displayName || agent.agentId
  return (
    <section className={`card ${done ? '' : ''}`} style={{ borderLeft: `4px solid var(--${done ? 'allow' : 'stepup'})` }}>
      <div className="row spread" style={{ marginTop: 0, alignItems: 'center' }}>
        <div>
          <b style={{ fontSize: 15 }}>{name}</b>
          <div className="muted small" style={{ marginTop: 3 }}>{done ? 'Protection set up.' : 'Add the checkpoint in Copilot Studio, then write its rules.'}</div>
        </div>
        <label className="check" style={{ margin: 0 }}>
          <input type="checkbox" checked={done} onChange={(e) => setDone(e.target.checked)} /> <span className="small">Mark set up</span>
        </label>
      </div>

      <div className="row" style={{ gap: 10, marginTop: 12 }}>
        <button className="btn" onClick={() => setOpen((o) => !o)}>{open ? 'Hide steps' : 'Show me where to add the checkpoint'}</button>
        <a className="btn ghost" href={`#/integration/${agent.agentId}`}>Open integration guide</a>
        <a className="btn ghost" href="#/rules">Write rules for this agent</a>
      </div>

      {open && (
        <ol className="wiz-guide">
          <li><b>Open the agent in Copilot Studio</b> and find the step where it does the real action (calls the finance / HR / core system).</li>
          <li><b>Insert an action just before it</b> → choose the <b>AgentPermit</b> connector → <b>Request an action</b>.</li>
          <li><b>Fill it in:</b> the action id (e.g. <span className="mono">Payment.Issue</span>), the details, and a unique <span className="mono">idempotencyKey</span>. Leave <span className="mono">agentId</span> blank — it’s taken from the sign-in.</li>
          <li><b>Branch on the result:</b> on <span className="pill allow">Allow</span> continue; on <span className="pill approval">Ask</span> tell the user it’s sent for sign-off and stop; on <span className="pill deny">Deny</span> tell them it isn’t allowed.</li>
          <li>Keep the real action <b>inside the Allow branch only</b> — that’s the wall.</li>
        </ol>
      )}
    </section>
  )
}

function LockDownCard({ status }: { status: ReturnType<typeof useAsync<OnboardingStatus>> }) {
  const toast = useToast()
  const [env, setEnv] = useState('PROD')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)

  async function lock() {
    setBusy(true)
    try {
      await api('/api/admin/dlp/generate', { method: 'POST', body: JSON.stringify({ environment: env, connectors: [] }) })
      setDone(true)
      toast.success('Zero-bypass protection generated', `A data-loss policy for ${env} is ready — your agents must go through AgentPermit.`)
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not generate protection', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    } finally { setBusy(false) }
  }

  return (
    <section className="card zerobypass" style={{ marginTop: 16 }}>
      <b style={{ fontSize: 15 }}>Lock the door — no bypass</b>
      <p className="muted small" style={{ margin: '4px 0 12px', maxWidth: 760 }}>
        Generate a data-loss policy that stops your agents reaching the target systems any way <i>except</i> through
        AgentPermit. Governance you can bypass isn’t governance.
      </p>
      <div className="row" style={{ gap: 10, alignItems: 'flex-end' }}>
        <label style={{ margin: 0 }}>Environment
          <select value={env} onChange={(e) => setEnv(e.target.value)} style={{ minWidth: 140 }}>
            <option>DEV</option><option>UAT</option><option>PROD</option>
          </select>
        </label>
        <button className="btn primary" disabled={busy || !status.data?.connected} onClick={lock}>
          {busy ? 'Generating…' : done ? 'Regenerate protection' : 'Generate zero-bypass protection'}
        </button>
        {done && <span className="pill allow" style={{ marginLeft: 'auto' }}>Ready</span>}
      </div>
      {!status.data?.connected && <p className="muted small" style={{ marginTop: 8 }}>Connect your environment first to generate this.</p>}
    </section>
  )
}

// ---- Step 4: Done ------------------------------------------------------------
function DoneStep({ agents }: { agents: ReturnType<typeof useAsync<Agent[]>> }) {
  const n = (agents.data ?? []).length
  return (
    <section className="card" style={{ textAlign: 'center', padding: '40px 24px' }}>
      <div style={{ width: 56, height: 56, margin: '0 auto 14px', display: 'grid', placeItems: 'center', borderRadius: 16, background: 'var(--allow-bg)', color: '#15803d' }}>
        <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.4} strokeLinecap="round" strokeLinejoin="round"><path d="M20 6 9 17l-5-5" /></svg>
      </div>
      <h2 style={{ fontSize: 20 }}>You’re set up</h2>
      <p className="muted" style={{ maxWidth: 520, margin: '6px auto 18px' }}>
        {n} agent{n === 1 ? '' : 's'} are under AgentPermit, in Learning mode. Write the rules that decide what needs a
        human sign-off, then move them to enforcing when you’re ready.
      </p>
      <div className="row" style={{ justifyContent: 'center', gap: 10 }}>
        <a className="btn primary" href="#/rules">Write your rules</a>
        <a className="btn" href="#/agents">See your agents</a>
      </div>
    </section>
  )
}
