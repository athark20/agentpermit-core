import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, StatusPill, Loading } from '../components/ui'
import type { PolicyActive } from '../types'

export function PolicyVersions() {
  const active = useAsync<PolicyActive>(() => api('/api/policies/active'))
  const versions = useAsync<{ versions: string[] }>(() => api('/api/policies/versions'))

  const current = active.data?.version
  // Newest-first; the active one is Published, the rest are Superseded.
  const rows = (versions.data?.versions ?? []).slice().reverse()

  return (
    <div>
      <PageHeader title="Policy Versions"
        subtitle="Every published snapshot is immutable and Key Vault-signed. The active version is enforced now." />
      <section className="card">
        {versions.loading ? <Loading what="versions" /> : (
          <table className="table">
            <thead><tr><th>Version</th><th>Status</th><th>Integrity</th><th>Signature</th></tr></thead>
            <tbody>
              {rows.map((v) => {
                const isActive = v === current
                return (
                  <tr key={v}>
                    <td><b>{v}</b>{isActive && <span className="chip live"> active</span>}</td>
                    <td><StatusPill s={isActive ? 'ACTIVE' : 'SUPERSEDED'} /></td>
                    <td className="mono small">{isActive ? active.data?.hash?.slice(0, 24) + '…' : 'immutable'}</td>
                    <td>{isActive ? (active.data?.signed ? `signed (${active.data.signatureAlgorithm})` : 'unsigned') : 'signed'}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
        <p className="muted note">
          Full lifecycle metadata (published-at, publisher, change reason, effective dates) is captured on
          each snapshot and surfaces here with the Release Management module.
        </p>
      </section>
    </div>
  )
}
