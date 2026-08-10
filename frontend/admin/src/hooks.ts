import { useCallback, useEffect, useState } from 'react'
import { resolveIsAdmin } from './auth'
import { ApiError, reasonOf } from './api'
import { useToast } from './components/Toast'

/**
 * Wraps a mutating action: tracks busy, shows a success toast on completion and an error toast
 * carrying the gateway's exact reason + support reference on failure. Returns undefined on error.
 */
export function useAction() {
  const toast = useToast()
  const [busy, setBusy] = useState(false)
  const run = useCallback(async <T,>(fn: () => Promise<T>, opts?: { success?: string; errorTitle?: string }): Promise<T | undefined> => {
    setBusy(true)
    try {
      const r = await fn()
      if (opts?.success) toast.success(opts.success)
      return r
    } catch (e) {
      const err = e instanceof ApiError ? e : undefined
      toast.error(opts?.errorTitle ?? err?.title ?? 'That didn’t work', reasonOf(e), { code: err?.code, traceId: err?.traceId })
      return undefined
    } finally {
      setBusy(false)
    }
  }, [toast])
  return { run, busy }
}

// Resolves control-plane admin status once (from the API access token) and caches it for the session.
// Returns undefined while resolving, then a boolean.
let cachedAdmin: boolean | undefined
export function useIsAdmin(): boolean | undefined {
  const [v, setV] = useState<boolean | undefined>(cachedAdmin)
  useEffect(() => {
    if (cachedAdmin !== undefined) return
    let alive = true
    resolveIsAdmin().then((a) => { cachedAdmin = a; if (alive) setV(a) })
    return () => { alive = false }
  }, [])
  return v
}

/** Loads async data with loading/error state and a manual reload(). */
export function useAsync<T>(loader: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T>()
  const [error, setError] = useState<string>()
  const [loading, setLoading] = useState(true)

  const run = useCallback(async () => {
    setLoading(true)
    setError(undefined)
    try {
      setData(await loader())
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps)

  useEffect(() => {
    run()
  }, [run])

  return { data, error, loading, reload: run }
}
