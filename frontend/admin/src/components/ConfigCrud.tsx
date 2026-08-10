import { useMemo, useState, type ReactNode } from 'react'
import { api, ApiError, reasonOf } from '../api'
import { useAsync } from '../hooks'
import { useAccess } from '../access'
import { useToast } from './Toast'
import { PageHeader, StatusPill, Loading, EmptyState } from './ui'

export interface Field {
  key: string
  label: string
  type?: 'text' | 'number' | 'checkbox' | 'select'
  options?: string[]
  placeholder?: string
  help?: string
  when?: (form: Record<string, unknown>) => boolean
}
export interface Column {
  key: string
  label: string
  kind?: 'text' | 'bool' | 'pill'
}

interface Item { id: string; updatedUtc: string; value: Record<string, unknown> }

export function ConfigCrud({ type, title, subtitle, idField = 'id', columns, fields, blank }: {
  type: string
  title: string
  subtitle: string
  idField?: string
  columns: Column[]
  fields: Field[]
  blank: Record<string, unknown>
}) {
  const list = useAsync<Item[]>(() => api(`/api/admin/config/${type}`))
  const toast = useToast()
  const access = useAccess()
  const [form, setForm] = useState<Record<string, unknown>>(blank)
  const [editing, setEditing] = useState(false)
  const [msg, setMsg] = useState<{ ok: boolean; text: string }>()

  const rows = useMemo<Record<string, unknown>[]>(
    () => (list.data ?? []).map((i) => ({ ...i.value, _updatedUtc: i.updatedUtc })), [list.data])

  function edit(row: Record<string, unknown>) { setForm({ ...row }); setEditing(true); setMsg(undefined) }
  function reset() { setForm(blank); setEditing(false); setMsg(undefined) }

  async function save() {
    setMsg(undefined)
    const id = String(form[idField] ?? '').trim()
    if (!id) { setMsg({ ok: false, text: `${idField} is required` }); return }
    try {
      await api(`/api/admin/config/${type}/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(form) })
      toast.success('Saved', `'${id}' has been saved.`)
      reset(); list.reload()
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not save', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    }
  }

  async function remove(id: string) {
    if (!window.confirm(`Delete '${id}'? This can't be undone.`)) return
    try {
      await api(`/api/admin/config/${type}/${encodeURIComponent(id)}`, { method: 'DELETE' })
      toast.success('Deleted', `'${id}' has been removed.`)
      list.reload()
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(err?.title ?? 'Could not delete', reasonOf(e), { code: err?.code, traceId: err?.traceId })
    }
  }

  function cell(row: Record<string, unknown>, col: Column): ReactNode {
    const v = row[col.key]
    if (col.kind === 'bool') return v ? '✓' : '—'
    if (col.kind === 'pill') return v ? <StatusPill s={String(v)} /> : '—'
    return v == null || v === '' ? '—' : String(v)
  }

  const setField = (f: Field, raw: string | boolean) =>
    setForm({ ...form, [f.key]: f.type === 'number' ? Number(raw) : raw })

  return (
    <div>
      <PageHeader title={title} subtitle={subtitle} />
      <div className="cols">
        <section className="card">
          <h2>Configured</h2>
          {list.loading ? <Loading /> : (rows.length ? (
            <table className="table">
              <thead><tr>{columns.map((c) => <th key={c.key}>{c.label}</th>)}{access.canEdit && <th></th>}</tr></thead>
              <tbody>
                {rows.map((row, i) => (
                  <tr key={i}>
                    {columns.map((c) => <td key={c.key}>{cell(row, c)}</td>)}
                    {access.canEdit && (
                      <td className="right">
                        <button className="btn ghost" onClick={() => edit(row)}>Edit</button>
                        <button className="btn ghost danger" onClick={() => remove(String(row[idField]))}>Delete</button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          ) : <EmptyState title="Nothing configured yet" hint="Use the form to add the first entry." />)}
        </section>

        {access.canEdit && (
        <section className="card">
          <h2>{editing ? `Edit ${form[idField]}` : `Add ${title.replace(/s$/, '')}`}</h2>
          {fields.filter((f) => !f.when || f.when(form)).map((f) => (
            <label key={f.key}>
              {f.label}
              {f.type === 'checkbox' ? (
                <div><input type="checkbox" checked={Boolean(form[f.key])} onChange={(e) => setField(f, e.target.checked)} /></div>
              ) : f.type === 'select' ? (
                <select value={String(form[f.key] ?? '')} onChange={(e) => setField(f, e.target.value)}>
                  <option value="">—</option>
                  {f.options?.map((o) => <option key={o} value={o}>{o}</option>)}
                </select>
              ) : (
                <input type={f.type === 'number' ? 'number' : 'text'} value={String(form[f.key] ?? '')}
                  placeholder={f.placeholder} disabled={editing && f.key === idField}
                  onChange={(e) => setField(f, e.target.value)} />
              )}
              {f.help && <span className="muted small"> {f.help}</span>}
            </label>
          ))}
          <div className="row">
            <button className="btn primary" onClick={save}>Save</button>
            <button className="btn ghost" onClick={reset}>Clear</button>
          </div>
          {msg && <p className={msg.ok ? 'success' : 'error'}>{msg.text}</p>}
        </section>
        )}
        {!access.loading && !access.canEdit && (
          <section className="card">
            <h2>View only</h2>
            <p className="muted small" style={{ marginTop: 0 }}>Your role can see this setup but not change it. Ask an administrator to make changes.</p>
          </section>
        )}
      </div>
    </div>
  )
}
