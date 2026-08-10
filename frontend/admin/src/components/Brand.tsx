// AgentPermit mark — the [P] bracket lockup from brand kit v1.0.
// Cyan + white brackets, cyan actor dot, amber permit check.
// On light backgrounds pass dark={false} so the white bracket renders in navy.
export function BrandMark({ className, dark = true }: { className?: string; dark?: boolean }) {
  return (
    <svg className={className} viewBox="18 18 92 92" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="AgentPermit">
      <path d="M49 27H30V101H49" fill="none" stroke="#22D3EE" strokeWidth="11" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M79 27H98V101H79" fill="none" stroke={dark ? '#F8FBFF' : '#071525'} strokeWidth="11" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="64" cy="57" r="9" fill="#22D3EE" />
      <path d="M49 77L59 87L82 55" fill="none" stroke="#F5B942" strokeWidth="9" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

// Two-tone wordmark: "Agent" (light) + "Permit" (cyan).
export function Wordmark({ className }: { className?: string }) {
  return (
    <span className={`brand-name ${className ?? ''}`}><span className="w1">Agent</span><span className="w2">Permit</span></span>
  )
}
