import { useState } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent, ReactNode } from 'react'

/** A copy-to-clipboard chip: shows a value (mono) and copies it on click, confirming briefly. */
export function CopyChip({ text, label }: { text: string; label?: string }) {
  const [done, setDone] = useState(false)
  return (
    <button type="button" className={`copychip ${done ? 'done' : ''}`} aria-label={`Copy ${label ?? text}`}
      onClick={async () => { try { await navigator.clipboard.writeText(text); setDone(true); setTimeout(() => setDone(false), 1400) } catch { /* clipboard blocked */ } }}>
      <span className="mono">{label ?? text}</span>
      {done
        ? <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.6} strokeLinecap="round" strokeLinejoin="round"><path d="M20 6 9 17l-5-5" /></svg>
        : <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.9} strokeLinecap="round" strokeLinejoin="round"><rect x="9" y="9" width="11" height="11" rx="2" /><path d="M5 15V5a2 2 0 0 1 2-2h10" /></svg>}
    </button>
  )
}

/**
 * Makes a non-button element (a clickable table row/cell) behave like a button for keyboard users too:
 * focusable, and activatable with Enter/Space — not just the mouse. Spread onto the element.
 */
export function rowActivate(fn: () => void, label?: string) {
  return {
    role: 'button' as const,
    tabIndex: 0,
    'aria-label': label,
    onClick: fn,
    onKeyDown: (e: ReactKeyboardEvent) => {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fn() }
    },
  }
}

export function PageHeader({ title, subtitle, actions }:
  { title: string; subtitle?: string; actions?: ReactNode }) {
  return (
    <div className="page-header">
      <div>
        <h1 className="page-title">{title}</h1>
        {subtitle && <p className="page-sub">{subtitle}</p>}
      </div>
      {actions && <div className="page-actions">{actions}</div>}
    </div>
  )
}

export function StatCard({ label, value, sub, tone, icon, caption, onClick }:
  { label: string; value: ReactNode; sub?: string; tone?: 'allow' | 'deny' | 'approval' | 'neutral'; icon?: ReactNode; caption?: string; onClick?: () => void }) {
  const t = tone ?? 'neutral'
  return (
    <button className={`stat ${t} ${onClick ? 'clickable' : ''}`} onClick={onClick} disabled={!onClick}>
      <div className="stat-top"><span className={`stat-chip ${t}`}>{icon ?? statGlyph(t)}</span></div>
      <div className="stat-value">{value}</div>
      <div className="stat-label">{label}</div>
      {caption && <div className="stat-caption">{caption}</div>}
      {sub && <div className="stat-sub">{sub}</div>}
    </button>
  )
}

/** Reassurance-first banner: is everything protected, or does something need a look. */
export function StatusStrip({ safe, title, detail }: { safe: boolean; title: string; detail: string }) {
  const p = { width: 22, height: 22, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
    strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const }
  return (
    <div className={`status-strip ${safe ? '' : 'attention'}`}>
      <span className="status-icon">
        {safe
          ? <svg {...p}><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" /><path d="M9 12l2 2 4-4" /></svg>
          : <svg {...p}><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z" /><path d="M12 9v4M12 17h.01" /></svg>}
      </span>
      <div className="status-text"><strong>{title}</strong><p>{detail}</p></div>
    </div>
  )
}

// Tone-based glyph for a KPI chip. Keeps every stat visually anchored (like a metric icon).
function statGlyph(tone: string) {
  const p = { width: 17, height: 17, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
    strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const }
  if (tone === 'allow') return <svg {...p}><path d="M20 6 9 17l-5-5" /></svg>
  if (tone === 'deny') return <svg {...p}><circle cx="12" cy="12" r="9" /><path d="M15 9l-6 6M9 9l6 6" /></svg>
  if (tone === 'approval') return <svg {...p}><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></svg>
  return <svg {...p}><path d="M3 12h4l2 6 4-14 2 8h6" /></svg>
}

// Permit outcomes: ALLOW→Allow, REQUIRE_APPROVAL→Step-up, DENY→Deny.
const OUTCOME: Record<string, { cls: string; label: string }> = {
  ALLOW: { cls: 'allow', label: 'Allow' },
  DENY: { cls: 'deny', label: 'Deny' },
  REQUIRE_APPROVAL: { cls: 'approval', label: 'Step-up' },
  SUCCEEDED: { cls: 'allow', label: 'Allow' },
  EXECUTED: { cls: 'allow', label: 'Allow' },
  DENIED: { cls: 'deny', label: 'Deny' },
  REJECTED: { cls: 'deny', label: 'Deny' },
  FAILED: { cls: 'deny', label: 'Failed' },
  AWAITING_APPROVAL: { cls: 'approval', label: 'Step-up' },
  PENDINGAPPROVAL: { cls: 'approval', label: 'Step-up' },
}
export function DecisionPill({ d }: { d: string }) {
  const o = OUTCOME[(d || '').toUpperCase()] ?? { cls: 'neutral', label: (d || '—').replace(/_/g, ' ') }
  return <span className={`pill ${o.cls}`}>{o.label}</span>
}

export function RiskLabel({ risk }: { risk?: string }) {
  const r = (risk || '').toLowerCase()
  const cls = r === 'low' ? 'risk-low' : r === 'medium' ? 'risk-medium' : r === 'high' ? 'risk-high' : 'muted'
  return <span className={cls}>{risk || '—'}</span>
}

const STATUS_TONE: Record<string, string> = {
  SUCCEEDED: 'allow', ALLOW: 'allow', HEALTHY: 'allow', APPROVED: 'allow', ACTIVE: 'allow',
  DENIED: 'deny', DENY: 'deny', REJECTED: 'deny', FAILED: 'deny', UNAVAILABLE: 'deny', REVOKED: 'deny', UNHEALTHY: 'deny',
  AWAITING_APPROVAL: 'approval', PENDING: 'approval', DEGRADED: 'approval', SUSPENDED: 'approval', EXPIRED: 'approval',
}
export function StatusPill({ s }: { s: string }) {
  const k = STATUS_TONE[s?.toUpperCase()] ?? 'neutral'
  return <span className={`pill ${k}`}>{(s || '—').replace(/_/g, ' ')}</span>
}

export function Drawer({ title, onClose, children, footer, wide }:
  { title: string; onClose: () => void; children: ReactNode; footer?: ReactNode; wide?: boolean }) {
  return (
    <div className="drawer-scrim" onClick={onClose}>
      <aside className={`drawer ${wide ? 'wide' : ''}`} onClick={(e) => e.stopPropagation()}>
        <div className="drawer-head">
          <h2>{title}</h2>
          <button className="btn ghost" onClick={onClose} aria-label="Close">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.2} strokeLinecap="round"><path d="M6 6l12 12M18 6 6 18" /></svg>
          </button>
        </div>
        <div className="drawer-body">{children}</div>
        {footer && <div className="drawer-foot">{footer}</div>}
      </aside>
    </div>
  )
}

export function KV({ label, children, mono }: { label: string; children: ReactNode; mono?: boolean }) {
  return (
    <div className="kv">
      <span>{label}</span>
      <b className={mono ? 'mono' : undefined}>{children}</b>
    </div>
  )
}

export function Loading({ what }: { what?: string }) {
  return <div className="loading">Loading {what ?? 'data'}…</div>
}

export function EmptyState({ title, hint }: { title: string; hint?: string }) {
  return (
    <div className="empty">
      <div className="empty-title">{title}</div>
      {hint && <div className="empty-hint">{hint}</div>}
    </div>
  )
}

/** Described roadmap module — makes the IA read as an intentional product surface. */
export function ComingSoon({ title, blurb, delivers }: { title: string; blurb: string; delivers: string[] }) {
  return (
    <div>
      <PageHeader title={title} subtitle="Planned module" />
      <div className="card roadmap">
        <span className="badge roadmap-badge">On the roadmap</span>
        <p className="muted">{blurb}</p>
        <div className="delivers">
          {delivers.map((d) => <div key={d} className="deliver"><span className="dot" />{d}</div>)}
        </div>
      </div>
    </div>
  )
}
