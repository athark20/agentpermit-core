import { useEffect, useState } from 'react'

/** Minimal hash router: deep-linkable, back-button friendly, zero dependencies. */
export function useHashRoute(fallback: string) {
  const read = () => location.hash.replace(/^#\/?/, '') || fallback
  const [route, setRoute] = useState(read)
  useEffect(() => {
    const on = () => setRoute(read())
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
  const navigate = (id: string) => { location.hash = `#/${id}` }
  return { route, navigate }
}
