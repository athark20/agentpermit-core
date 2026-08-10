import { useEffect, useState } from 'react'
import { api, ApiError, reasonOf } from '../api'
import { useToast } from './Toast'
import { useAccess } from '../access'

interface Safety { killSwitch: { frozen: boolean; reason?: string; actor?: string; sinceUtc?: string } }

/**
 * Always-visible fleet-wide kill switch in the top bar. Freezing denies EVERY agent action
 * (SYSTEM_FROZEN), overriding all rules — the break-glass control for an incident. Reuses the
 * existing /api/admin/safety endpoints; state is polled so it reflects a freeze from anywhere.
 */
export function EmergencyStop() {
  const toast = useToast()
  const access = useAccess()
  const canAct = access.isAdmin || access.canManageAgents
  const [ks, setKs] = useState<Safety['killSwitch'] | null>(null)
  const [dialog, setDialog] = useState<null | 'stop' | 'resume'>(null)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)

  async function load() {
    try { const s = await api<Safety>('/api/admin/safety'); setKs(s.killSwitch) } catch { /* keep last known */ }
  }
  useEffect(() => { load(); const t = setInterval(load, 25000); return () => clearInterval(t) }, [])

  async function stop() {
    setBusy(true)
    try {
      await api('/api/admin/safety/freeze', { method: 'POST', body: JSON.stringify({ reason: reason.trim() || 'Emergency stop' }) })
      toast.warning('All agents stopped', 'Every action is denied fleet-wide. Resume once the incident is resolved.')
      setDialog(null); setReason(''); load()
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not stop agents', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    } finally { setBusy(false) }
  }
  async function resume() {
    setBusy(true)
    try {
      await api('/api/admin/safety/unfreeze', { method: 'POST' })
      toast.success('Agents resumed', 'Actions are evaluated by your rules again.')
      setDialog(null); load()
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not resume', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    } finally { setBusy(false) }
  }

  if (ks === null) return null
  const frozen = ks.frozen
  // Non-privileged users still see the frozen state (read-only), but can't act.
  if (!frozen && !canAct) return null

  return (
    <>
      {frozen
        ? <button className="estop-btn frozen" onClick={() => canAct && setDialog('resume')} title={ks.reason ? `Stopped: ${ks.reason}` : 'All agents stopped'} disabled={!canAct}>
            <span className="estop-dot" /> Agents stopped{canAct ? ' · Resume' : ''}
          </button>
        : <button className="estop-btn" onClick={() => setDialog('stop')} title="Stop every agent immediately">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.2} strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="9" /><rect x="9" y="9" width="6" height="6" rx="1" fill="currentColor" stroke="none" /></svg>
            Emergency stop
          </button>}

      {dialog && (
        <div className="modal-scrim" onClick={() => !busy && setDialog(null)}>
          <div className="modal" onClick={(e) => e.stopPropagation()} role="dialog" aria-modal="true">
            {dialog === 'stop' ? (
              <>
                <h3 className="modal-title danger">Stop all agents?</h3>
                <p className="muted small" style={{ margin: '6px 0 14px' }}>
                  This immediately <b>denies every agent action</b> across the fleet, overriding all rules
                  (SYSTEM_FROZEN). Use it during an incident. Nothing is deleted — all evidence is kept and you can
                  resume anytime.
                </p>
                <label>Reason <span className="muted small">(recorded in the audit log)</span>
                  <input autoFocus value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Suspected compromised agent" onKeyDown={(e) => e.key === 'Enter' && stop()} />
                </label>
                <div className="row" style={{ marginTop: 16, gap: 10 }}>
                  <button className="btn" onClick={() => setDialog(null)} disabled={busy}>Cancel</button>
                  <button className="btn danger" style={{ marginLeft: 'auto' }} onClick={stop} disabled={busy}>{busy ? 'Stopping…' : 'Stop all agents'}</button>
                </div>
              </>
            ) : (
              <>
                <h3 className="modal-title">Resume all agents?</h3>
                <p className="muted small" style={{ margin: '6px 0 14px' }}>
                  Actions will be evaluated by your rules again.{ks.reason ? ` Stopped because: “${ks.reason}”.` : ''}
                </p>
                <div className="row" style={{ marginTop: 16, gap: 10 }}>
                  <button className="btn" onClick={() => setDialog(null)} disabled={busy}>Cancel</button>
                  <button className="btn primary" style={{ marginLeft: 'auto' }} onClick={resume} disabled={busy}>{busy ? 'Resuming…' : 'Resume agents'}</button>
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </>
  )
}
