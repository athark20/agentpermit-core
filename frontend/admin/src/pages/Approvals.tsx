import { useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, Drawer, KV, StatusPill, Loading, EmptyState } from '../components/ui'

interface Approval {
  approvalRequestId: string
  actionRequestId: string
  actionId: string
  agentId: string
  amount?: number
  currency?: string
  customerId?: string
  reason?: string
  status: string
  approverRole?: string
  payloadHash: string
  requestedAtUtc: string
  expiresAtUtc?: string
}

export function Approvals() {
  const list = useAsync<Approval[]>(() => api('/api/approvals/pending'))
  const [open, setOpen] = useState<Approval>()
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string }>()

  async function decide(a: Approval, decision: 'approve' | 'reject') {
    setBusy(true); setMsg(undefined)
    try {
      const res = await api<{ outcome: string }>(`/api/approvals/${a.approvalRequestId}/${decision}`, {
        method: 'POST', body: JSON.stringify({ comment: `${decision} via admin console` }),
      })
      setMsg({ ok: true, text: `${a.actionId}: ${res.outcome}` })
      setOpen(undefined); list.reload()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof Error ? e.message : String(e) })
    } finally { setBusy(false) }
  }

  return (
    <div>
      <PageHeader title="Approval Centre"
        subtitle="Requests where policy required human approval. Approving authorizes the exact frozen payload." />
      {msg && <p className={msg.ok ? 'success' : 'error'}>{msg.text}</p>}

      <section className="card">
        {list.loading ? <Loading what="approvals" /> : (list.data?.length ? (
          <table className="table">
            <thead><tr><th>Agent</th><th>Action</th><th>Amount</th><th>Customer</th><th>Status</th><th>Requested</th><th>Expires</th><th></th></tr></thead>
            <tbody>
              {list.data.map((a) => (
                <tr key={a.approvalRequestId}>
                  <td>{a.agentId}</td>
                  <td><b>{a.actionId}</b></td>
                  <td>{a.amount != null ? `${a.currency ?? ''} ${a.amount}` : '—'}</td>
                  <td>{a.customerId ?? '—'}</td>
                  <td><StatusPill s={a.status} /></td>
                  <td className="muted">{new Date(a.requestedAtUtc).toLocaleString()}</td>
                  <td className="muted">{a.expiresAtUtc ? new Date(a.expiresAtUtc).toLocaleString() : '—'}</td>
                  <td className="right"><button className="btn ghost" onClick={() => setOpen(a)}>Review</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No pending approvals" hint="Requests that hit a REQUIRE_APPROVAL rule will appear here." />)}
      </section>

      {open && (
        <Drawer title={`Approval — ${open.actionId}`} onClose={() => setOpen(undefined)}
          footer={
            <div className="row">
              <button className="btn primary" disabled={busy} onClick={() => decide(open, 'approve')}>Approve</button>
              <button className="btn danger" disabled={busy} onClick={() => decide(open, 'reject')}>Reject</button>
            </div>
          }>
          <KV label="Agent">{open.agentId}</KV>
          <KV label="Action">{open.actionId}</KV>
          <KV label="Amount">{open.amount != null ? `${open.currency ?? ''} ${open.amount}` : '—'}</KV>
          <KV label="Customer">{open.customerId ?? '—'}</KV>
          <KV label="Reason">{open.reason ?? '—'}</KV>
          <KV label="Approver role">{open.approverRole ?? '—'}</KV>
          <KV label="Status"><StatusPill s={open.status} /></KV>
          <KV label="Requested">{new Date(open.requestedAtUtc).toLocaleString()}</KV>
          <KV label="Expires">{open.expiresAtUtc ? new Date(open.expiresAtUtc).toLocaleString() : '—'}</KV>
          <KV label="Action request id" mono>{open.actionRequestId}</KV>
          <KV label="Frozen payload hash" mono>{open.payloadHash}</KV>
          <p className="muted small">You are approving this exact payload hash. If the payload changed after the
            request was frozen, execution is refused (tamper protection).</p>
        </Drawer>
      )}
    </div>
  )
}
