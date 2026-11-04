# Teams approvals

When a rule sends an agent's action for approval, AgentPermit tells a Power Automate flow, and the flow posts
an Adaptive Card in Microsoft Teams. The card shows every field of the request. Its button opens the request
in the AgentPermit console, where the approver signs in with their own Microsoft Entra ID account and approves
or declines.

The decision therefore carries the approver's own sign-in. AgentPermit validates their token, checks that they
hold the Approver role (and belong to the approver group the rule names, and are neither the person who asked
nor the agent's owner), and records the identity from the token. A name sent in the request body is ignored.

## Why the card does not approve by itself

A flow that calls AgentPermit back to approve holds the shared admin key and reports who clicked in Teams.
AgentPermit cannot check that report, so anyone holding the key could approve as anyone. By default the shared
key can **read** approvals but cannot **approve or reject** them: it gets `403 Sign in to decide`.

## Settings

| Setting | Default | What it does |
|---|---|---|
| `Teams:WebhookUrl` | empty | The flow's "When a HTTP request is received" URL. Empty = no notification. |
| `Teams:ConsoleBaseUrl` | empty | The console's address, e.g. `https://agentpermit.contoso.com`. Each notification's `reviewUrl` is `{ConsoleBaseUrl}/#/approvals/{approvalRequestId}`. |
| `Teams:ApprovalsBaseUrl` | | API Management base URL, used for `approveUrl` / `rejectUrl`. |
| `Teams:AllowWorkflowDecisions` | `false` | Legacy. Lets the shared key approve or reject. See below. |

## What the flow receives

| Field | Meaning |
|---|---|
| `approvalRequestId`, `actionRequestId` | Ids of the approval and the request |
| `actionId`, `agentId` | What the agent wants to do, and which agent |
| `amount`, `currency`, `customerId`, `reason` | Common fields, when the request has them |
| `parameters` | The complete request payload |
| `facts` | Every payload field as `{ "title", "value" }` pairs, ready for an Adaptive Card `FactSet` |
| `initiatingUserId`, `approverRole` | Who the agent acted for, and which approver group must decide |
| `payloadHash` | Fingerprint of the exact payload being approved |
| `requestedAtUtc`, `expiresAtUtc` | When it was asked, and when it expires |
| `decisionMode` | `console` (default) or `workflow` (legacy) |
| `reviewUrl` | Console link for this approval, or `null` if `Teams:ConsoleBaseUrl` is not set |
| `approveUrl`, `rejectUrl` | API endpoints. In `console` mode they need the approver's own token. |

## Building the flow

1. **Trigger:** *When a HTTP request is received*. Generate the schema from a sample notification.
2. **Action:** *Post card in a chat or channel* (Microsoft Teams connector, posted as the Flow bot) to the
   approvers' channel or group chat, with the card below. Power Automate fills in the `@{...}` expressions.

There is no *wait for a response* step and no HTTP call back to AgentPermit.

```json
{
  "type": "AdaptiveCard",
  "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
  "version": "1.4",
  "body": [
    { "type": "TextBlock", "size": "Medium", "weight": "Bolder", "wrap": true,
      "text": "Approval needed: @{triggerBody()?['actionId']}" },
    { "type": "TextBlock", "isSubtle": true, "wrap": true,
      "text": "Agent @{triggerBody()?['agentId']} is waiting. Asked for by @{coalesce(triggerBody()?['initiatingUserId'], 'not recorded')}." },
    { "type": "FactSet", "facts": @{triggerBody()?['facts']} },
    { "type": "TextBlock", "isSubtle": true, "size": "Small", "wrap": true,
      "text": "Expires @{triggerBody()?['expiresAtUtc']}" }
  ],
  "actions": [
    { "type": "Action.OpenUrl", "title": "Review and decide", "url": "@{triggerBody()?['reviewUrl']}" }
  ]
}
```

The card is a notification. Its values come from the agent's request, so the decision is made in the console,
which shows the exact values and ties the approval to `payloadHash`: if any detail changes before the action
runs, the approval no longer counts.

## Before you switch

1. Set `Teams:ConsoleBaseUrl`.
2. Make sure approvers can sign in to the console: they need access to its Entra ID app registration and the
   Approver role in **Roles & access** (plus membership of any approver group a rule names).
3. Replace the flow's *Post adaptive card and wait for a response* step and its HTTP call back with the
   *Post card in a chat or channel* step above.
4. Leave `Teams:AllowWorkflowDecisions` at `false`.

## Legacy workflow mode

`Teams:AllowWorkflowDecisions=true` keeps an existing flow working while it is being changed: the flow posts
its own approve/decline card, waits for the response and calls `approveUrl` / `rejectUrl` with the shared key,
passing the responder as `approverId`. Every decision made this way is marked: the response message and the
audit entry say the approver was reported by the workflow and not verified, and a warning is logged. Turn it
off once the flow uses the review link.

## Not built

Approving inside Teams itself with a verified identity would need a Teams bot using Universal Actions
(`Action.Execute`), where Teams passes the signed-in user to the bot. That is not built.
