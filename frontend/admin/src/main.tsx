import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'
import { EventType, type AuthenticationResult } from '@azure/msal-browser'
import { msal } from './auth'
import { App } from './App'
import { ToastProvider, pushToast } from './components/Toast'
import { ErrorBoundary } from './components/ErrorBoundary'
import './styles.css'

// Catch-all for errors that escape React (uncaught promise rejections, async throws) so the user
// still gets a plain notification instead of a silent failure. Benign auth/cancel noise is filtered.
const BENIGN = /interaction_in_progress|user_cancelled|popup_window_error|BrowserAuthError|AbortError|Not signed in|ResizeObserver/i
window.addEventListener('unhandledrejection', (e) => {
  const msg = String((e.reason && (e.reason.message ?? e.reason)) ?? '')
  if (!msg || BENIGN.test(msg)) return
  pushToast({ kind: 'error', title: 'Unexpected error', detail: msg })
})
window.addEventListener('error', (e) => {
  if (!(e.error instanceof Error) || BENIGN.test(e.message)) return
  pushToast({ kind: 'error', title: 'Unexpected error', detail: e.message })
})

// MSAL v4 requires explicit initialization before the provider renders.
async function bootstrap() {
  await msal.initialize()

  // Keep an active account set after login / on reload.
  const accounts = msal.getAllAccounts()
  if (accounts.length > 0) msal.setActiveAccount(accounts[0])
  msal.addEventCallback((e) => {
    if (e.eventType === EventType.LOGIN_SUCCESS && e.payload) {
      msal.setActiveAccount((e.payload as AuthenticationResult).account)
    }
  })

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <MsalProvider instance={msal}>
        <ToastProvider>
          <ErrorBoundary>
            <App />
          </ErrorBoundary>
        </ToastProvider>
      </MsalProvider>
    </StrictMode>,
  )
}

void bootstrap()
