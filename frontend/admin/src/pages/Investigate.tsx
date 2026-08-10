import { useMemo, useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, Loading, EmptyState, StatusPill, Drawer, CopyChip, rowActivate } from '../components/ui'
import { prettyAction } from '../plain'
import type { ActionRequest } from '../types'

interface Execution {
  executionId: string; actionRequestId: string; adapterCode?: string; targetResourceId?: string
  status: string; attemptNumber?: number; startedUtc?: string; completedUtc?: string; errorCode?: string; errorMessage?: string
}
interface Receipt { actionRequestId: string; receiptNumber: string; signed: boolean; createdUtc: string }

interface TrailRow { agentId: string; total: number; allowed: number; stepUp: number; denied: number; failed: number; lastSeenUtc?: string }

interface ConvStep {
  kind: 'user' | 'bot' | 'thinking' | 'tool' | 'governance'
  text?: string; tool?: string; actionId?: string; decision?: string
  durationMs?: number; needsApproval?: boolean; isAgentPermit?: boolean; ts?: string
}
interface Conversation {
  conversationId: string; agentId?: string; botName?: string; startTime?: string
  hasGovernance: boolean; governedCalls: number; outcome?: string; steps: ConvStep[]
}

const BAD_STATUS = /DENIED|REJECTED|FAILED/i
const isProblemReq = (r: ActionRequest) => BAD_STATUS.test(r.status)

function ago(iso?: string) {
  if (!iso) return ''
  const s = Math.floor((Date.now() - new Date(iso).getTime()) / 1000)
  if (s < 60) return `${s}s ago`; if (s < 3600) return `${Math.floor(s / 60)}m ago`
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`; return `${Math.floor(s / 86400)}d ago`
}
// Absolute date+time for trail tracing (e.g. "10 Aug, 18:42:05").
function fmt(iso?: string) {
  if (!iso) return ''
  const d = new Date(iso)
  return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) + ', ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}
function fmtTime(iso?: string) {
  if (!iso) return ''
  return new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

// One KQL to pull the whole incident from App Insights (agent reasoning + tool calls + AgentPermit decision).
const kqlFor = (corr: string) => `union requests, dependencies, traces, exceptions
| where timestamp > ago(24h)
| where tostring(customDimensions["agentpermit.correlationId"]) == "${corr}"
   or operation_Id in (
     (requests | where tostring(customDimensions["agentpermit.correlationId"]) == "${corr}" | project operation_Id))
| project timestamp, itemType, name,
          decision = tostring(customDimensions["agentpermit.decision"]),
          reason   = tostring(customDimensions["agentpermit.reasonCode"]),
          rule     = tostring(customDimensions["agentpermit.matchedRuleId"])
| order by timestamp asc`

export function Investigate({ sub }: { sub?: string; navigate?: (id: string) => void }) {
  const requests = useAsync<ActionRequest[]>(() => api('/api/admin/requests?take=500'))
  const executions = useAsync<Execution[]>(() => api('/api/admin/executions?take=500'))
  const receipts = useAsync<Receipt[]>(() => api('/api/admin/receipts?take=500'))
  const [q, setQ] = useState('')
  const [problemsOnly, setProblemsOnly] = useState(false)
  const [agent, setAgent] = useState(sub ?? '')   // deep-linked from an agent drawer ("View trail")
  const [range, setRange] = useState<'24h' | '7d' | '30d' | 'all'>(sub ? 'all' : '7d')
  const [openCorr, setOpenCorr] = useState<string | undefined>()

  const execByReq = useMemo(() => {
    const m = new Map<string, Execution[]>()
    for (const e of executions.data ?? []) { const a = m.get(e.actionRequestId) ?? []; a.push(e); m.set(e.actionRequestId, a) }
    return m
  }, [executions.data])
  const rcptByReq = useMemo(() => {
    const m = new Map<string, Receipt>()
    for (const r of receipts.data ?? []) m.set(r.actionRequestId, r)
    return m
  }, [receipts.data])

  // Stitch requests into incidents by correlationId.
  const sessions = useMemo(() => {
    const byCorr = new Map<string, ActionRequest[]>()
    for (const r of requests.data ?? []) { const a = byCorr.get(r.correlationId) ?? []; a.push(r); byCorr.set(r.correlationId, a) }
    return [...byCorr.entries()].map(([corr, rs]) => {
      rs.sort((a, b) => a.createdUtc.localeCompare(b.createdUtc))
      const agents = [...new Set(rs.map((r) => r.executingAgentId))]
      const actions = [...new Set(rs.map((r) => r.actionId))]
      const failedExec = rs.some((r) => (execByReq.get(r.actionRequestId) ?? []).some((e) => BAD_STATUS.test(e.status) || e.errorCode))
      const denied = rs.some(isProblemReq)
      const problem = denied || failedExec
      return { corr, requests: rs, agents, actions, start: rs[0].createdUtc, end: rs[rs.length - 1].updatedUtc || rs[rs.length - 1].createdUtc, problem, denied, failedExec }
    }).sort((a, b) => b.end.localeCompare(a.end))
  }, [requests.data, execByReq])

  const rangeHours = range === '24h' ? 24 : range === '7d' ? 168 : range === '30d' ? 720 : 24 * 90
  // Centralized fleet trail — server-side per-agent aggregation (scales past the 500-row client window).
  const trail = useAsync<TrailRow[]>(() => api(`/api/admin/trail/summary?hours=${rangeHours}`), [rangeHours])
  const agentOptions = useMemo(() => (trail.data ?? []).map((t) => t.agentId), [trail.data])
  const rangeMs = range === '24h' ? 864e5 : range === '7d' ? 6048e5 : range === '30d' ? 2592e6 : Infinity
  const cutoff = Date.now() - rangeMs
  const fleet = useMemo(() => {
    const t = trail.data ?? []
    return { agents: t.length, total: t.reduce((s, r) => s + r.total, 0), denied: t.reduce((s, r) => s + r.denied, 0),
      failed: t.reduce((s, r) => s + r.failed, 0), stepUp: t.reduce((s, r) => s + r.stepUp, 0) }
  }, [trail.data])

  const filtered = sessions.filter((s) => {
    if (problemsOnly && !s.problem) return false
    if (agent && !s.agents.includes(agent)) return false
    if (rangeMs !== Infinity && new Date(s.end).getTime() < cutoff) return false
    if (q) {
      const hay = `${s.corr} ${s.agents.join(' ')} ${s.actions.join(' ')}`.toLowerCase()
      if (!hay.includes(q.toLowerCase())) return false
    }
    return true
  })

  const open = openCorr ? sessions.find((s) => s.corr === openCorr) : undefined
  const loading = requests.loading || executions.loading || receipts.loading

  // The reasoning layer: recent Copilot Studio conversations, parsed from Dataverse (no per-agent setup).
  const openAgentId = open?.agents[0]
  const convos = useAsync<Conversation[]>(
    () => openAgentId ? api(`/api/admin/onboarding/conversations?take=20${openAgentId ? `&agentId=${encodeURIComponent(openAgentId)}` : ''}`) : Promise.resolve([]),
    [openAgentId])

  return (
    <div>
      <PageHeader title="Investigate"
        subtitle="Every agent action, stitched into incidents by correlation id — decision, execution, errors and evidence in one timeline for root-cause."
        actions={<button className="btn ghost" onClick={() => { requests.reload(); executions.reload(); receipts.reload() }}>Refresh</button>} />

      <div className="toolbar">
        <input className="search" type="search" aria-label="Search incidents" placeholder="Search by agent, action or correlation id…" value={q} onChange={(e) => setQ(e.target.value)} />
        <select value={agent} onChange={(e) => setAgent(e.target.value)} aria-label="Filter by agent">
          <option value="">All agents</option>
          {agentOptions.map((a) => <option key={a} value={a}>{a}</option>)}
        </select>
        <select value={range} onChange={(e) => setRange(e.target.value as typeof range)} aria-label="Time range">
          <option value="24h">Last 24 hours</option>
          <option value="7d">Last 7 days</option>
          <option value="30d">Last 30 days</option>
          <option value="all">All time</option>
        </select>
        <label className="check" style={{ margin: 0 }}>
          <input type="checkbox" checked={problemsOnly} onChange={(e) => setProblemsOnly(e.target.checked)} /> <span className="small">Problems only</span>
        </label>
        <span className="muted count" style={{ marginLeft: 'auto' }}>{filtered.length} of {sessions.length}</span>
      </div>

      {/* Centralized fleet trail — server-aggregated, so it holds up at prod scale (not capped at 500). */}
      <div className="tiles" style={{ marginBottom: 16 }}>
        <div className="stat static"><div className="stat-label">Active agents</div><div className="stat-value">{fleet.agents}</div></div>
        <div className="stat static"><div className="stat-label">Actions ({range})</div><div className="stat-value">{fleet.total.toLocaleString()}</div></div>
        <div className="stat static approval"><div className="stat-label">Sign-offs</div><div className="stat-value">{fleet.stepUp}</div></div>
        <div className="stat static deny"><div className="stat-label">Denied</div><div className="stat-value">{fleet.denied}</div></div>
        <div className="stat static deny"><div className="stat-label">Failed</div><div className="stat-value">{fleet.failed}</div></div>
      </div>

      {!agent && (trail.data && trail.data.length > 0) && (
        <section className="card" style={{ marginBottom: 16 }}>
          <div className="row spread" style={{ marginTop: 0 }}><b style={{ fontSize: 15 }}>Agent trails</b><span className="muted small">activity per agent · {range}</span></div>
          <table className="table">
            <thead><tr><th>Agent</th><th>Actions</th><th>Allowed</th><th>Sign-off</th><th>Denied</th><th>Failed</th><th>Last seen</th></tr></thead>
            <tbody>
              {trail.data.slice(0, 20).map((t) => (
                <tr key={t.agentId} className="rowlink" {...rowActivate(() => setAgent(t.agentId), `Filter to ${t.agentId}`)}>
                  <td><b>{t.agentId}</b></td>
                  <td>{t.total}</td>
                  <td><span className="ldot allow" /> {t.allowed}</td>
                  <td>{t.stepUp || '—'}</td>
                  <td>{t.denied ? <span className="pill deny">{t.denied}</span> : '—'}</td>
                  <td>{t.failed ? <span className="pill deny">{t.failed}</span> : '—'}</td>
                  <td className="muted">{ago(t.lastSeenUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {trail.data.length > 20 && <p className="muted small" style={{ marginTop: 8 }}>Showing top 20 of {trail.data.length} agents by volume.</p>}
        </section>
      )}

      <section className="card">
        {loading ? <Loading what="activity" /> : (filtered.length ? (
          <table className="table">
            <thead><tr><th>Incident</th><th>Agents</th><th>Actions</th><th>Steps</th><th>Outcome</th><th>Started</th></tr></thead>
            <tbody>
              {filtered.map((s) => (
                <tr key={s.corr} className="rowlink" {...rowActivate(() => setOpenCorr(s.corr), `Investigate ${s.corr}`)}>
                  <td><span className="mono">{s.corr.slice(0, 8)}…</span></td>
                  <td>{s.agents.slice(0, 2).join(', ')}{s.agents.length > 2 ? ` +${s.agents.length - 2}` : ''}</td>
                  <td>{s.actions.slice(0, 2).map((a) => prettyAction(a)).join(', ')}{s.actions.length > 2 ? '…' : ''}</td>
                  <td>{s.requests.length}</td>
                  <td>{s.problem
                    ? <span className={`pill deny`}>{s.denied ? 'Denied' : 'Failed'}</span>
                    : <span className="pill allow">OK</span>}</td>
                  <td className="muted" title={new Date(s.start).toLocaleString()}>{fmt(s.start)} <span className="dim">· {ago(s.end)}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No activity yet" hint="Governed agent actions will appear here, grouped into incidents you can drill into." />)}
      </section>

      {open && (
        <Drawer wide title={`Incident ${open.corr.slice(0, 8)}…`} onClose={() => setOpenCorr(undefined)}
          footer={<div className="row" style={{ margin: 0, gap: 10 }}>
            <CopyChip text={open.corr} label="Copy correlation id" />
            <CopyChip text={kqlFor(open.corr)} label="Copy App Insights KQL" />
          </div>}>
          <p className="muted small" style={{ marginTop: 0 }}>
            {open.requests.length} step{open.requests.length === 1 ? '' : 's'} · {open.agents.length} agent{open.agents.length === 1 ? '' : 's'} · started {ago(open.start)}.
            {open.problem && <b style={{ color: 'var(--deny)' }}> Contains a {open.denied ? 'denied action' : 'failed execution'}.</b>}
          </p>
          <div className="timeline">
            {open.requests.map((r) => {
              const execs = execByReq.get(r.actionRequestId) ?? []
              const rcpt = rcptByReq.get(r.actionRequestId)
              const bad = isProblemReq(r) || execs.some((e) => BAD_STATUS.test(e.status) || e.errorCode)
              return (
                <div key={r.actionRequestId} className={`tl-step ${bad ? 'bad' : ''}`}>
                  <div className="tl-dot" />
                  <div className="tl-body">
                    <div className="row spread" style={{ margin: 0, alignItems: 'baseline' }}>
                      <b>{prettyAction(r.actionId)}</b>
                      <StatusPill s={r.status} />
                    </div>
                    <div className="muted small" style={{ marginTop: 2 }}>
                      {r.executingAgentId}{r.rootAgentId && r.rootAgentId !== r.executingAgentId ? ` (via ${r.rootAgentId})` : ''} · {new Date(r.createdUtc).toLocaleString()}
                    </div>
                    {execs.map((e) => (
                      <div key={e.executionId} className={`tl-exec ${BAD_STATUS.test(e.status) || e.errorCode ? 'bad' : ''}`}>
                        <span className="mono small">{e.adapterCode || 'execution'}</span> · {e.status}
                        {e.attemptNumber && e.attemptNumber > 1 ? ` · attempt ${e.attemptNumber}` : ''}
                        {(e.errorCode || e.errorMessage) && <div className="error small" style={{ marginTop: 3 }}>{e.errorCode}{e.errorCode && e.errorMessage ? ' — ' : ''}{e.errorMessage}</div>}
                      </div>
                    ))}
                    {rcpt && <div className="muted small" style={{ marginTop: 4 }}>Receipt <span className="mono">{rcpt.receiptNumber}</span> {rcpt.signed ? '· signed' : '· unsigned'}</div>}
                  </div>
                </div>
              )
            })}
          </div>
          {/* Reasoning layer — graphical conversation flow from Dataverse transcripts (no per-agent setup). */}
          <h3 className="sec" style={{ marginTop: 22 }}>Agent conversations</h3>
          <p className="muted small" style={{ margin: '-4px 0 10px' }}>
            The agent’s own reasoning and tool calls for <b>{openAgentId}</b>, read straight from Copilot Studio —
            AgentPermit checkpoints highlighted.
          </p>
          {convos.loading ? <Loading what="conversations" />
            : (convos.data && convos.data.length
              ? convos.data.slice(0, 4).map((c) => <ConversationFlow key={c.conversationId} c={c} />)
              : <p className="muted small">No Copilot Studio conversations found for this agent yet (they appear once the agent has run and Dataverse has stored the transcript).</p>)}

          <p className="muted small" style={{ marginTop: 14 }}>
            Prefer raw traces? Paste the <b>App Insights KQL</b> (above) into your workspace for the full end-to-end trace.
          </p>
        </Drawer>
      )}
    </div>
  )
}

// Icon per step kind.
function StepIcon({ kind }: { kind: ConvStep['kind'] }) {
  const p = { width: 15, height: 15, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const }
  if (kind === 'user') return <svg {...p}><circle cx="12" cy="8" r="4" /><path d="M4 21v-1a6 6 0 0 1 12 0v1" /></svg>
  if (kind === 'bot') return <svg {...p}><rect x="4" y="7" width="16" height="12" rx="3" /><path d="M12 3v4M9 13h.01M15 13h.01" /></svg>
  if (kind === 'thinking') return <svg {...p}><path d="M12 3a6 6 0 0 0-4 10.5V17h8v-3.5A6 6 0 0 0 12 3zM9 21h6" /></svg>
  if (kind === 'governance') return <svg {...p}><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" /><path d="M9 12l2 2 4-4" /></svg>
  return <svg {...p}><path d="M14 7l-1.5-1.5a2 2 0 0 0-3 0L4 11a2 2 0 0 0 0 3l5.5 5.5a2 2 0 0 0 3 0L18 14" /><path d="M14 7l4 4" /></svg>
}

// Map an AgentPermit result string to a decision tone/label.
function decisionTone(d?: string): { cls: string; label: string } {
  const s = (d || '').toUpperCase()
  if (s.includes('ALLOW') || s.includes('EXECUT') || s.includes('SUCCEED')) return { cls: 'allow', label: 'Allowed' }
  if (s.includes('DENY') || s.includes('DENIED') || s.includes('REJECT') || s.includes('BLOCK')) return { cls: 'deny', label: 'Denied' }
  if (s.includes('APPROVAL') || s.includes('CONSENT') || s.includes('SUSPEND') || s.includes('STEP')) return { cls: 'approval', label: 'Sign-off' }
  return { cls: 'neutral', label: d || '—' }
}

// A single conversation rendered as a vertical flow diagram, with timestamps and expandable reasoning.
function ConversationFlow({ c }: { c: Conversation }) {
  const out = decisionTone(c.outcome)
  const [expanded, setExpanded] = useState(false)
  const hasReasoning = c.steps.some((s) => s.kind === 'thinking')
  return (
    <div className="conv-card">
      <div className="conv-head">
        <span className="mono small">{c.conversationId.slice(0, 8)}…</span>
        {c.startTime && <span className="muted small" title={new Date(c.startTime).toLocaleString()}>{fmt(c.startTime)}</span>}
        <span className="muted small">· {c.governedCalls} checkpoint{c.governedCalls === 1 ? '' : 's'}</span>
        {hasReasoning && <button className="linklike small" onClick={() => setExpanded((v) => !v)}>{expanded ? 'Collapse reasoning' : 'Show full reasoning'}</button>}
        <span className={`pill ${out.cls}`} style={{ marginLeft: 'auto' }}>{out.label}</span>
      </div>
      <div className={`flow ${expanded ? 'expanded' : ''}`}>
        {c.steps.map((st, i) => {
          const gov = st.kind === 'governance'
          const tone = gov ? decisionTone(st.decision).cls : ''
          return (
            <div key={i} className={`flow-node k-${st.kind} ${gov ? `tone-${tone}` : ''} ${st.needsApproval ? 'await' : ''}`}>
              <span className="fn-icon"><StepIcon kind={st.kind} /></span>
              <div className="fn-body">
                {gov ? (
                  <>
                    <div className="fn-title">AgentPermit checkpoint{st.actionId ? <> · <span className="mono">{st.actionId}</span></> : ''}</div>
                    <div className="fn-sub">
                      {st.needsApproval ? <span className="pill approval">Sent for sign-off</span> : <span className={`pill ${decisionTone(st.decision).cls}`}>{decisionTone(st.decision).label}</span>}
                      {st.durationMs != null && <span className="muted small"> · {st.durationMs} ms</span>}
                    </div>
                  </>
                ) : st.kind === 'tool' ? (
                  <>
                    <div className="fn-title">{st.tool || 'Tool call'}</div>
                    {st.decision && <div className="fn-sub muted small">{st.decision}{st.durationMs != null ? ` · ${st.durationMs} ms` : ''}</div>}
                  </>
                ) : st.kind === 'thinking' ? (
                  <div className="fn-think">{st.text}</div>
                ) : (
                  <>
                    <div className="fn-role">{st.kind === 'user' ? 'User' : 'Agent'}</div>
                    <div className="fn-text">{st.text}</div>
                  </>
                )}
              </div>
              {st.ts && <span className="fn-time" title={new Date(st.ts).toLocaleString()}>{fmtTime(st.ts)}</span>}
            </div>
          )
        })}
      </div>
    </div>
  )
}
