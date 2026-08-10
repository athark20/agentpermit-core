import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { api, ApiError } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, Loading } from '../components/ui'
import { Intro, timeAgo } from '../plain'

interface Member { email: string; displayName: string }
interface Role {
  key: string; name: string; description: string; members: Member[]
  entraGroupId?: string; lastSyncedUtc?: string
  lastAdded?: number; lastRemoved?: number; lastUnresolved?: string[]; appliedMemberCount?: number
}
interface Status { configured: boolean; consented: boolean; tenantId?: string; consentUrl?: string; message: string }

function problemMessage(e: unknown): string {
  if (e instanceof ApiError) {
    try { const p = JSON.parse(e.message); return p.detail || p.title || e.message } catch { return e.message }
  }
  return e instanceof Error ? e.message : String(e)
}

export function RolesAccess() {
  const status = useAsync<Status>(() => api('/api/admin/access/status'))
  const roles = useAsync<Role[]>(() => api('/api/admin/access'))
  const [local, setLocal] = useState<Record<string, Member[]>>({})
  const [dirty, setDirty] = useState<Record<string, boolean>>({})
  const [busy, setBusy] = useState<string>()
  const [msg, setMsg] = useState<Record<string, { ok: boolean; text: string }>>({})

  useEffect(() => {
    if (!roles.data) return
    const m: Record<string, Member[]> = {}
    for (const r of roles.data) m[r.key] = r.members ?? []
    setLocal(m); setDirty({})
  }, [roles.data])

  const st = status.data
  const canApply = !!st?.consented

  function setMembers(key: string, members: Member[]) {
    setLocal((l) => ({ ...l, [key]: members }))
    setDirty((d) => ({ ...d, [key]: true }))
    setMsg((mm) => ({ ...mm, [key]: undefined as never }))
  }

  async function save(key: string) {
    setBusy(`save-${key}`)
    try {
      await api(`/api/admin/access/${key}`, { method: 'PUT', body: JSON.stringify({ members: local[key] ?? [] }) })
      setDirty((d) => ({ ...d, [key]: false }))
      setMsg((mm) => ({ ...mm, [key]: { ok: true, text: 'Saved.' } }))
    } catch (e) { setMsg((mm) => ({ ...mm, [key]: { ok: false, text: problemMessage(e) } })) }
    finally { setBusy(undefined) }
  }

  async function apply(key: string) {
    setBusy(`apply-${key}`)
    try {
      // Save first so what we apply is exactly what's on screen.
      await api(`/api/admin/access/${key}`, { method: 'PUT', body: JSON.stringify({ members: local[key] ?? [] }) })
      const r = await api<{ added: number; removed: number; unresolved: string[]; memberCount: number }>(`/api/admin/access/${key}/sync`, { method: 'POST' })
      setDirty((d) => ({ ...d, [key]: false }))
      const bits = [`${r.memberCount} ${r.memberCount === 1 ? 'person' : 'people'} now in Microsoft Entra`]
      if (r.added) bits.push(`${r.added} added`)
      if (r.removed) bits.push(`${r.removed} removed`)
      let text = bits.join(' · ') + '.'
      if (r.unresolved?.length) text += ` Couldn't find: ${r.unresolved.join(', ')}.`
      setMsg((mm) => ({ ...mm, [key]: { ok: true, text } }))
      roles.reload()
    } catch (e) { setMsg((mm) => ({ ...mm, [key]: { ok: false, text: problemMessage(e) } })) }
    finally { setBusy(undefined) }
  }

  if (roles.loading) return <div><PageHeader title="Roles & access" /><Loading what="roles" /></div>

  return (
    <div>
      <PageHeader title="Roles & access" />
      <Intro>Decide what each person can do in AgentPermit. You manage everyone here, and AgentPermit keeps Microsoft Entra in step for you. Changes always flow <b>from here to Entra</b> — never the other way.</Intro>

      {/* Connection to Entra */}
      {st && (
        <section className="card" style={{ borderLeft: `4px solid var(--${st.consented ? 'allow' : st.configured ? 'approval' : 'deny'})` }}>
          <b style={{ fontSize: 15 }}>
            {st.consented ? 'Connected to Microsoft Entra' : st.configured ? 'One-time approval needed' : 'Not connected yet'}
          </b>
          <p className="muted small" style={{ margin: '6px 0 0', maxWidth: 640 }}>{st.message}</p>
          {st.configured && !st.consented && (
            <div className="row" style={{ marginTop: 12 }}>
              <button className="btn primary" onClick={() => st.consentUrl && window.open(st.consentUrl, '_blank')}>Approve in Microsoft</button>
              <button className="btn ghost" onClick={() => status.reload()}>Check again</button>
            </div>
          )}
          {!st.configured && (
            <p className="small" style={{ marginTop: 10 }}><a href="#/onboarding">Connect your environment</a> to get started.</p>
          )}
        </section>
      )}

      {/* One card per role */}
      <div className="stack" style={{ marginTop: 4 }}>
        {(roles.data ?? []).map((role) => {
          const members = local[role.key] ?? []
          const isDirty = dirty[role.key]
          const m = msg[role.key]
          return (
            <section className="card" key={role.key}>
              <div className="row spread" style={{ marginTop: 0, alignItems: 'flex-start' }}>
                <div>
                  <div style={{ fontWeight: 600, fontSize: 15 }}>{role.name}</div>
                  <div className="muted small" style={{ marginTop: 3, maxWidth: 560, lineHeight: 1.5 }}>{role.description}</div>
                </div>
                <span className="chip">{members.length} {members.length === 1 ? 'person' : 'people'}</span>
              </div>

              <div className="member-list" style={{ marginTop: 12 }}>
                {members.length === 0 && <p className="muted small">No one has this role yet.</p>}
                {members.map((mem, i) => (
                  <div className="member-row" key={i}>
                    <div>
                      <div style={{ fontWeight: 500 }}>{mem.displayName || mem.email}</div>
                      {mem.displayName && <div className="muted small">{mem.email}</div>}
                    </div>
                    <button className="btn ghost danger small" onClick={() => setMembers(role.key, members.filter((_, j) => j !== i))}>Remove</button>
                  </div>
                ))}
              </div>

              <AddPerson onAdd={(mem) => setMembers(role.key, [...members.filter((x) => x.email.toLowerCase() !== mem.email.toLowerCase()), mem])} />

              <div className="row" style={{ marginTop: 14, borderTop: '1px solid var(--line)', paddingTop: 12, alignItems: 'center' }}>
                <div className="muted small">
                  {role.lastSyncedUtc
                    ? <>In Entra: {role.appliedMemberCount ?? 0} {role.appliedMemberCount === 1 ? 'person' : 'people'} · applied {timeAgo(role.lastSyncedUtc)}</>
                    : <>Not applied to Entra yet.</>}
                </div>
                <div className="row" style={{ marginLeft: 'auto', gap: 8 }}>
                  <button className="btn ghost" disabled={busy === `save-${role.key}` || !isDirty} onClick={() => save(role.key)}>
                    {busy === `save-${role.key}` ? 'Saving…' : 'Save'}
                  </button>
                  <button className="btn primary" disabled={busy === `apply-${role.key}` || !canApply} onClick={() => apply(role.key)}
                    title={canApply ? 'Apply these people to Microsoft Entra' : 'Approve AgentPermit in Microsoft first'}>
                    {busy === `apply-${role.key}` ? 'Applying…' : 'Apply in Entra'}
                  </button>
                </div>
              </div>
              {m && <p className={m.ok ? 'success' : 'error'} style={{ marginTop: 8 }}>{m.text}</p>}
            </section>
          )
        })}
      </div>
    </div>
  )
}

interface DirUser { displayName: string; email: string; jobTitle?: string }

function initials(s: string): string {
  const parts = s.replace(/@.*/, '').split(/[.\s_-]+/).filter(Boolean)
  return (((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase()) || s[0]?.toUpperCase() || '?'
}

// Type-ahead people-picker: suggests users from Microsoft Entra as you type; falls back to adding a
// full email by hand when the directory has no match (or isn't connected yet).
function AddPerson({ onAdd }: { onAdd: (m: Member) => void }) {
  const [q, setQ] = useState('')
  const [results, setResults] = useState<DirUser[]>([])
  const [open, setOpen] = useState(false)
  const [loading, setLoading] = useState(false)
  const [active, setActive] = useState(0)
  const box = useRef<HTMLDivElement>(null)
  const validEmail = /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(q.trim())

  useEffect(() => {
    const term = q.trim()
    if (term.length < 2) { setResults([]); setOpen(false); setLoading(false); return }
    setLoading(true)
    const t = setTimeout(async () => {
      try {
        const r = await api<DirUser[]>(`/api/admin/access/directory/search?q=${encodeURIComponent(term)}`)
        setResults(r); setActive(0); setOpen(true)
      } catch { setResults([]); setOpen(true) }
      finally { setLoading(false) }
    }, 250)
    return () => clearTimeout(t)
  }, [q])

  useEffect(() => {
    function onDoc(e: MouseEvent) { if (box.current && !box.current.contains(e.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', onDoc)
    return () => document.removeEventListener('mousedown', onDoc)
  }, [])

  function pick(u: DirUser) { onAdd({ email: u.email, displayName: u.displayName }); setQ(''); setResults([]); setOpen(false) }
  function addManual() { if (validEmail) { onAdd({ email: q.trim(), displayName: '' }); setQ(''); setResults([]); setOpen(false) } }

  function onKey(e: KeyboardEvent<HTMLInputElement>) {
    if (open && results.length) {
      if (e.key === 'ArrowDown') { e.preventDefault(); setActive((a) => Math.min(a + 1, results.length - 1)); return }
      if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => Math.max(a - 1, 0)); return }
      if (e.key === 'Enter') { e.preventDefault(); pick(results[active]); return }
    }
    if (e.key === 'Enter') { e.preventDefault(); addManual() }
    if (e.key === 'Escape') setOpen(false)
  }

  return (
    <div className="typeahead" ref={box} style={{ marginTop: 10 }}>
      <input value={q} onChange={(e) => setQ(e.target.value)} onKeyDown={onKey}
        onFocus={() => (results.length || validEmail) && setOpen(true)}
        placeholder="Search people by name or email…" autoComplete="off" />
      {open && (
        <div className="ta-menu">
          {loading && <div className="ta-empty">Searching your directory…</div>}
          {!loading && results.length === 0 && (
            <div className="ta-empty">{validEmail ? <>No directory match — press <b>Enter</b> to add <b>{q.trim()}</b>.</> : 'No matching people found.'}</div>
          )}
          {!loading && results.map((u, i) => (
            <button type="button" key={u.email} className={`ta-item ${i === active ? 'active' : ''}`}
              onMouseEnter={() => setActive(i)} onClick={() => pick(u)}>
              <span className="ta-avatar">{initials(u.displayName || u.email)}</span>
              <span className="ta-text">
                <span className="ta-name">{u.displayName || u.email}</span>
                <span className="ta-sub">{u.email}{u.jobTitle ? ` · ${u.jobTitle}` : ''}</span>
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
