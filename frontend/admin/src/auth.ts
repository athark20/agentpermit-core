import { PublicClientApplication, type Configuration } from '@azure/msal-browser'

// Everything is env-driven so one build serves dev and prod (see .env.example).
export const apiScope = import.meta.env.VITE_API_SCOPE
export const apiBase = import.meta.env.VITE_API_BASE.replace(/\/$/, '')

const config: Configuration = {
  auth: {
    clientId: import.meta.env.VITE_ENTRA_CLIENT_ID,
    authority: `https://login.microsoftonline.com/${import.meta.env.VITE_ENTRA_TENANT_ID}`,
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
  },
  cache: { cacheLocation: 'sessionStorage', storeAuthStateInCookie: false },
}

export const msal = new PublicClientApplication(config)

// The admin API scope. Group / app-role membership (enforced server-side) decides authorization.
export const loginRequest = { scopes: [apiScope] }

// Control-plane admin identity — mirrors the gateway's AdminAuth (app role, or an Entra group id).
// Authorization is ALWAYS enforced server-side; this only lets the UI hide admin-only surfaces
// (e.g. licence management) from non-admins. Defaults to the gateway's default app role.
export const adminRole = import.meta.env.VITE_ADMIN_ROLE || 'Admin.Access'
export const adminGroupId = import.meta.env.VITE_ADMIN_GROUP_ID || ''

// Decode a JWT payload (no verification — for UI hints only; the gateway verifies for real).
function decodeJwtClaims(jwt: string): Record<string, unknown> {
  try {
    const b64 = jwt.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')
    const json = decodeURIComponent(
      atob(b64).split('').map((c) => '%' + c.charCodeAt(0).toString(16).padStart(2, '0')).join(''),
    )
    return JSON.parse(json)
  } catch {
    return {}
  }
}

/**
 * True when the signed-in user is a control-plane admin. The admin app role/group is defined on the
 * GATEWAY API, so it lives in the API ACCESS token (not the SPA's ID token) — we acquire that token
 * silently and read its claims. Authorization is still enforced server-side; this only hides admin
 * surfaces from non-admins.
 */
export async function resolveIsAdmin(): Promise<boolean> {
  const account = msal.getActiveAccount() ?? msal.getAllAccounts()[0]
  if (!account) return false
  try {
    const r = await msal.acquireTokenSilent({ scopes: [apiScope], account })
    const c = decodeJwtClaims(r.accessToken)
    const roles = Array.isArray(c.roles) ? (c.roles as string[]) : []
    const groups = Array.isArray(c.groups) ? (c.groups as string[]) : []
    if (adminRole && roles.includes(adminRole)) return true
    if (adminGroupId && groups.includes(adminGroupId)) return true
    return false
  } catch {
    return false
  }
}
