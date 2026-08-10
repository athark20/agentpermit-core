import { msal, apiScope, apiBase, loginRequest } from './auth'

// Acquire a delegated access token for the gateway admin API (silent, with popup fallback).
async function token(): Promise<string> {
  const account = msal.getActiveAccount() ?? msal.getAllAccounts()[0]
  if (!account) throw new ApiError({ status: 401, code: 'UNAUTHENTICATED', title: 'Please sign in', detail: 'Your session has ended. Sign in again to continue.', message: 'Not signed in' })
  try {
    const r = await msal.acquireTokenSilent({ scopes: [apiScope], account })
    return r.accessToken
  } catch {
    const r = await msal.acquireTokenPopup(loginRequest)
    return r.accessToken
  }
}

export interface Problem {
  status: number
  title?: string
  detail?: string
  code?: string
  traceId?: string
  errors?: Record<string, string[]>
  message: string
}

/** A structured API error carrying the gateway's exact reason, machine code and support trace id. */
export class ApiError extends Error {
  status: number
  title?: string
  detail?: string
  code?: string
  traceId?: string
  fieldErrors?: Record<string, string[]>
  constructor(p: Problem) {
    super(p.message)
    this.name = 'ApiError'
    this.status = p.status
    this.title = p.title
    this.detail = p.detail
    this.code = p.code
    this.traceId = p.traceId
    this.fieldErrors = p.errors
  }
  /** Best plain-language reason to show the user. */
  get reason(): string {
    return this.detail || this.title || this.message || `The request failed (${this.status}).`
  }
}

/** Typed fetch that always attaches the Entra bearer token. Throws a structured ApiError on any failure. */
export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const t = await token()
  let res: Response
  try {
    res = await fetch(`${apiBase}${path}`, {
      ...init,
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${t}`,
        ...(init?.headers ?? {}),
      },
    })
  } catch {
    // Network / DNS / CORS-preflight failure — the server was never reached.
    throw new ApiError({
      status: 0, code: 'NETWORK', title: 'Cannot reach AgentPermit',
      detail: 'The console could not reach the server. Check your connection and try again.',
      message: 'Network error',
    })
  }

  const text = await res.text()
  if (!res.ok) {
    // Parse the gateway's problem+json (title / detail / code / traceId / field errors).
    let p: Partial<Problem> & { errors?: Record<string, string[]> } = {}
    try { p = text ? JSON.parse(text) : {} } catch { /* non-JSON error body */ }
    throw new ApiError({
      status: res.status,
      title: p.title ?? statusTitle(res.status),
      detail: p.detail,
      code: p.code ?? codeForStatus(res.status),
      traceId: p.traceId,
      errors: p.errors,
      message: p.detail || p.title || text || res.statusText || `HTTP ${res.status}`,
    })
  }
  return (text ? JSON.parse(text) : undefined) as T
}

function statusTitle(s: number): string {
  if (s === 401) return 'Please sign in'
  if (s === 403) return 'You do not have access'
  if (s === 402) return 'Not included in your plan'
  if (s === 404) return 'Not found'
  if (s === 409) return 'That conflicts with something'
  if (s === 422 || s === 400) return 'Please check what you entered'
  if (s >= 500) return 'Something went wrong on our side'
  return 'The request could not be completed'
}

function codeForStatus(s: number): string {
  if (s === 401) return 'UNAUTHENTICATED'
  if (s === 403) return 'FORBIDDEN'
  if (s === 402) return 'LICENCE_REQUIRED'
  if (s === 404) return 'NOT_FOUND'
  if (s === 409) return 'CONFLICT'
  if (s === 400 || s === 422) return 'VALIDATION'
  if (s >= 500) return 'UNEXPECTED'
  return 'ERROR'
}

/** Extract a plain-language reason from any thrown value. */
export function reasonOf(e: unknown): string {
  if (e instanceof ApiError) return e.reason
  if (e instanceof Error) return e.message
  return String(e)
}
