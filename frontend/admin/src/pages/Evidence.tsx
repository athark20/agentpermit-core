import { useMemo, useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, Drawer, KV, Loading, EmptyState, rowActivate } from '../components/ui'
import type { Receipt, VerifyResult } from '../types'

export function Evidence() {
  const list = useAsync<Receipt[]>(() => api('/api/admin/receipts?take=100'))
  const [q, setQ] = useState('')
  const [signedOnly, setSignedOnly] = useState<'all' | 'signed' | 'unsigned'>('all')
  const [open, setOpen] = useState<Receipt>()

  const filtered = useMemo(() => (list.data ?? []).filter((r) => {
    if (signedOnly === 'signed' && !r.signed) return false
    if (signedOnly === 'unsigned' && r.signed) return false
    if (q && !(`${r.receiptNumber} ${r.receiptHash} ${r.actionRequestId}`.toLowerCase().includes(q.toLowerCase()))) return false
    return true
  }), [list.data, q, signedOnly])

  return (
    <div>
      <PageHeader title="Evidence trail"
        subtitle="Every executed action produces a signed, hash-chained receipt — independently verifiable."
        actions={<button className="btn ghost" onClick={() => list.reload()}>Refresh</button>} />

      <div className="toolbar">
        <input className="search" type="search" aria-label="Search evidence receipts" placeholder="Search receipt #, hash or request id…" value={q} onChange={(e) => setQ(e.target.value)} />
        <select value={signedOnly} onChange={(e) => setSignedOnly(e.target.value as typeof signedOnly)}>
          <option value="all">All</option><option value="signed">Signed</option><option value="unsigned">Unsigned</option>
        </select>
        <span className="muted count">{filtered.length} of {list.data?.length ?? 0}</span>
      </div>

      <section className="card">
        {list.loading ? <Loading what="evidence" /> : (filtered.length ? (
          <table className="table">
            <thead><tr><th>Receipt</th><th>Hash</th><th>Signed</th><th>When</th><th></th></tr></thead>
            <tbody>
              {filtered.map((r) => (
                <tr key={r.actionReceiptId} className="rowlink" {...rowActivate(() => setOpen(r), `View receipt ${r.receiptNumber}`)}>
                  <td className="mono">{r.receiptNumber}</td>
                  <td className="mono ellipsis" title={r.receiptHash}>{r.receiptHash.slice(0, 18)}…</td>
                  <td>{r.signed ? '✓' : <span className="muted" title="Generated before signing was enabled">unsigned</span>}</td>
                  <td className="muted">{new Date(r.createdUtc).toLocaleString()}</td>
                  <td className="right"><button className="btn ghost" onClick={(e) => { e.stopPropagation(); setOpen(r) }}>Open</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No matching receipts" hint="Executed actions produce receipts; adjust filters to see them." />)}
      </section>

      {open && <ReceiptDrawer receipt={open} onClose={() => setOpen(undefined)} />}
    </div>
  )
}

function ReceiptDrawer({ receipt, onClose }: { receipt: Receipt; onClose: () => void }) {
  const [v, setV] = useState<VerifyResult | 'loading'>()
  const [err, setErr] = useState<string>()

  async function verify() {
    setV('loading'); setErr(undefined)
    try { setV(await api<VerifyResult>(`/api/admin/receipts/${receipt.actionRequestId}/verify`)) }
    catch (e) { setErr(e instanceof Error ? e.message : String(e)); setV(undefined) }
  }

  return (
    <Drawer title={receipt.receiptNumber} onClose={onClose}
      footer={<button className="btn primary" onClick={verify}>Re-verify this receipt</button>}>
      <KV label="Receipt number" mono>{receipt.receiptNumber}</KV>
      <KV label="Action request id" mono>{receipt.actionRequestId}</KV>
      <KV label="Content hash" mono>{receipt.receiptHash}</KV>
      <KV label="Previous hash (chain)" mono>{receipt.previousReceiptHash ?? '— (first in chain)'}</KV>
      <KV label="Signed">{receipt.signed ? 'yes — Key Vault (RS256)' : 'no'}</KV>
      <KV label="Created">{new Date(receipt.createdUtc).toLocaleString()}</KV>
      {!receipt.signed && <p className="muted small">Unsigned — this receipt was generated before receipt signing
        was enabled. Its content is still hash-chained and tamper-evident.</p>}

      {v === 'loading' && <p className="muted">Verifying…</p>}
      {err && <p className="error">{err}</p>}
      {v && v !== 'loading' && (
        <div className={`verifybox ${v.verified ? 'ok' : 'bad'}`}>
          <div className="verify-head">{v.verified ? <><svg className="vi" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.4} strokeLinecap="round" strokeLinejoin="round"><path d="M20 6 9 17l-5-5" /></svg> Verified</> : <><svg className="vi" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.4} strokeLinecap="round"><path d="M6 6l12 12M18 6 6 18" /></svg> Verification failed</>}</div>
          <KV label="Content integrity">{v.integrityOk === true ? 'Valid' : v.integrityOk === false ? 'TAMPERED' : 'n/a'}</KV>
          <KV label="Signature">{v.signed ? (v.signatureOk === true ? 'Valid' : v.signatureOk === false ? 'INVALID' : 'n/a') : 'unsigned'}</KV>
          <KV label="Signing key">{v.signed ? 'Key Vault receipt-signing-key (RS256)' : '—'}</KV>
          <KV label="Recomputed hash" mono>{v.recomputedHash?.slice(0, 32)}…</KV>
        </div>
      )}
    </Drawer>
  )
}
