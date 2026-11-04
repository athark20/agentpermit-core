import { useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError, reasonOf } from '../api'
import { useAsync } from '../hooks'
import { useAccess } from '../access'
import { useToast } from '../components/Toast'
import { PageHeader, StatCard, Loading, EmptyState, Drawer, KV, rowActivate } from '../components/ui'
import {
  agentStage, STAGES, stageIndex, outcome, riskWord, Tag, prettyAction,
  plainCondition, ruleOutcome, timeAgo, Intro,
} from '../plain'
import type { Agent, ActionRequest, BusinessAction, PolicyActive } from '../types'

interface RolloutRow { agentId: string; posture: string; recommendation?: string | null; observationsInWindow?: number; wouldHaveBlockedInWindow?: number }
interface RolloutOverview { agents: RolloutRow[] }

// ============================================================ 1 · Your agents

export function YourAgents() {
  const agents = useAsync<Agent[]>(() => api('/api/admin/agents'))
  const rollout = useAsync<RolloutOverview>(() => api('/api/admin/rollout'))
  const requests = useAsync<ActionRequest[]>(() => api('/api/admin/requests?take=300'))
  const [open, setOpen] = useState<Agent>()

  const stageById = useMemo(() => {
    const m: Record<string, RolloutRow> = {}
    for (const r of rollout.data?.agents ?? []) m[r.agentId] = r
    return m
  }, [rollout.data])
  const activity = useMemo(() => {
    const m: Record<string, { last?: string; count: number }> = {}
    for (const r of requests.data ?? []) {
      const e = (m[r.executingAgentId] ??= { count: 0 }); e.count++
      if (!e.last || r.createdUtc > e.last) e.last = r.createdUtc
    }
    return m
  }, [requests.data])

  const rows = agents.data ?? []
  const live = rows.filter((a) => a.enabled && (stageById[a.agentId]?.posture ?? 'ENFORCE').toUpperCase() === 'ENFORCE').length
  const learning = rows.filter((a) => a.enabled && ['OBSERVE', 'WARN'].includes((stageById[a.agentId]?.posture ?? 'ENFORCE').toUpperCase())).length

  return (
    <div>
      <PageHeader title="Your agents" actions={<button className="btn" onClick={() => { location.hash = '#/onboarding' }}>Add agents</button>} />
      <Intro>These are the AI agents working in your business. Each one starts by <b>learning</b>, then you <b>test</b> it, then let it go <b>live</b> — safely, at your pace.</Intro>

      <div className="tiles">
        <StatCard label="Agents in total" value={rows.length} />
        <StatCard label="Fully live" value={live} tone="allow" />
        <StatCard label="Still settling in" value={learning} tone="approval" sub="learning or testing" />
      </div>

      <section className="card">
        {agents.loading ? <Loading what="your agents" /> : (rows.length ? (
          <table className="table">
            <thead><tr><th>Agent</th><th>Owner</th><th>Protection status</th><th>Risk</th><th>Last active</th><th>Actions done</th></tr></thead>
            <tbody>
              {rows.map((a) => {
                const st = agentStage(a.enabled, stageById[a.agentId]?.posture)
                const act = activity[a.agentId]
                return (
                  <tr key={a.agentId} className="rowlink" {...rowActivate(() => setOpen(a), `Open ${a.displayName || a.agentId}`)}>
                    <td><b>{a.displayName || a.agentId}</b></td>
                    <td>{a.owner || '—'}</td>
                    <td><Tag p={st} /></td>
                    <td><Tag p={riskWord(a.riskTier)} /></td>
                    <td className="muted">{timeAgo(act?.last)}</td>
                    <td>{act?.count ?? 0}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        ) : <EmptyState title="No agents yet" hint="Add your first agent to bring it under control." />)}
      </section>

      {open && <AgentDrawer agent={open} rollout={stageById[open.agentId]} onClose={() => setOpen(undefined)}
        onChanged={() => { rollout.reload(); agents.reload() }} />}
    </div>
  )
}

function AgentDrawer({ agent, rollout, onClose, onChanged }:
  { agent: Agent; rollout?: RolloutRow; onClose: () => void; onChanged: () => void }) {
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  const access = useAccess()
  const posture = (rollout?.posture ?? 'ENFORCE')
  const idx = stageIndex(posture)
  const st = agentStage(agent.enabled, posture)

  async function move(dir: 'promote' | 'demote') {
    setBusy(true)
    try {
      await api(`/api/admin/rollout/${encodeURIComponent(agent.agentId)}/${dir}`, { method: 'POST' })
      toast.success(dir === 'promote' ? 'Agent moved forward' : 'Agent moved back', `${agent.displayName || agent.agentId} updated.`)
      onChanged(); onClose()
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not update the agent', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    } finally { setBusy(false) }
  }

  const obs = rollout?.observationsInWindow
  const blocked = rollout?.wouldHaveBlockedInWindow
  const readyToAdvance = rollout?.recommendation && idx < 2

  const mayMove = access.canManageAgents || access.isAdmin
  return (
    <Drawer title={agent.displayName || agent.agentId} onClose={onClose}
      footer={!mayMove
        ? <span className="muted small">Admins and agent owners can move this agent between stages.</span>
        : idx < 2 ? <div className="row" style={{ margin: 0 }}>
        <button className="btn primary" disabled={busy} onClick={() => move('promote')}>
          {idx === 0 ? 'Start testing →' : 'Go live →'}
        </button>
        {idx > 0 && <button className="btn" disabled={busy} onClick={() => move('demote')}>Step back</button>}
      </div> : <button className="btn" disabled={busy} onClick={() => move('demote')}>Move back to testing</button>}>

      <div style={{ marginBottom: 14 }}><Tag p={st} /> <span className="muted small">{st.hint}</span></div>

      <div className="steps">
        {STAGES.map((s, i) => (
          <div key={s} className={`step ${i < idx ? 'done' : i === idx ? 'current' : ''}`}>
            <span className="step-dot">{i < idx ? '✓' : i + 1}</span> {s}
          </div>
        ))}
      </div>

      <div className="passport">
        <KV label="Owner">{agent.owner || '—'}</KV>
        <KV label="Type">{agent.type || 'AI agent'}</KV>
        <KV label="Runs in">{agent.environment || '—'}</KV>
        <KV label="Risk">{riskWord(agent.riskTier).label}</KV>
        <KV label="Verified identity">{agent.entraClientId
          ? <span className="pill allow">Confirmed</span>
          : <span className="pill approval">Not confirmed yet</span>}</KV>
      </div>
      <p className="muted small" style={{ margin: '2px 0 10px' }}>
        A confirmed identity is this agent’s passport — it’s how AgentPermit knows it really is who it says it is,
        so no other agent or person can impersonate it.
      </p>

      <div className="row" style={{ margin: '0 0 12px', gap: 8 }}>
        <a className="btn ghost small" href={`#/investigate/${agent.agentId}`}>View trail</a>
        <a className="btn ghost small" href={`#/integration/${agent.agentId}`}>Integration guide</a>
        <a className="btn ghost small" href="#/rules">Rules</a>
      </div>

      {(agent.enabled !== false && idx < 2) && (
        <div className="card" style={{ marginTop: 14 }}>
          <b>Is it ready for the next step?</b>
          <p className="muted small" style={{ margin: '6px 0 0' }}>
            {obs == null ? 'No activity recorded yet.'
              : blocked ? `${blocked} action(s) would have been stopped recently — worth a look before you move on.`
              : readyToAdvance ? `Looking good — ${obs} recent action(s) and nothing risky. Safe to ${idx === 0 ? 'start testing' : 'go live'}.`
              : `${obs} recent action(s) so far. Give it a little more time before moving on.`}
          </p>
        </div>
      )}
      <p className="muted small" style={{ marginTop: 12 }}>
        While an agent is learning or testing, anything risky (like large payments) is <b>already blocked</b> — moving
        forward only tightens control on the smaller, everyday actions.
      </p>
    </Drawer>
  )
}

// ============================================================ 2 · What they can do

export function WhatTheyCanDo() {
  const actions = useAsync<BusinessAction[]>(() => api('/api/admin/actions'))
  const policy = useAsync<PolicyActive>(() => api('/api/policies/active'))

  const rulesFor = (actionId: string) =>
    (policy.data?.rules ?? []).filter((r) => !r.actionId || r.actionId === actionId)

  return (
    <div>
      <PageHeader title="What they can do" />
      <Intro>This is the full list of things your agents are allowed to do — and the limits on each. If something isn’t here, no agent can do it.</Intro>

      <section className="card">
        {actions.loading ? <Loading what="what agents can do" /> : (actions.data?.length ? (
          <div className="stack">
            {actions.data.map((a) => (
              <div key={a.actionId} className="permit-row">
                <div className="row" style={{ margin: 0, alignItems: 'center' }}>
                  <b style={{ fontSize: 15 }}>{prettyAction(a.actionId, a.displayName)}</b>
                  <span style={{ marginLeft: 'auto' }}><Tag p={riskWord(a.risk)} /></span>
                </div>
                <ul className="rules">
                  {rulesFor(a.actionId).length
                    ? rulesFor(a.actionId).map((r) => (
                      <li key={r.id}>{ruleSentence(r)}</li>
                    ))
                    : <li className="muted">Allowed within the standard limits.</li>}
                </ul>
              </div>
            ))}
          </div>
        ) : <EmptyState title="No actions set up yet" />)}
      </section>
    </div>
  )
}

// Render a policy rule as a plain sentence.
function ruleSentence(r: PolicyActive['rules'][number] & { conditions?: { field: string; operator: string; value: unknown }[] }): string {
  const conds = (r.conditions ?? [])
  const when = conds.length ? 'When ' + conds.map((c) => plainCondition(c.field, c.operator, c.value)).join(' and ') + ', ' : 'Otherwise, '
  return `${when}${ruleOutcome(r.decision)}.`
}

// ============================================================ 3 · Limits & safety

interface Safety { killSwitch: { frozen: boolean; reason?: string }; circuitBreaker: { maxExecutionsPerWindow: number; windowSeconds: number } }

export function LimitsAndSafety() {
  const s = useAsync<Safety>(() => api('/api/admin/safety'))
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  const access = useAccess()

  async function toggle(freeze: boolean) {
    const reason = freeze ? prompt('Why are you pausing every agent? (optional)', 'Manual pause') : null
    if (freeze && reason === null) return
    setBusy(true)
    try {
      await api(`/api/admin/safety/${freeze ? 'freeze' : 'unfreeze'}`, freeze ? { method: 'POST', body: JSON.stringify({ reason }) } : { method: 'POST' })
      toast.success(freeze ? 'All agents paused' : 'Agents resumed', freeze ? 'Every agent action is on hold until you resume.' : 'Agents are governed and running again.')
      s.reload()
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not change the pause state', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    } finally { setBusy(false) }
  }

  const frozen = s.data?.killSwitch.frozen
  const cb = s.data?.circuitBreaker
  return (
    <div>
      <PageHeader title="Limits & safety" />
      <Intro>Your always-on safety net. You don’t need to touch this day to day — it just protects you in the background.</Intro>

      {s.loading ? <Loading what="safety" /> : (
        <>
          <section className="card" style={{ borderLeft: `4px solid var(--${frozen ? 'deny' : 'allow'})` }}>
            <div className="row" style={{ margin: 0, alignItems: 'center' }}>
              <div>
                <b style={{ fontSize: 15 }}>{frozen ? 'All agents are paused' : 'Emergency stop'}</b>
                <p className="muted small" style={{ margin: '4px 0 0' }}>
                  {frozen
                    ? `Every agent is stopped right now${s.data?.killSwitch.reason ? ` — ${s.data.killSwitch.reason}` : ''}. Resume when you’re ready.`
                    : 'One click stops every agent instantly. Use it if something looks wrong.'}
                </p>
              </div>
              <div style={{ marginLeft: 'auto' }}>
                {access.canManageAgents || access.isAdmin ? (
                  frozen
                    ? <button className="btn primary" disabled={busy} onClick={() => toggle(false)}>Resume all agents</button>
                    : <button className="btn danger" disabled={busy} onClick={() => toggle(true)}>Pause all agents</button>
                ) : (
                  <span className="muted small">Admins and agent owners can pause</span>
                )}
              </div>
            </div>
          </section>

          <section className="card">
            <b style={{ fontSize: 15 }}>Automatic runaway protection</b>
            <p className="muted small" style={{ margin: '4px 0 0' }}>
              If any agent suddenly does far more than usual, we stop it on its own — no matter what the rules say.
              Current limit: <b>{cb?.maxExecutionsPerWindow ?? '—'} actions per {cb?.windowSeconds ?? '—'} seconds</b> for each agent.
            </p>
          </section>

          <p className="muted small" style={{ padding: '0 4px' }}>
            Which outside apps your agents may connect to is set up under <b>Settings → App connections</b>.
          </p>
        </>
      )}
    </div>
  )
}

// ============================================================ 4 · What's happening

export function WhatsHappening() {
  const requests = useAsync<ActionRequest[]>(() => api('/api/admin/requests?take=100'))
  const actions = useAsync<BusinessAction[]>(() => api('/api/admin/actions'))
  const nameFor = (id: string) => prettyAction(id, actions.data?.find((a) => a.actionId === id)?.displayName)

  return (
    <div>
      <PageHeader title="What’s happening" />
      <Intro>Everything your agents have done recently — and whether we <b>allowed</b> it, paused it for your <b>sign-off</b>, or <b>stopped</b> it.</Intro>

      <section className="card">
        {requests.loading ? <Loading what="activity" /> : (requests.data?.length ? (
          <table className="table">
            <thead><tr><th>When</th><th>Agent</th><th>What it did</th><th>Outcome</th></tr></thead>
            <tbody>
              {requests.data.map((r) => (
                <tr key={r.actionRequestId}>
                  <td className="muted small">{timeAgo(r.createdUtc)}</td>
                  <td>{r.executingAgentId}</td>
                  <td>{nameFor(r.actionId)}</td>
                  <td><Tag p={outcome(r.status)} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="Nothing yet" hint="Your agents’ activity will show up here." />)}
      </section>
    </div>
  )
}

// ============================================================ 5 · Things needing you

interface Approval {
  approvalRequestId: string; actionId: string; agentId: string
  amount?: number; currency?: string; customerId?: string; reason?: string
  /** The complete frozen payload: every field the approver is signing off on. */
  parameters?: Record<string, unknown>
  initiatingUserId?: string; approverRole?: string
  status: string; payloadHash: string
  requestedAtUtc: string; expiresAtUtc?: string
  /** When a rule needs several approvers: how many, and who has approved so far. */
  requiredApprovals?: number; approvedBy?: string[]
}

function show(v: unknown): string {
  if (v == null || v === '') return '—'
  return typeof v === 'object' ? JSON.stringify(v) : String(v)
}

function decidedWord(status: string): string {
  switch (status) {
    case 'Approved': return 'approved'
    case 'Rejected': return 'declined'
    case 'Expired': return 'closed because nobody decided in time'
    default: return 'decided'
  }
}

/** Every field of the request exactly as the agent sent it, plus who asked and until when. */
function ApprovalDetails({ a }: { a: Approval }) {
  return (
    <div className="approval-details">
      <table className="table">
        <tbody>
          {Object.entries(a.parameters ?? {}).map(([k, v]) => (
            <tr key={k}><td className="muted" style={{ width: '38%' }}>{k}</td><td className="mono">{show(v)}</td></tr>
          ))}
          <tr><td className="muted">Asked for by</td><td>{a.initiatingUserId ?? '— (not recorded)'}</td></tr>
          <tr><td className="muted">Who can approve</td><td>{a.approverRole ?? 'Any approver'}</td></tr>
          <tr><td className="muted">Expires</td><td>{a.expiresAtUtc ? new Date(a.expiresAtUtc).toLocaleString() : '—'}</td></tr>
        </tbody>
      </table>
      <p className="muted small">You are approving exactly these details. If any of them changes before the action runs, your approval no longer counts.</p>
    </div>
  )
}

/**
 * Approvals waiting for a person. A Teams card links here as #/approvals/<id>: that request opens with every
 * detail shown, and the approver decides with their own sign-in.
 */
export function ThingsNeedingYou({ focus }: { focus?: string }) {
  const list = useAsync<Approval[]>(() => api('/api/approvals/pending'))
  const linked = useAsync<Approval | null>(
    () => (focus ? api<Approval>(`/api/approvals/${focus}`).catch(() => null) : Promise.resolve(null)), [focus])
  const [open, setOpen] = useState<string | undefined>(focus)
  const [busy, setBusy] = useState<string>()
  const toast = useToast()
  const access = useAccess()
  const focusRef = useRef<HTMLDivElement>(null)

  useEffect(() => { setOpen(focus) }, [focus])
  useEffect(() => {
    if (!list.loading) focusRef.current?.scrollIntoView({ block: 'center' })
  }, [list.loading, focus])

  async function decide(a: Approval, ok: boolean) {
    setBusy(a.approvalRequestId)
    try {
      // Tie the decision to what was on screen: if the details changed since, the gateway refuses it.
      const res = await api<{ outcome?: string; message?: string }>(`/api/approvals/${a.approvalRequestId}/${ok ? 'approve' : 'reject'}`, {
        method: 'POST', body: JSON.stringify({ expectedPayloadHash: a.payloadHash }),
      })
      // Show what the gateway says happened (e.g. an approval that still needs another approver).
      const done = res.outcome === 'Executed' || res.outcome === 'Rejected' || !res.outcome
      toast.success(done ? (ok ? 'Approved' : 'Rejected') : 'Approval recorded',
        res.message ?? `The request has been ${ok ? 'approved' : 'rejected'}.`)
      list.reload()
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not record your decision', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    } finally { setBusy(undefined) }
  }

  const pending = list.data ?? []
  const linkedNotWaiting = !!focus && !list.loading && !linked.loading && !pending.some((a) => a.approvalRequestId === focus)

  return (
    <div>
      <PageHeader title="Things needing you" />
      <Intro>These actions are bigger or riskier, so an agent has paused and is waiting for your <b>OK</b> before going ahead.</Intro>
      {!access.loading && !access.canApprove && (
        <p className="persona-note">You can view these requests, but only people with the <b>Approver</b> role can approve or decline. Ask your administrator if you need it.</p>
      )}
      {linkedNotWaiting && (
        <p className="persona-note">
          {linked.data && linked.data.status !== 'Pending'
            ? `The request you opened was already ${decidedWord(linked.data.status)}.`
            : 'The request you opened is not waiting for a decision any more.'}
        </p>
      )}

      <section className="card">
        {list.loading ? <Loading what="approvals" /> : (pending.length ? (
          <div className="stack">
            {pending.map((a) => {
              const isOpen = open === a.approvalRequestId
              const isFocus = focus === a.approvalRequestId
              return (
                <div key={a.approvalRequestId} ref={isFocus ? focusRef : undefined}
                  className={`permit-row${isFocus ? ' focused' : ''}`}>
                  <div className="row" style={{ margin: 0, alignItems: 'center' }}>
                    <div>
                      <b>{a.agentId}</b> wants to {prettyAction(a.actionId)}
                      {a.amount != null && <> — <b>{a.amount} {a.currency ?? ''}</b></>}
                      {a.customerId && <span className="muted"> for {a.customerId}</span>}
                      <div className="muted small">{a.reason || ''} · asked {timeAgo(a.requestedAtUtc)}</div>
                      {(a.requiredApprovals ?? 1) > 1 && (
                        <div className="muted small">
                          Needs {a.requiredApprovals} different approvers · {a.approvedBy?.length
                            ? `approved so far by ${a.approvedBy.join(', ')}` : 'no approvals yet'}
                        </div>
                      )}
                    </div>
                    <div className="row" style={{ margin: 0, marginLeft: 'auto', gap: 8, alignItems: 'center' }}>
                      <button className="btn ghost small" aria-expanded={isOpen}
                        onClick={() => setOpen(isOpen ? undefined : a.approvalRequestId)}>
                        {isOpen ? 'Hide details' : 'See every detail'}
                      </button>
                      {access.canApprove ? (
                        <>
                          <button className="btn primary" disabled={busy === a.approvalRequestId} onClick={() => decide(a, true)}>Approve</button>
                          <button className="btn danger" disabled={busy === a.approvalRequestId} onClick={() => decide(a, false)}>Decline</button>
                        </>
                      ) : (
                        <span className="muted small">Waiting for an approver</span>
                      )}
                    </div>
                  </div>
                  {isOpen && <ApprovalDetails a={a} />}
                </div>
              )
            })}
          </div>
        ) : <EmptyState title="You’re all caught up" hint="Nothing is waiting for your sign-off." />)}
      </section>
    </div>
  )
}
