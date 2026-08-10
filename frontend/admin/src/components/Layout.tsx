import { type ReactNode } from 'react'
import { NAV, labelFor } from '../nav'
import { env, isProd } from '../config'
import { useAccess, roleLabel } from '../access'
import { BrandMark, Wordmark } from './Brand'
import { EmergencyStop } from './EmergencyStop'

export function Layout({ route, navigate, userName, onSignOut, children }: {
  route: string
  navigate: (id: string) => void
  userName: string
  onSignOut: () => void
  children: ReactNode
}) {
  const top = route.split('/')[0]
  const access = useAccess()

  return (
    <div className="app">
      <aside className="sidebar">
        <div className="brand" onClick={() => navigate('dashboard')}>
          <div className="brand-badge"><BrandMark className="brand-mark" /></div>
          <div className="brand-text">
            <Wordmark />
            <div className="brand-sub">Sovereign AI agent governance</div>
          </div>
        </div>

        <nav className="nav">
          {NAV.map((item) => (
            <button
              key={item.id}
              className={`nav-item ${top === item.id ? 'active' : ''}`}
              aria-current={top === item.id ? 'page' : undefined}
              onClick={() => navigate(item.id)}
            >
              <span className="nav-label">{item.label}</span>
            </button>
          ))}
        </nav>

        <div className="sidebar-foot">
          <div className="sysstatus"><span className="dot" /> System healthy</div>
          <div className="version">v{env.version} · {env.customer}</div>
        </div>
      </aside>

      <div className="main">
        <header className="topbar">
          <div className="crumbs">
            <span className="crumb-current">{labelFor(top)}</span>
          </div>
          <div className="top-right">
            <EmergencyStop />
            <span className={`env-badge ${isProd ? 'prod' : 'nonprod'}`}>{env.environment}{isProd ? ' · live' : ''}</span>
            {!access.loading && (
              <span className={`role-badge ${access.canEdit ? '' : 'limited'}`} title="Your AgentPermit role">
                {roleLabel(access)}{access.canView && !access.canEdit && !access.canApprove && !access.canManageAgents ? ' · read-only' : ''}
              </span>
            )}
            <span className="user-name">{userName}</span>
            <button className="btn ghost small" onClick={onSignOut}>Sign out</button>
          </div>
        </header>
        <main className="content">{children}</main>
      </div>
    </div>
  )
}
