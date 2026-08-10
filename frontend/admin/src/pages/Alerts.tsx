import { useState } from 'react'
import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatusPill, Loading } from '../components/ui'
import { ConfigCrud } from '../components/ConfigCrud'
import type { Alert } from '../types'

const METRICS = ['approvalBacklog', 'executionFailures', 'unhealthyTargets', 'deniedToday', 'failedToday']

export function Alerts() {
  const list = useAsync<Alert[]>(() => api('/api/admin/alerts'))
  const [manage, setManage] = useState(false)
  const firing = list.data?.filter((a) => a.firing) ?? []

  return (
    <div>
      <PageHeader title="Alerts"
        subtitle="Operational alert rules evaluated against the live platform state."
        actions={<>
          <button className="btn ghost" onClick={() => list.reload()}>Re-evaluate</button>
          <button className="btn ghost" onClick={() => setManage((m) => !m)}>{manage ? 'Hide rules' : 'Manage rules'}</button>
        </>} />

      <div className="tiles">
        <div className={`stat ${firing.length ? 'deny' : 'allow'} static`}>
          <div className="stat-value">{list.loading ? '…' : firing.length}</div>
          <div className="stat-label">Firing alerts</div>
          <div className="stat-sub">{list.data?.length ?? 0} rules evaluated</div>
        </div>
      </div>

      <section className="card">
        <h2>Rule evaluation</h2>
        {list.loading ? <Loading what="alerts" /> : (
          <table className="table">
            <thead><tr><th>Rule</th><th>Condition</th><th>Current</th><th>Severity</th><th>State</th></tr></thead>
            <tbody>
              {list.data?.map((a) => (
                <tr key={a.id}>
                  <td><b>{a.name}</b></td>
                  <td className="mono">{a.metric} {a.operator} {a.threshold}</td>
                  <td>{a.currentValue}</td>
                  <td><StatusPill s={a.severity === 'Critical' ? 'DENIED' : 'DEGRADED'} /></td>
                  <td>{a.firing ? <StatusPill s="FAILED" /> : (a.enabled ? <StatusPill s="ACTIVE" /> : <span className="muted">disabled</span>)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {manage && (
        <ConfigCrud
          type="alert-rule"
          title="Alert Rules"
          subtitle="Define what to alert on. Metrics: approvalBacklog, executionFailures, unhealthyTargets, deniedToday, failedToday."
          columns={[{ key: 'name', label: 'Name' }, { key: 'metric', label: 'Metric' }, { key: 'operator', label: 'Op' }, { key: 'threshold', label: 'Threshold' }, { key: 'severity', label: 'Severity' }, { key: 'enabled', label: 'On', kind: 'bool' }]}
          fields={[
            { key: 'id', label: 'Rule id', placeholder: 'approval-backlog' },
            { key: 'name', label: 'Name' },
            { key: 'metric', label: 'Metric', type: 'select', options: METRICS },
            { key: 'operator', label: 'Operator', type: 'select', options: ['>', '>=', '<', '<='] },
            { key: 'threshold', label: 'Threshold', type: 'number' },
            { key: 'severity', label: 'Severity', type: 'select', options: ['Warning', 'Critical'] },
            { key: 'enabled', label: 'Enabled', type: 'checkbox' },
          ]}
          blank={{ id: '', name: '', metric: '', operator: '>', threshold: 0, severity: 'Warning', enabled: true }} />
      )}
    </div>
  )
}
