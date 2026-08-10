import { useMemo, useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, DecisionPill, Drawer, KV, Loading, EmptyState, rowActivate } from '../components/ui'
import { AuditLog } from './AuditLog'
import type { ActionRequest, Receipt, Metrics, PolicyActive, VerifyResult } from '../types'

function outcomeKey(status: string): 'allow' | 'deny' | 'stepup' {
  const s = status.toUpperCase()
  if (s === 'SUCCEEDED' || s === 'EXECUTED' || s === 'ALLOW') return 'allow'
  if (s === 'AWAITING_APPROVAL' || s === 'PENDINGAPPROVAL') return 'stepup'
  return 'deny'
}

export function AuditDecisions() {
  const requests = useAsync<ActionRequest[]>(() => api('/api/admin/requests?take=200'))
  const receipts = useAsync<Receipt[]>(() => api('/api/admin/receipts?take=200'))
  const metrics = useAsync<Metrics>(() => api('/api/admin/metrics'))
  const policy = useAsync<PolicyActive>(() => api('/api/policies/active'))
  const [outcome, setOutcome] = useState<'all' | 'allow' | 'stepup' | 'deny'>('all')
  const [q, setQ] = useState('')
  const [open, setOpen] = useState<ActionRequest>()
  const [tab, setTab] = useState<'decisions' | 'changes'>('decisions')

  const byReq = useMemo(() => Object.fromEntries((receipts.data ?? []).map((r) => [r.actionRequestId, r])), [receipts.data])
  const policyName = policy.data ? `${policy.data.policyCode} v${policy.data.version}` : '—'

  const rows = useMemo(() => (requests.data ?? []).filter((r) => {
    if (outcome !== 'all' && outcomeKey(r.status) !== outcome) return false
    if (q && !(`${r.executingAgentId} ${r.actionId} ${r.actionRequestId}`.toLowerCase().includes(q.toLowerCase()))) return false
    return true
  }), [requests.data, outcome, q])

  const m = metrics.data

  return (
    <div>
      <PageHeader title="Audit Log" subtitle="Every permit decision and configuration change — immutable and signed" />

      <div className="subtabs">
        <button className={`subtab ${tab === 'decisions' ? 'active' : ''}`} aria-pressed={tab === 'decisions'} onClick={() => setTab('decisions')}>Permit decisions</button>
        <button className={`subtab ${tab === 'changes' ? 'active' : ''}`} aria-pressed={tab === 'changes'} onClick={() => setTab('changes')}>Configuration changes</button>
      </div>

      {tab === 'changes' ? <AuditLog embedded /> : <>

      <div className="toolbar">
        <select value={outcome} onChange={(e) => setOutcome(e.target.value as typeof outcome)}>
          <option value="all">All (allow, step-up, deny)</option>
          <option value="allow">Allow</option>
          <option value="stepup">Step-up</option>
          <option value="deny">Deny</option>
        </select>
        <input className="search" type="search" aria-label="Search decisions" placeholder="Agent, action or permit id…" value={q} onChange={(e) => setQ(e.target.value)} />
        <span className="count">{rows.length} of {requests.data?.length ?? 0}</span>
      </div>

      <div className="tiles">
        <div className="stat static"><div className="stat-label">Total decisions</div><div className="stat-value">{m?.totalRequests ?? '…'}</div></div>
        <div className="stat static allow"><div className="stat-label">Allowed</div><div className="stat-value">{m ? (m.byStatus['SUCCEEDED'] ?? 0) : '…'}</div></div>
        <div className="stat static approval"><div className="stat-label">Step-up required</div><div className="stat-value">{m ? (m.byStatus['AWAITING_APPROVAL'] ?? 0) : '…'}</div></div>
        <div className="stat static deny"><div className="stat-label">Denied</div><div className="stat-value">{m ? ((m.byStatus['DENIED'] ?? 0) + (m.byStatus['REJECTED'] ?? 0)) : '…'}</div></div>
      </div>

      <section className="card">
        {requests.loading ? <Loading what="decisions" /> : (rows.length ? (
          <table className="table">
            <thead><tr><th>Timestamp</th><th>Agent</th><th>Action</th><th>Policy</th><th>Outcome</th><th>Signed</th><th>Event hash</th></tr></thead>
            <tbody>
              {rows.map((r) => {
                const rec = byReq[r.actionRequestId]
                return (
                  <tr key={r.actionRequestId} className="rowlink" {...rowActivate(() => setOpen(r), `View decision for ${r.actionId}`)}>
                    <td className="muted">{new Date(r.createdUtc).toLocaleTimeString()}</td>
                    <td><b>{r.executingAgentId}</b></td>
                    <td>{r.actionId}</td>
                    <td className="muted">{policyName}</td>
                    <td><DecisionPill d={r.status} /></td>
                    <td>{rec?.signed ? '✓' : '—'}</td>
                    <td className="mono">{rec ? rec.receiptHash.slice(0, 10) + '…' : '—'}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        ) : <EmptyState title="No permit decisions match" hint="Agent requests through the gateway are recorded here." />)}
      </section>
      </>}

      {open && <DecisionDrawer request={open} receipt={byReq[open.actionRequestId]} policyName={policyName} onClose={() => setOpen(undefined)} />}
    </div>
  )
}

function DecisionDrawer({ request, receipt, policyName, onClose }:
  { request: ActionRequest; receipt?: Receipt; policyName: string; onClose: () => void }) {
  const [v, setV] = useState<VerifyResult | 'loading'>()
  async function verify() {
    setV('loading')
    try { setV(await api<VerifyResult>(`/api/admin/receipts/${request.actionRequestId}/verify`)) }
    catch { setV(undefined) }
  }
  return (
    <Drawer title={`Permit prm_${request.actionRequestId.slice(0, 6)}`} onClose={onClose}
      footer={receipt ? <button className="btn primary" onClick={verify}>Verify signature & chain</button> : undefined}>
      <div className="dotlist" style={{ marginBottom: 18 }}>
        <div className="dotrow"><span className="dot ok" /><span className="small">Request received from agent <b>{request.executingAgentId}</b></span></div>
        <div className="dotrow"><span className="dot ok" /><span className="small">Policy engine evaluated <b>{policyName}</b></span></div>
        <div className="dotrow"><span className={`dot ${receipt ? 'ok' : 'warn'}`} /><span className="small">Decision <b>{request.status}</b>{receipt ? ' · signed & chained' : ''}</span></div>
      </div>
      <KV label="Outcome"><DecisionPill d={request.status} /></KV>
      <KV label="Agent">{request.executingAgentId}</KV>
      <KV label="Action">{request.actionId}</KV>
      <KV label="Policy">{policyName}</KV>
      <KV label="Timestamp">{new Date(request.createdUtc).toLocaleString()}</KV>
      <KV label="Payload hash" mono>{request.requestPayloadHash}</KV>
      <KV label="Correlation id" mono>{request.correlationId}</KV>
      {receipt && <>
        <KV label="Receipt" mono>{receipt.receiptNumber}</KV>
        <KV label="Event hash" mono>{receipt.receiptHash}</KV>
        <KV label="Prev hash" mono>{receipt.previousReceiptHash ?? '— (first)'}</KV>
      </>}
      {v === 'loading' && <p className="muted">Verifying…</p>}
      {v && v !== 'loading' && (
        <div className={`verifybox ${v.verified ? 'ok' : 'bad'}`}>
          <div className="verify-head">{v.verified ? <><svg className="vi" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.4} strokeLinecap="round" strokeLinejoin="round"><path d="M20 6 9 17l-5-5" /></svg> Verified</> : <><svg className="vi" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.4} strokeLinecap="round"><path d="M6 6l12 12M18 6 6 18" /></svg> Verification failed</>}</div>
          <KV label="Content integrity">{v.integrityOk ? 'Valid' : 'TAMPERED'}</KV>
          <KV label="Signature">{v.signed ? (v.signatureOk ? 'Valid' : 'INVALID') : 'unsigned'}</KV>
        </div>
      )}
    </Drawer>
  )
}
