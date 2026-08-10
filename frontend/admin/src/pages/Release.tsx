import { api } from '../api'
import { useAsync } from '../hooks'
import { KV } from '../components/ui'
import { ConfigCrud } from '../components/ConfigCrud'
import type { PolicyActive } from '../types'

export function Release() {
  const policy = useAsync<PolicyActive>(() => api('/api/policies/active'))
  const versions = useAsync<{ versions: string[] }>(() => api('/api/policies/versions'))

  return (
    <div>
      <section className="card">
        <h2>Current release</h2>
        <div className="cols">
          <div>
            <KV label="Active policy">{policy.data ? `v${policy.data.version}` : '…'}</KV>
            <KV label="Policy signed">{policy.data ? (policy.data.signed ? 'yes' : 'no') : '…'}</KV>
          </div>
          <div>
            <KV label="Published policy versions">{versions.data?.versions.length ?? '…'}</KV>
            <KV label="Latest published">{versions.data?.versions.slice(-1)[0] ?? '—'}</KV>
          </div>
        </div>
        <p className="muted small">Policy snapshots are immutable and versioned; gateway image releases are recorded below
          for changelog, promotion and rollback tracking.</p>
      </section>

      <ConfigCrud
        type="release"
        title="Release Management"
        subtitle="Deployment history for the gateway and policy releases — versions, notes and promotion status."
        columns={[{ key: 'version', label: 'Version' }, { key: 'component', label: 'Component' }, { key: 'environment', label: 'Env', kind: 'pill' }, { key: 'status', label: 'Status', kind: 'pill' }, { key: 'notes', label: 'Notes' }]}
        fields={[
          { key: 'id', label: 'Release id', placeholder: 'gateway-week7c' },
          { key: 'version', label: 'Version', placeholder: 'week7c' },
          { key: 'component', label: 'Component', type: 'select', options: ['Gateway', 'Policy', 'Admin UI', 'Connector'] },
          { key: 'environment', label: 'Environment', type: 'select', options: ['DEV', 'UAT', 'PROD'] },
          { key: 'status', label: 'Status', type: 'select', options: ['Deployed', 'Promoted', 'Rolled back'] },
          { key: 'notes', label: 'Notes' },
        ]}
        blank={{ id: '', version: '', component: 'Gateway', environment: 'DEV', status: 'Deployed', notes: '' }} />
    </div>
  )
}
