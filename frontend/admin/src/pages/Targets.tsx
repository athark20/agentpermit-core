import { useState } from 'react'
import { api, ApiError } from '../api'
import { useAsync } from '../hooks'
import { PageHeader } from '../components/ui'
import type { TargetSystem } from '../types'

const BLANK: TargetSystem = {
  targetSystemCode: '', displayName: '', adapterType: 'REST', authMode: 'None',
  baseUrl: '', executePath: '', timeoutSeconds: 20, maxRetries: 3, enabled: true, allowedActions: [],
}

export function Targets() {
  const list = useAsync<TargetSystem[]>(() => api('/api/admin/targets'))
  const [form, setForm] = useState<TargetSystem>(BLANK)
  const [msg, setMsg] = useState<{ ok: boolean; text: string }>()

  function edit(t: TargetSystem) { setForm({ ...t, allowedActions: t.allowedActions ?? [] }); setMsg(undefined) }

  async function save() {
    setMsg(undefined)
    try {
      await api(`/api/admin/targets/${encodeURIComponent(form.targetSystemCode)}`, {
        method: 'PUT', body: JSON.stringify(form),
      })
      setMsg({ ok: true, text: `Saved ${form.targetSystemCode}. Callable now — no code deploy.` })
      setForm(BLANK); list.reload()
    } catch (e) {
      setMsg({ ok: false, text: e instanceof ApiError ? `${e.status}: ${e.message}` : String(e) })
    }
  }

  async function remove(code: string) {
    try { await api(`/api/admin/targets/${encodeURIComponent(code)}`, { method: 'DELETE' }); list.reload() }
    catch (e) { setMsg({ ok: false, text: String(e) }) }
  }

  const set = (k: keyof TargetSystem) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm({ ...form, [k]: e.target.type === 'number' ? Number(e.target.value) : e.target.value })

  return (
    <div>
      <PageHeader title="Connected Systems"
        subtitle="The systems your agents can act on (SAP, ServiceNow, a REST API…). Add one here and it's available — no code deploy." />
      <div className="cols">
      <section className="card">
        <h2>Registered targets</h2>
        {list.error && <p className="error">{list.error}</p>}
        <table className="table">
          <thead><tr><th>Code</th><th>Adapter</th><th>Enabled</th><th></th></tr></thead>
          <tbody>
            {list.data?.map((t) => (
              <tr key={t.targetSystemCode}>
                <td><b>{t.targetSystemCode}</b><div className="muted">{t.displayName}</div></td>
                <td><span className="chip">{t.adapterType}</span></td>
                <td>{t.enabled ? '✓' : '—'}</td>
                <td className="right">
                  <button className="btn ghost" onClick={() => edit(t)}>Edit</button>
                  <button className="btn ghost danger" onClick={() => remove(t.targetSystemCode)}>Delete</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <section className="card">
        <h2>{form.targetSystemCode ? `Edit ${form.targetSystemCode}` : 'Add target'}</h2>
        <label>Code<input value={form.targetSystemCode} onChange={set('targetSystemCode')} placeholder="SERVICENOW_TICKETS" /></label>
        <label>Display name<input value={form.displayName ?? ''} onChange={set('displayName')} /></label>
        <label>Adapter type
          <select value={form.adapterType} onChange={(e) => setForm({ ...form, adapterType: e.target.value })}>
            <option>REST</option><option>DELEGATED</option><option>REFUND_SIMULATOR</option>
          </select>
        </label>
        <label>Base URL<input value={form.baseUrl ?? ''} onChange={set('baseUrl')} placeholder="https://… (REST endpoint or Power Automate flow URL)" /></label>
        <label>Execute path<input value={form.executePath ?? ''} onChange={set('executePath')} placeholder="/api/v1/actions (REST only)" /></label>
        <div className="row">
          <label>Timeout (s)<input type="number" value={form.timeoutSeconds ?? 20} onChange={set('timeoutSeconds')} /></label>
          <label>Max retries<input type="number" value={form.maxRetries ?? 3} onChange={set('maxRetries')} /></label>
        </div>
        <label>Allowed actions (comma-separated)
          <input value={(form.allowedActions ?? []).join(',')}
            onChange={(e) => setForm({ ...form, allowedActions: e.target.value.split(',').map((s) => s.trim()).filter(Boolean) })} />
        </label>
        <label className="check"><input type="checkbox" checked={form.enabled}
          onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> Enabled</label>
        <div className="row">
          <button className="btn primary" disabled={!form.targetSystemCode || !form.adapterType} onClick={save}>Save</button>
          <button className="btn ghost" onClick={() => setForm(BLANK)}>Clear</button>
        </div>
        {msg && <p className={msg.ok ? 'success' : 'error'}>{msg.text}</p>}
      </section>
      </div>
    </div>
  )
}
