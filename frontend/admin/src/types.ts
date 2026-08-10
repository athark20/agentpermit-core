export type Tab = 'dashboard' | 'policies' | 'targets' | 'agents' | 'evidence'

export interface PolicyActive {
  policyCode: string
  version: string
  hash: string
  signed: boolean
  signatureAlgorithm?: string
  signingKeyId?: string
  publishedAtUtc?: string
  defaultDecision: string
  ruleCount: number
  rules: { id: string; name: string; priority: number; actionId?: string; decision: string }[]
}

export interface TargetSystem {
  targetSystemCode: string
  displayName?: string
  adapterType: string
  authMode?: string
  baseUrl?: string
  executePath?: string
  timeoutSeconds?: number
  maxRetries?: number
  enabled: boolean
  allowedActions?: string[]
}

export interface Agent {
  agentId: string
  displayName: string
  enabled: boolean
  entraClientId?: string
  type?: string
  owner?: string
  environment?: string
  riskTier?: string
}

export interface Receipt {
  actionReceiptId: string
  actionRequestId: string
  receiptNumber: string
  receiptHash: string
  previousReceiptHash?: string
  signed: boolean
  createdUtc: string
}

export interface BusinessAction {
  actionId: string
  displayName: string
  domain: string
  risk: string
  targetSystem: string
}

export interface ActionRequest {
  actionRequestId: string
  correlationId: string
  executingAgentId: string
  rootAgentId?: string
  initiatingUserId?: string
  actionId: string
  status: string
  requestPayloadHash: string
  createdUtc: string
  updatedUtc: string
}

export interface Execution {
  executionId: string
  actionRequestId: string
  adapterCode: string
  targetResourceId?: string
  status: string
  attemptNumber: number
  startedUtc: string
  completedUtc?: string
  errorCode?: string
  errorMessage?: string
}

export interface Metrics {
  totalRequests: number
  byStatus: Record<string, number>
  todayTotal: number
  todayAllowed: number
  todayDenied: number
  todayApproval: number
  todayFailed: number
}

export interface ExceptionItem {
  kind: string
  occurredUtc: string
  reference: string
  detail?: string
  code?: string
  message?: string
}

export interface Integration {
  targetSystemCode: string
  displayName: string
  adapterType: string
  enabled: boolean
  status: string
  message?: string
  checkedAtUtc: string
}

export interface DelegationEdge {
  rootAgentId: string
  executingAgentId: string
  requests: number
  lastSeenUtc: string
  escalation: boolean
}

export interface SecurityPosture {
  identityBindingEnforced: boolean
  identityHeader: string
  apimOriginHeader: string
  apimOriginSecretConfigured: boolean
  receiptSigningEnabled: boolean
  adminEntraConfigured: boolean
  adminAppRole: string
  adminGroupConfigured: boolean
  allowedCorsOrigins: string[]
}

export interface LicenseStatus {
  edition: string
  maxAgents: number | null   // null = unlimited
  maxEnvironments: number
  features: string[]
  expiresUtc?: string | null
  expired: boolean
  managementLocked: boolean
  customerId?: string
}

export interface AuditEvent {
  auditEventId: string
  occurredUtc: string
  actor: string
  actorId?: string
  action: string
  targetType: string
  targetId?: string
  summary?: string
}

export interface Alert {
  id: string
  name: string
  metric: string
  operator: string
  threshold: number
  severity: string
  enabled: boolean
  currentValue: number
  firing: boolean
}

export interface DeadLetterItem {
  executionId: string
  actionRequestId: string
  adapterCode: string
  errorCode?: string
  errorMessage?: string
  startedUtc: string
  status: string
}

export interface OnboardingStatus {
  key?: string
  label?: string
  configured: boolean
  connected: boolean
  environmentUrl?: string
  environmentName?: string
  organizationId?: string
  message?: string
}

export interface TenantConnection {
  key: string
  label: string
  environmentName?: string
  environmentUrl?: string
  configured: boolean
  removable?: boolean
}

export interface DiscoveredAgent {
  botId: string
  schemaName: string
  name: string
  owner: string
  state: string
  isManaged: boolean
  createdOn?: string
  topicsCount: number
  componentsCount: number
  connectionReferences: string[]
  authMethod: string
  entraAgentId?: string
  alreadyImported: boolean
}
export interface DiscoveredRow { suggestedAgentId: string; agent: DiscoveredAgent }

export interface VerifyResult {
  receiptNumber: string
  actionRequestId: string
  storedHash: string
  recomputedHash?: string
  integrityOk?: boolean
  signed: boolean
  signatureOk?: boolean
  verified: boolean
}
