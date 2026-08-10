import type { ReactNode } from 'react'
import { SETTINGS_GROUPS } from '../nav'
import { useIsAdmin } from '../hooks'
import { EmptyState, Loading } from '../components/ui'
import { Targets } from './Targets'
import { SecurityConfig } from './SecurityConfig'
import { RolesAccess } from './RolesAccess'
import { PlatformSettings, Retention } from './config-pages'
import { Feeds, RiskCatalog, DlpGenerator } from './governance-pages'
import { Plan } from './Plan'

// Less-frequent setup — plain names.
const PAGES: Record<string, ReactNode> = {
  'plan': <Plan />,
  'targets': <Targets />,
  'dlp': <DlpGenerator />,
  'users-roles': <RolesAccess />,
  'platform-settings': <PlatformSettings />,
  'retention': <Retention />,
  'risk-catalog': <RiskCatalog />,
  'feeds': <Feeds />,
  'security': <SecurityConfig />,
}

const DEFAULT_SUB = 'targets'

// Pages that require the control-plane admin role. Enforced server-side too — this hides them.
const ADMIN_ONLY = new Set(['plan'])

export function Settings({ sub, navigate }: { sub?: string; navigate: (id: string) => void }) {
  const adminState = useIsAdmin()          // undefined while resolving, then boolean
  const admin = adminState === true
  const requested = sub && PAGES[sub] ? sub : DEFAULT_SUB
  const active = ADMIN_ONLY.has(requested) && !admin
    ? (adminState === undefined ? '__loading' : '__forbidden')
    : requested
  return (
    <div className="settings-shell">
      <aside className="settings-nav">
        {SETTINGS_GROUPS.map((g) => (
          <div className="settings-group" key={g.title}>
            {g.items.filter((it) => admin || !it.adminOnly).map((it) => (
              <button
                key={it.id}
                className={`settings-item ${active === it.id ? 'active' : ''}`}
                onClick={() => navigate(`settings/${it.id}`)}
              >
                {it.label}
              </button>
            ))}
          </div>
        ))}
      </aside>
      <div className="settings-content">
        {active === '__loading'
          ? <Loading what="access" />
          : active === '__forbidden'
            ? <EmptyState title="Admins only" hint="Managing your plan needs administrator access. Ask a workspace administrator, or sign in with an admin account." />
            : PAGES[active]}
      </div>
    </div>
  )
}
