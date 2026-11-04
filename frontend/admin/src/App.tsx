import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from '@azure/msal-react'
import { loginRequest } from './auth'
import { useHashRoute } from './router'
import { DEFAULT_PAGE } from './nav'
import { Layout } from './components/Layout'
import { Dashboard } from './pages/Dashboard'
import { YourAgents, LimitsAndSafety, WhatsHappening, ThingsNeedingYou } from './pages/journey'
import { Rules } from './pages/Rules'
import { Settings } from './pages/Settings'
import { Onboarding } from './pages/Onboarding'
import { IntegrationGuide } from './pages/IntegrationGuide'
import { Investigate } from './pages/Investigate'
import { BrandMark, Wordmark } from './components/Brand'
import { ErrorBoundary } from './components/ErrorBoundary'

export function App() {
  const { instance, accounts } = useMsal()
  const { route, navigate } = useHashRoute(DEFAULT_PAGE)
  const [top, sub] = route.split('/')

  function render() {
    switch (top) {
      case 'dashboard': return <Dashboard />
      case 'agents': return <YourAgents />
      case 'rules': return <Rules />
      case 'limits': return <LimitsAndSafety />
      case 'activity': return <WhatsHappening />
      case 'approvals': return <ThingsNeedingYou focus={sub} />
      case 'onboarding': return <Onboarding />
      case 'integration': return <IntegrationGuide sub={sub} navigate={navigate} />
      case 'investigate': return <Investigate sub={sub} navigate={navigate} />
      case 'settings': return <Settings sub={sub} navigate={navigate} />
      default: return <Dashboard />
    }
  }

  return (
    <>
      <UnauthenticatedTemplate>
        <div className="signin">
          <div className="signin-brand">
            <BrandMark className="brand-mark" dark={false} />
            <Wordmark />
          </div>
          <p className="tagline">Every agent. Explicitly permitted.</p>
          <p className="signin-sub">
            Customer-hosted enforcement that governs Microsoft Copilot agents and multi-agent flows
            before they act. Access is governed by your Microsoft Entra ID group.
          </p>
          <button className="btn primary lg" onClick={() => instance.loginRedirect(loginRequest)}>
            Sign in with Microsoft Entra ID
          </button>
        </div>
      </UnauthenticatedTemplate>

      <AuthenticatedTemplate>
        <Layout
          route={route}
          navigate={navigate}
          userName={accounts[0]?.name ?? accounts[0]?.username ?? ''}
          onSignOut={() => instance.logoutRedirect()}
        >
          <ErrorBoundary resetKey={route}>{render()}</ErrorBoundary>
        </Layout>
      </AuthenticatedTemplate>
    </>
  )
}
