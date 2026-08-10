# Copilot Studio manual checkpoint

The bootstrap script creates the environment, Dataverse schema, Azure resources,
runtime database, and the `Refund.Issue` action definition.

Microsoft does not provide a stable supported CLI for authoring a Copilot Studio
agent. Complete this one portal step:

1. Open Microsoft Copilot Studio.
2. Select the `AgentPermit Dev` environment.
3. Create an agent named `AgentPermit Refund Demo`.
4. Add instructions:
   - Collect customer ID, refund amount, currency, and reason.
   - Do not execute refunds directly.
   - In Week 3, call only the AgentPermit gateway tool.
5. Save the agent without publishing it.
6. Record its Copilot Studio agent ID and Entra Agent ID in the Agent table once available.

Do not add HTTP, Dataverse write, Power Automate write, or target-system connectors
to this agent. The gateway connector will be added in Week 3.
