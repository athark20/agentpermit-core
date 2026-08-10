import { Component, type ReactNode } from 'react'

/** Catches render-time crashes so one broken screen never white-screens the whole console. */
export class ErrorBoundary extends Component<{ children: ReactNode; resetKey?: string }, { error?: Error }> {
  state: { error?: Error } = {}

  static getDerivedStateFromError(error: Error) { return { error } }

  componentDidCatch(error: Error, info: unknown) {
    // Full detail to the console for support; the UI shows a safe, plain message.
    console.error('AgentPermit UI error:', error, info)
  }

  componentDidUpdate(prev: { resetKey?: string }) {
    // Navigating to a different screen clears a previous crash.
    if (this.state.error && prev.resetKey !== this.props.resetKey) this.setState({ error: undefined })
  }

  render() {
    if (!this.state.error) return this.props.children
    return (
      <div className="boundary">
        <div className="boundary-card">
          <div className="boundary-icon">
            <svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
              <path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z" /><path d="M12 9v4M12 17h.01" />
            </svg>
          </div>
          <h2>This screen ran into a problem</h2>
          <p className="muted">The rest of AgentPermit is unaffected and your data is safe. Try again, or reload the console.</p>
          <p className="boundary-msg mono">{this.state.error.message || 'Unexpected error'}</p>
          <div className="boundary-actions">
            <button className="btn primary" onClick={() => this.setState({ error: undefined })}>Try again</button>
            <button className="btn" onClick={() => window.location.reload()}>Reload console</button>
          </div>
        </div>
      </div>
    )
  }
}
