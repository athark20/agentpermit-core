import { ConfigCrud } from '../components/ConfigCrud'

const ROLES = ['PlatformAdmin', 'PolicyAuthor', 'PolicyReviewer', 'PolicyPublisher', 'Approver', 'OperationsAnalyst', 'Auditor', 'ReadOnlyBusinessOwner']
const ENVIRONMENTS = ['DEV', 'UAT', 'PROD']

export function UsersRoles() {
  return <ConfigCrud
    type="role-assignment"
    title="Roles & Access"
    subtitle="Decide what each of your Microsoft Entra ID groups can do in AgentPermit. Who's in a group is managed in Entra; this maps groups to roles."
    columns={[{ key: 'role', label: 'Role', kind: 'pill' }, { key: 'entraGroupName', label: 'Entra group' }, { key: 'entraGroupId', label: 'Group id' }, { key: 'description', label: 'Notes' }]}
    fields={[
      { key: 'id', label: 'Assignment id', placeholder: 'policy-authors' },
      { key: 'role', label: 'Role', type: 'select', options: ROLES },
      { key: 'entraGroupName', label: 'Entra group name', placeholder: 'AgentPermit Policy Authors' },
      { key: 'entraGroupId', label: 'Entra group object id', placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'description', label: 'Notes' },
    ]}
    blank={{ id: '', role: '', entraGroupName: '', entraGroupId: '', description: '' }} />
}

export function PolicyAssignments() {
  return <ConfigCrud
    type="policy-assignment"
    title="Assignments"
    subtitle="Choose which policy governs each agent and action. This is how a rule you author becomes active for a specific agent."
    columns={[{ key: 'agentId', label: 'Agent' }, { key: 'actionId', label: 'Action' }, { key: 'environment', label: 'Env', kind: 'pill' }, { key: 'policyCode', label: 'Policy' }, { key: 'enabled', label: 'Enabled', kind: 'bool' }]}
    fields={[
      { key: 'id', label: 'Assignment id', placeholder: 'refund-copilot-prod' },
      { key: 'agentId', label: 'Agent id', placeholder: 'copilot-refund-demo' },
      { key: 'actionId', label: 'Action id', placeholder: 'Refund.Issue' },
      { key: 'environment', label: 'Environment', type: 'select', options: ENVIRONMENTS },
      { key: 'policyCode', label: 'Policy code', placeholder: 'Refund.Issue.AmountThreshold' },
      { key: 'enabled', label: 'Enabled', type: 'checkbox' },
    ]}
    blank={{ id: '', agentId: '', actionId: '', environment: '', policyCode: '', enabled: true }} />
}

export function ApprovalMatrices() {
  return <ConfigCrud
    type="approval-matrix"
    title="Approval Matrices"
    subtitle="Who approves what, at which thresholds. Defines the approver role per action and amount band."
    columns={[{ key: 'actionId', label: 'Action' }, { key: 'minAmount', label: 'Min' }, { key: 'maxAmount', label: 'Max' }, { key: 'approverRole', label: 'Approver role' }, { key: 'enabled', label: 'Enabled', kind: 'bool' }]}
    fields={[
      { key: 'id', label: 'Rule id', placeholder: 'refund-250-2000' },
      { key: 'actionId', label: 'Action id', placeholder: 'Refund.Issue' },
      { key: 'minAmount', label: 'Min amount', type: 'number' },
      { key: 'maxAmount', label: 'Max amount', type: 'number' },
      { key: 'approverRole', label: 'Approver role', placeholder: 'RefundApprover' },
      { key: 'enabled', label: 'Enabled', type: 'checkbox' },
    ]}
    blank={{ id: '', actionId: '', minAmount: 0, maxAmount: 0, approverRole: '', enabled: true }} />
}

export function Environments() {
  return <ConfigCrud
    type="environment"
    title="Environments"
    subtitle="The DEV / UAT / PROD environments AgentPermit governs. Production is highlighted so changes there are treated with care."
    idField="id"
    columns={[{ key: 'id', label: 'Code' }, { key: 'displayName', label: 'Name' }, { key: 'isProduction', label: 'Production', kind: 'bool' }, { key: 'description', label: 'Description' }]}
    fields={[
      { key: 'id', label: 'Code', placeholder: 'UAT' },
      { key: 'displayName', label: 'Display name' },
      { key: 'description', label: 'Description' },
      { key: 'isProduction', label: 'Is production', type: 'checkbox' },
      { key: 'ordinal', label: 'Order', type: 'number' },
    ]}
    blank={{ id: '', displayName: '', description: '', isProduction: false, ordinal: 0 }} />
}

export function PlatformSettings() {
  return <ConfigCrud
    type="platform-setting"
    title="General"
    subtitle="Customer name, notifications (Microsoft Teams) and time zone."
    columns={[{ key: 'key', label: 'Setting' }, { key: 'value', label: 'Value' }]}
    fields={[
      { key: 'id', label: 'Setting id', placeholder: 'time-zone' },
      { key: 'key', label: 'Setting name', placeholder: 'Time zone' },
      { key: 'value', label: 'Value', placeholder: 'UTC' },
    ]}
    blank={{ id: '', key: '', value: '' }} />
}

export function Retention() {
  return <ConfigCrud
    type="retention-policy"
    title="Data Retention"
    subtitle="How long each type of record is kept, and whether it's under legal hold. Evidence and audit records keep a long horizon."
    columns={[{ key: 'dataClass', label: 'Data class' }, { key: 'retentionDays', label: 'Retention (days)' }, { key: 'legalHold', label: 'Legal hold', kind: 'bool' }]}
    fields={[
      { key: 'id', label: 'Policy id', placeholder: 'receipts' },
      { key: 'dataClass', label: 'Data class', placeholder: 'Receipts (evidence)' },
      { key: 'retentionDays', label: 'Retention days', type: 'number' },
      { key: 'legalHold', label: 'Legal hold', type: 'checkbox' },
    ]}
    blank={{ id: '', dataClass: '', retentionDays: 365, legalHold: false }} />
}
