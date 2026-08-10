import { useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatusPill, Loading, EmptyState } from '../components/ui'
import type { DeadLetterItem } from '../types'

export function DeadLetter() {
  const list = useAsync<DeadLetterItem[]>(() => api('/api/admin/dead-letter'))
  const [busy, setBusy] = useState<string>()
  const [msg, setMsg] = useState<string>()

  async function triage(executionId: string, action: 'replay' | 'discard') {
    setBusy(executionId); setMsg(undefined)
    try {
      await api(`/api/admin/dead-letter/${executionId}/${action}`, { method: 'POST' })
      setMsg(`Execution ${executionId.slice(0, 8)}… → ${action === 'replay' ? 'Replayed (requeued)' : 'Discarded'}`)
      list.reload()
    } catch (e) { setMsg(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(undefined) }
  }

  const pending = list.data?.filter((d) => d.status === 'Pending').length ?? 0

  return (
    <div>
      <PageHeader title="Dead-letter Queue"
        subtitle="Executions that failed to complete against a target — captured durably for inspection and safe replay."
        actions={<button className="btn ghost" onClick={() => list.reload()}>Refresh</button>} />

      <div className="tiles">
        <div className={`stat ${pending ? 'approval' : 'allow'} static`}>
          <div className="stat-value">{list.loading ? '…' : pending}</div>
          <div className="stat-label">Pending triage</div>
          <div className="stat-sub">{list.data?.length ?? 0} total captured</div>
        </div>
      </div>
      {msg && <p className="success">{msg}</p>}

      <section className="card">
        {list.loading ? <Loading what="dead-letter queue" /> : (list.data?.length ? (
          <table className="table">
            <thead><tr><th>Adapter</th><th>Error</th><th>Failed</th><th>Status</th><th>Reference</th><th></th></tr></thead>
            <tbody>
              {list.data.map((d) => (
                <tr key={d.executionId}>
                  <td className="mono">{d.adapterCode}</td>
                  <td><div className="mono">{d.errorCode ?? '—'}</div><div className="muted small">{d.errorMessage}</div></td>
                  <td className="muted">{new Date(d.startedUtc).toLocaleString()}</td>
                  <td><StatusPill s={d.status === 'Pending' ? 'PENDING' : d.status === 'Replayed' ? 'ACTIVE' : 'DENIED'} /></td>
                  <td className="mono ellipsis" title={d.actionRequestId}>{d.actionRequestId.slice(0, 8)}…</td>
                  <td className="right">
                    <button className="btn ghost" disabled={busy === d.executionId || d.status !== 'Pending'} onClick={() => triage(d.executionId, 'replay')}>Replay</button>
                    <button className="btn ghost danger" disabled={busy === d.executionId || d.status !== 'Pending'} onClick={() => triage(d.executionId, 'discard')}>Discard</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="Dead-letter queue is empty" hint="Executions that fail against a target are captured here for triage." />)}
      </section>
    </div>
  )
}
