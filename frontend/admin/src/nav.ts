export interface NavItem { id: string; label: string; adminOnly?: boolean }

// A guided journey, in sequence — reads like a story a non-technical owner can follow.
export const NAV: NavItem[] = [
  { id: 'dashboard', label: 'Dashboard' },
  { id: 'agents', label: 'Your agents' },
  { id: 'rules', label: 'Rules' },
  { id: 'limits', label: 'Limits & safety' },
  { id: 'activity', label: 'What’s happening' },
  { id: 'investigate', label: 'Investigate' },
  { id: 'approvals', label: 'Things needing you' },
  { id: 'settings', label: 'Settings' },
]

// Settings — the less-frequent setup, grouped with plain names.
export interface SettingsGroup { title: string; items: NavItem[] }
export const SETTINGS_GROUPS: SettingsGroup[] = [
  {
    title: 'Setup',
    items: [
      { id: 'plan', label: 'Your plan', adminOnly: true },
      { id: 'targets', label: 'Connected systems' },
      { id: 'dlp', label: 'App connections' },
      { id: 'users-roles', label: 'Roles & access' },
      { id: 'platform-settings', label: 'General' },
      { id: 'retention', label: 'Data retention' },
    ],
  },
  {
    title: 'Behind the scenes',
    items: [
      { id: 'risk-catalog', label: 'Connection risk levels' },
      { id: 'feeds', label: 'Where activity comes from' },
      { id: 'security', label: 'Security' },
    ],
  },
]

export const DEFAULT_PAGE = 'dashboard'

export function labelFor(id: string): string {
  const top = NAV.find((n) => n.id === id)
  if (top) return top.label
  for (const g of SETTINGS_GROUPS) {
    const it = g.items.find((i) => i.id === id)
    if (it) return it.label
  }
  return id
}
