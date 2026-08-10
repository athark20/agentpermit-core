import { api } from '../api'
import { useAsync } from '../hooks'
import { PageHeader, KV, StatusPill, Loading } from '../components/ui'
import type { SecurityPosture } from '../types'

export function SecurityConfig() {
  const s = useAsync<SecurityPosture>(() => api('/api/admin/security'))
  const yn = (b: boolean) => <StatusPill s={b ? 'ACTIVE' : 'PENDING'} />

  return (
    <div>
      <PageHeader title="Security Configuration"
        subtitle="Current identity-binding, signing and access posture of the control plane." />
      {s.loading ? <Loading /> : s.error ? <p className="error">{s.error}</p> : s.data && (
        <div className="cols">
          <section className="card">
            <h2>Identity binding &amp; origin</h2>
            <KV label="Identity binding enforced">{yn(s.data.identityBindingEnforced)}</KV>
            <KV label="Identity header" mono>{s.data.identityHeader}</KV>
            <KV label="APIM origin header" mono>{s.data.apimOriginHeader}</KV>
            <KV label="APIM origin secret set">{yn(s.data.apimOriginSecretConfigured)}</KV>
            <p className="muted small">When enforced, requests must arrive via APIM with a verified origin secret and an
              Entra identity bound to a registered agent — a spoofed body agentId is rejected.</p>
          </section>
          <section className="card">
            <h2>Evidence &amp; admin access</h2>
            <KV label="Receipt signing enabled">{yn(s.data.receiptSigningEnabled)}</KV>
            <KV label="Admin Entra auth configured">{yn(s.data.adminEntraConfigured)}</KV>
            <KV label="Admin app role">{s.data.adminAppRole}</KV>
            <KV label="Admin group configured">{yn(s.data.adminGroupConfigured)}</KV>
            <KV label="Allowed origins">{s.data.allowedCorsOrigins.length || '—'}</KV>
            {s.data.allowedCorsOrigins.map((o) => <div key={o} className="muted mono small">{o}</div>)}
          </section>
        </div>
      )}
      <p className="muted note">Signing-key rotation and network-rule editing are managed here as the module expands.</p>
    </div>
  )
}
