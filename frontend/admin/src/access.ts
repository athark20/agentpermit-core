import { useEffect, useState } from 'react'
import { api, ApiError } from './api'
import { resolveIsAdmin } from './auth'

/** Who the signed-in person is and what their AgentPermit role lets them do. */
export interface Access {
  loading: boolean
  name: string
  email: string
  /** Role keys: admin · approver · agent-owner · viewer (empty = signed in but not a member). */
  roles: string[]
  isAdmin: boolean
  /** May decide approvals (approver or admin). */
  canApprove: boolean
  /** May manage agents day-to-day (agent owner or admin). */
  canManageAgents: boolean
  /** May change rules, settings and configuration (admin only). */
  canEdit: boolean
  /** May see the workspace at all (any role). */
  canView: boolean
}

const NONE: Access = { loading: true, name: '', email: '', roles: [], isAdmin: false, canApprove: false, canManageAgents: false, canEdit: false, canView: false }

interface MeResponse {
  name?: string
  email?: string
  roles?: string[]
  can?: { approve?: boolean; manageAgents?: boolean; edit?: boolean; view?: boolean }
}

let cached: Access | undefined
let inflight: Promise<Access> | undefined

async function fetchAccess(): Promise<Access> {
  try {
    const me = await api<MeResponse>('/api/admin/me')
    const roles = me.roles ?? []
    const admin = roles.includes('admin') || !!me.can?.edit
    return {
      loading: false,
      name: me.name ?? '',
      email: me.email ?? '',
      roles,
      isAdmin: admin,
      canApprove: me.can?.approve ?? (admin || roles.includes('approver')),
      canManageAgents: me.can?.manageAgents ?? (admin || roles.includes('agent-owner')),
      canEdit: me.can?.edit ?? admin,
      canView: me.can?.view ?? roles.length > 0,
    }
  } catch (e) {
    // Older gateway without /me (e.g. mid-rollout): fall back to the token-claim admin check so the
    // console keeps behaving exactly as before the persona feature.
    if (e instanceof ApiError && e.status === 404) {
      const admin = await resolveIsAdmin()
      return { loading: false, name: '', email: '', roles: admin ? ['admin'] : [], isAdmin: admin, canApprove: admin, canManageAgents: admin, canEdit: admin, canView: admin }
    }
    // Unauthenticated / no access: signed in but not a member of this workspace.
    return { ...NONE, loading: false }
  }
}

/** Resolves the caller's persona once per session (cached); all components share the result. */
export function useAccess(): Access {
  const [a, setA] = useState<Access>(cached ?? NONE)
  useEffect(() => {
    if (cached) return
    inflight ??= fetchAccess().then((r) => { cached = r; return r })
    let alive = true
    inflight.then((r) => { if (alive) setA(r) })
    return () => { alive = false }
  }, [])
  return a
}

/** Plain-language label for the strongest role someone holds. */
export function roleLabel(a: Access): string {
  if (a.isAdmin) return 'Admin'
  if (a.roles.includes('approver')) return 'Approver'
  if (a.roles.includes('agent-owner')) return 'Agent owner'
  if (a.roles.includes('viewer')) return 'Viewer'
  return 'No access'
}
