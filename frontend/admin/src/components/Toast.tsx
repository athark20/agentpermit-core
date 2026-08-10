import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'

export type ToastKind = 'info' | 'success' | 'warning' | 'error'
export interface ToastInput { kind: ToastKind; title: string; detail?: string; code?: string; traceId?: string; duration?: number }
interface Toast extends ToastInput { id: number }

// Module-level bus — lets non-React code (the api layer, window error handlers) raise toasts.
type Listener = (t: ToastInput) => void
const listeners = new Set<Listener>()
let recent: { key: string; at: number } | null = null
/** Raise a toast from anywhere. De-dupes identical toasts fired within 1.2s (avoids storms). */
export function pushToast(t: ToastInput) {
  const key = `${t.kind}:${t.title}:${t.detail ?? ''}`
  const now = Date.now()
  if (recent && recent.key === key && now - recent.at < 1200) return
  recent = { key, at: now }
  listeners.forEach((l) => l(t))
}

interface ToastApi {
  show: (t: ToastInput) => void
  info: (title: string, detail?: string) => void
  success: (title: string, detail?: string) => void
  warning: (title: string, detail?: string) => void
  error: (title: string, detail?: string, extra?: { code?: string; traceId?: string }) => void
}
const Ctx = createContext<ToastApi | null>(null)

/** In-app notifications for info / success / warning / error, with the exact reason + a support reference. */
export function useToast(): ToastApi {
  const c = useContext(Ctx)
  if (!c) throw new Error('useToast must be used within <ToastProvider>')
  return c
}

let seq = 0
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const dismiss = useCallback((id: number) => setToasts((list) => list.filter((x) => x.id !== id)), [])
  const add = useCallback((t: ToastInput) => {
    setToasts((list) => [...list.slice(-4), { id: ++seq, duration: t.kind === 'error' ? 9000 : 4500, ...t }])
  }, [])

  // Bridge the module bus into React state while mounted.
  useEffect(() => { listeners.add(add); return () => { listeners.delete(add) } }, [add])

  const api: ToastApi = {
    show: add,
    info: (title, detail) => add({ kind: 'info', title, detail }),
    success: (title, detail) => add({ kind: 'success', title, detail }),
    warning: (title, detail) => add({ kind: 'warning', title, detail }),
    error: (title, detail, extra) => add({ kind: 'error', title, detail, ...extra }),
  }
  return (
    <Ctx.Provider value={api}>
      {children}
      <div className="toast-host" role="region" aria-label="Notifications">
        {toasts.map((t) => <ToastItem key={t.id} t={t} onClose={() => dismiss(t.id)} />)}
      </div>
    </Ctx.Provider>
  )
}

function ToastItem({ t, onClose }: { t: Toast; onClose: () => void }) {
  const [paused, setPaused] = useState(false)
  const timer = useRef<number>(0)
  useEffect(() => {
    if (paused || !t.duration) return
    timer.current = window.setTimeout(onClose, t.duration)
    return () => window.clearTimeout(timer.current)
  }, [paused, t.duration, onClose])
  return (
    <div className={`toast ${t.kind}`} role={t.kind === 'error' ? 'alert' : 'status'}
      onMouseEnter={() => setPaused(true)} onMouseLeave={() => setPaused(false)}>
      <span className="toast-icon">{glyph(t.kind)}</span>
      <div className="toast-body">
        <div className="toast-title">{t.title}</div>
        {t.detail && <div className="toast-detail">{t.detail}</div>}
        {t.traceId && <div className="toast-ref">Reference <span className="mono">{t.traceId}</span></div>}
      </div>
      <button className="toast-close" onClick={onClose} aria-label="Dismiss">
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.2} strokeLinecap="round"><path d="M6 6l12 12M18 6 6 18" /></svg>
      </button>
    </div>
  )
}

function glyph(kind: ToastKind) {
  const p = { width: 18, height: 18, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
    strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const }
  if (kind === 'success') return <svg {...p}><path d="M20 6 9 17l-5-5" /></svg>
  if (kind === 'error') return <svg {...p}><circle cx="12" cy="12" r="9" /><path d="M15 9l-6 6M9 9l6 6" /></svg>
  if (kind === 'warning') return <svg {...p}><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z" /><path d="M12 9v4M12 17h.01" /></svg>
  return <svg {...p}><circle cx="12" cy="12" r="9" /><path d="M12 11v5M12 8h.01" /></svg>
}
