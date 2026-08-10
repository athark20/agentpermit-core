import { useMemo, useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { useToast } from '../components/Toast'
import { PageHeader, Loading, EmptyState, CopyChip, DecisionPill } from '../components/ui'
import { prettyAction } from '../plain'
import { DOMAIN_LABEL, DOMAIN_ORDER } from './Rules'
import type { Agent, BusinessAction } from '../types'

interface Integ {
  connectorBaseUrl: string | null
  identityHeader: string
  requestAction: { method: string; path: string }
  getActionStatus: { method: string; path: string }
  appInsightsConnectionString?: string | null
}
interface RawRule { actionId?: string; conditions?: { field: string; operator: string; value: unknown }[] }
interface Active { rules: RawRule[] }

// Which catalog actions carry a rule scoped to THIS agent (agentId equals / one-of).
function scopedActionIds(rules: RawRule[], agentId: string): string[] {
  const ids = new Set<string>()
  for (const r of rules) {
    const hit = (r.conditions ?? []).some((c) => c.field === 'agentId' && (
      (c.operator === 'equals' && String(c.value) === agentId) ||
      (c.operator === 'in' && Array.isArray(c.value) && (c.value as unknown[]).map(String).includes(agentId))
    ))
    if (hit && r.actionId) ids.add(r.actionId)
  }
  return [...ids]
}

export function IntegrationGuide({ sub, navigate }: { sub?: string; navigate: (id: string) => void }) {
  const toast = useToast()
  const agents = useAsync<Agent[]>(() => api('/api/admin/agents'))
  const integ = useAsync<Integ>(() => api('/api/admin/onboarding/integration'))
  const catalog = useAsync<BusinessAction[]>(() => api('/api/admin/actions'))
  const active = useAsync<Active>(() => api('/api/policies/active'))
  const [picked, setPicked] = useState(sub ?? '')
  const [showAll, setShowAll] = useState(false)

  const list = agents.data ?? []
  const agent = list.find((a) => a.agentId === picked) ?? list[0]

  const actLabel = (id: string) => prettyAction(id, catalog.data?.find((a) => a.actionId === id)?.displayName)
  const scoped = useMemo(() => agent ? scopedActionIds(active.data?.rules ?? [], agent.agentId) : [], [active.data, agent])

  // Full catalog grouped by domain, for the reference list.
  const groups = useMemo(() => {
    const by = new Map<string, BusinessAction[]>()
    for (const a of catalog.data ?? []) { const d = a.domain || 'Other'; (by.get(d) ?? by.set(d, []).get(d)!).push(a) }
    return [...by.entries()]
      .sort((x, y) => (DOMAIN_ORDER.indexOf(x[0]) + 1 || 99) - (DOMAIN_ORDER.indexOf(y[0]) + 1 || 99))
      .map(([d, l]) => [d, [...l].sort((p, q) => actLabel(p.actionId).localeCompare(actLabel(q.actionId)))] as const)
  }, [catalog.data])

  const base = integ.data?.connectorBaseUrl
  const requestUrl = base ? `${base}${integ.data!.requestAction.path}` : integ.data?.requestAction.path

  // The whole wiring as a plain-text checklist a maker can paste into a ticket / notes.
  function buildChecklist(): string {
    const idHeader = integ.data?.identityHeader ?? 'X-Agent-Client-Id'
    const firstAction = scoped[0] ?? 'Payment.Issue'
    const actionLines = scoped.length
      ? scoped.map((id) => `    - ${actLabel(id)}  (${id})`).join('\n')
      : '    - (no agent-specific rules yet; global rules still apply)'
    return [
      `AgentPermit integration — ${agent?.displayName || agent?.agentId}  (${agent?.agentId})`,
      ``,
      `1) CONNECT`,
      `   - In Copilot Studio, add an action -> the AgentPermit connector.`,
      `   - Connector endpoint: ${requestUrl ?? '(set Integration:ConnectorBaseUrl on the gateway)'}`,
      `   - Sign in AS THIS AGENT. Bound app id: ${agent?.entraClientId || '(not bound yet — register the agent first)'}`,
      ``,
      `2) INSERT "Request an action" immediately before the real step`,
      `   - actionId: ${firstAction}`,
      `   - parameters.*: the action's fields (e.g. amount, currency)`,
      `   - idempotencyKey: unique per request, >= 8 chars (a GUID). A retry with the same key is de-duplicated.`,
      `   - Leave agentId blank — the gateway derives it from the sign-in (header ${idHeader}).`,
      ``,
      `3) BRANCH on the "decision" output`,
      `   - ALLOW -> do the real action (keep it in this branch only).`,
      `   - REQUIRE_APPROVAL -> tell the user it's sent for sign-off and STOP; a person approves, then it runs.`,
      `   - DENY -> tell the user it isn't allowed; do not act.`,
      ``,
      `Action ids for this agent:`,
      actionLines,
    ].join('\n')
  }
  async function copyChecklist() {
    try { await navigator.clipboard.writeText(buildChecklist()); toast.success('Checklist copied', 'Paste it into your notes or a ticket for the maker.') }
    catch { toast.error('Couldn’t copy', 'Your browser blocked clipboard access.') }
  }

  if (agents.loading) return <div><PageHeader title="Integration guide" /><Loading what="your agents" /></div>

  return (
    <div>
      <PageHeader title="Integration guide"
        subtitle="Everything a maker needs to wire this agent into Copilot Studio — the exact connector inputs, ready to copy."
        actions={<>
          {list.length > 0 && <button className="btn primary" onClick={copyChecklist}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.9} strokeLinecap="round" strokeLinejoin="round"><rect x="9" y="9" width="11" height="11" rx="2" /><path d="M5 15V5a2 2 0 0 1 2-2h10" /></svg>
            Copy setup checklist
          </button>}
          <button className="btn" onClick={() => navigate('onboarding')}>Back to setup</button>
        </>} />

      {list.length === 0 ? (
        <EmptyState title="No agents yet" hint="Add an agent first, then come back for its integration guide." />
      ) : (<>
        <section className="card">
          <label style={{ margin: 0, maxWidth: 460 }}>Show the guide for
            <select value={agent?.agentId ?? ''} onChange={(e) => setPicked(e.target.value)}>
              {list.map((a) => <option key={a.agentId} value={a.agentId}>{a.displayName || a.agentId}</option>)}
            </select>
          </label>
        </section>

        {/* 1 — Connect */}
        <section className="card guide-step">
          <div className="guide-num">1</div>
          <div className="guide-body">
            <b style={{ fontSize: 15 }}>Add the connection in Copilot Studio</b>
            <p className="muted small" style={{ margin: '4px 0 12px', maxWidth: 720 }}>
              In the agent, add an action → pick the <b>AgentPermit</b> connector. It points at your APIM front door.
              Sign in <b>as this agent’s identity</b> when prompted — that’s what proves who’s calling.
            </p>
            <div className="kvline"><span>Connector endpoint</span>
              {base ? <CopyChip text={requestUrl!} /> : <span className="muted small">Set <span className="mono">Integration:ConnectorBaseUrl</span> (or <span className="mono">Teams:ApprovalsBaseUrl</span>) on the gateway to show this.</span>}
            </div>
            <div className="kvline"><span>This agent’s bound identity</span>
              {agent?.entraClientId
                ? <CopyChip text={agent.entraClientId} />
                : <span className="pill approval">No identity bound yet — register it first</span>}
            </div>
            <p className="muted small" style={{ margin: '2px 0 0' }}>The sign-in identity must match this app id, or the gateway rejects the call.</p>
          </div>
        </section>

        {/* 2 — Insert the checkpoint */}
        <section className="card guide-step">
          <div className="guide-num">2</div>
          <div className="guide-body">
            <b style={{ fontSize: 15 }}>Insert “Request an action” before the real step</b>
            <p className="muted small" style={{ margin: '4px 0 12px', maxWidth: 720 }}>
              Find the step where the agent would call the real system, and add <b>AgentPermit → Request an action</b>
              immediately before it, with these inputs:
            </p>
            <table className="table compact">
              <thead><tr><th>Input</th><th>What to put</th></tr></thead>
              <tbody>
                <tr><td><CopyChip text="actionId" /></td><td>The catalog id for what you’re governing (see below), e.g. {scoped[0] ? <CopyChip text={scoped[0]} /> : <span className="mono">Payment.Issue</span>}</td></tr>
                <tr><td><CopyChip text="parameters" /></td><td>The action’s fields — e.g. <span className="mono">parameters.amount</span>, <span className="mono">parameters.currency</span>. Match the action’s schema.</td></tr>
                <tr><td><CopyChip text="idempotencyKey" /></td><td>A unique id for this request, ≥ 8 chars. Use a GUID from your topic; a retry with the same key is de-duplicated. Never reuse it for a different action.</td></tr>
              </tbody>
            </table>
            <p className="muted small" style={{ margin: '10px 0 0' }}>
              Leave <span className="mono">agentId</span> blank — the gateway derives it from the signed-in identity (header <span className="mono">{integ.data?.identityHeader ?? 'X-Agent-Client-Id'}</span>), never the request body.
            </p>
          </div>
        </section>

        {/* 3 — Branch */}
        <section className="card guide-step">
          <div className="guide-num">3</div>
          <div className="guide-body">
            <b style={{ fontSize: 15 }}>Branch on the <span className="mono">decision</span> output</b>
            <ul className="guide-branches">
              <li><DecisionPill d="ALLOW" /> <span>Proceed — do the real action (or, if AgentPermit executes it for you, you’re done).</span></li>
              <li><DecisionPill d="REQUIRE_APPROVAL" /> <span>Tell the user it’s been sent for sign-off and <b>stop here</b>. A person approves in <b>Things needing you</b> / Teams; then it runs.</span></li>
              <li><DecisionPill d="DENY" /> <span>Tell the user it isn’t allowed (show <span className="mono">reason</span>). Don’t perform the action.</span></li>
            </ul>
            <p className="muted small" style={{ margin: '6px 0 0' }}>Keep the real action <b>inside the Allow branch only</b> — that’s the wall.</p>
          </div>
        </section>

        {/* 4 — Turn on tracing (App Insights) */}
        <section className="card guide-step">
          <div className="guide-num">4</div>
          <div className="guide-body">
            <b style={{ fontSize: 15 }}>Turn on tracing (so incidents are traceable end-to-end)</b>
            <p className="muted small" style={{ margin: '4px 0 12px', maxWidth: 720 }}>
              In Copilot Studio: <b>Settings → Advanced → Application Insights</b>, paste the connection string below,
              and enable activity/event logging. This sends the agent’s conversation + tool telemetry to the <b>same</b>
              App Insights as AgentPermit’s decisions — so one trace shows the agent’s reasoning, the tool call, and the
              <span className="mono"> ALLOW/ASK/DENY</span> decision together.
            </p>
            <div className="kvline"><span>App Insights connection string</span>
              {integ.data?.appInsightsConnectionString
                ? <CopyChip text={integ.data.appInsightsConnectionString} label="Copy connection string" />
                : <span className="muted small">Not configured on the gateway yet.</span>}
            </div>
            <p className="muted small" style={{ margin: '2px 0 0' }}>
              It’s an ingestion (write-only) key — safe to paste into Copilot Studio. Once on, use <b>Investigate</b> in
              the console (or App Insights KQL) to trace any incident by conversation or correlation id.
            </p>
          </div>
        </section>

        {/* Actions for this agent */}
        <section className="card">
          <b style={{ fontSize: 15 }}>Action ids to use for {agent?.displayName || agent?.agentId}</b>
          {scoped.length ? (
            <>
              <p className="muted small" style={{ margin: '4px 0 10px' }}>This agent has rules for these actions — copy the id straight into the connector’s <span className="mono">actionId</span>:</p>
              <div className="chip-wrap">{scoped.map((id) => <CopyChip key={id} text={id} label={`${actLabel(id)}  ·  ${id}`} />)}</div>
            </>
          ) : (
            <p className="muted small" style={{ margin: '4px 0 10px' }}>
              No agent-specific rules yet (global rules still apply). You can add one under <button className="linklike" onClick={() => navigate('rules')}>Rules</button> — “Agent is {agent?.displayName || agent?.agentId}”.
            </p>
          )}

          <button className="btn ghost small" style={{ marginTop: 8 }} onClick={() => setShowAll((s) => !s)}>
            {showAll ? 'Hide the full catalog' : 'Browse all governed actions'}
          </button>
          {showAll && (catalog.loading ? <Loading what="actions" /> : (
            <div style={{ marginTop: 12 }}>
              {groups.map(([domain, l]) => (
                <div key={domain} style={{ marginBottom: 14 }}>
                  <div className="muted small" style={{ fontWeight: 700, marginBottom: 6 }}>{DOMAIN_LABEL[domain] ?? domain}</div>
                  <div className="chip-wrap">{l.map((a) => <CopyChip key={a.actionId} text={a.actionId} label={`${prettyAction(a.actionId, a.displayName)}  ·  ${a.actionId}`} />)}</div>
                </div>
              ))}
            </div>
          ))}
        </section>
      </>)}
    </div>
  )
}
