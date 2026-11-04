using System.Text.Json;
using AgentPermit.Gateway.Common;
using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.ControlPlane;
using AgentPermit.Gateway.Domain;
using AgentPermit.Gateway.Observations;
using AgentPermit.Gateway.Persistence;
using AgentPermit.Gateway.Policies;
using AgentPermit.Gateway.Registry;

namespace AgentPermit.Gateway.Services;

public sealed class ApprovalOptions
{
    public int ExpiryHours { get; set; } = 24;

    /// <summary>
    /// When true, an approval whose rule names an approver role (e.g. "FinanceController") can only be decided
    /// by a member of an approver group with that name — and if no such group is defined, nobody can decide it
    /// (fail closed). When false (default, for backwards compatibility), a missing group falls back to the
    /// generic Approver persona with a warning in the log. Set to true in production.
    /// </summary>
    public bool RequireNamedApproverGroups { get; set; } = false;
}

/// <summary>
/// The gateway pipeline for POST /api/actions/request:
/// validate agent → validate action → validate parameters → evaluate policy →
/// persist (audit) → ALLOW: execute + receipt / REQUIRE_APPROVAL: open approval / DENY: stop.
/// Idempotency and full auditing are guaranteed for every request.
/// </summary>
public sealed class ActionRequestService(
    IAgentRegistry agents,
    IActionRegistry actions,
    IPolicyEngine policy,
    PolicyAggregateProvider aggregateProvider,
    EnforcementGate enforcement,
    SafetyGate safety,
    ITargetSystemRegistry targets,
    IObservationStore observations,
    FeedStatusTracker feedStatus,
    IAgentPermitStore store,
    ExecutionService execution,
    ReceiptService receipts,
    ReceiptOptions receiptOptions,
    ITeamsNotifier teams,
    SecurityOptions security,
    ApprovalOptions approvalOptions,
    IAggregateLock limitLocks,
    ILogger<ActionRequestService> logger)
{
    /// <summary>
    /// Mint a signed DECISION receipt for a DENY / REQUIRE_APPROVAL verdict and attach it to the response, so
    /// what the agent TRIED is in the same tamper-evident chain as what it did. Best-effort: a signing or
    /// store failure is logged and the (already persisted, already decided) response still goes back.
    /// </summary>
    private async Task AttachDecisionReceiptAsync(
        ActionResponseDto response, ActionRequestRecord request, string decision, string policyVersion,
        string? reasonCode, CancellationToken ct)
    {
        if (!receiptOptions.SignDecisions) return;
        try
        {
            var receipt = await receipts.CreateAsync(new ReceiptInput
            {
                Request = request,
                Decision = decision,
                PolicyVersion = policyVersion,
                ExecutionStatus = ExecutionStatus.NotExecuted,
                Kind = ReceiptKind.Decision,
                ReasonCode = reasonCode
            }, ct);
            response.Receipt = ReceiptService.ToDto(receipt, request.RequestPayloadHash);
            response.ReceiptNumber = receipt.ReceiptNumber;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not mint a decision receipt for request {RequestId} (non-fatal).", request.ActionRequestId);
        }
    }
    public async Task<ActionResponseDto> ProcessAsync(ActionRequestDto dto, CallerContext caller, CancellationToken ct)
    {
        // A trusted connector may identify the CALLING agent by its own Entra Agent ID (its "badge").
        // The maker can supply that badge in whichever field their tooling actually exposes: the
        // X-Agent-Identity header, the agentIdentity body field, OR — because Copilot Studio only shows
        // body fields and caches connector schemas — directly in the agentId field. We resolve the badge
        // from the first of those that names a registered agent. Only a trusted connector may assert it.
        string? badge = null;
        if (caller.FromTrustedConnector)
        {
            badge = !string.IsNullOrWhiteSpace(caller.AssertedAgentIdentity) ? caller.AssertedAgentIdentity!.Trim()
                  : !string.IsNullOrWhiteSpace(dto.AgentIdentity) ? dto.AgentIdentity!.Trim()
                  : null;
            // Fall back to the agentId field when it carries the Entra Agent ID rather than the agent name.
            // A GUID-shaped agentId is unambiguously an Entra Agent ID (agent names are slugs, never GUIDs),
            // so treat it as the badge even if unregistered — that yields a clear AGENT_NOT_REGISTERED below.
            if (string.IsNullOrEmpty(badge) && !string.IsNullOrWhiteSpace(dto.AgentId)
                && Guid.TryParse(dto.AgentId.Trim(), out _))
                badge = dto.AgentId.Trim();
        }
        var identityAsserted = !string.IsNullOrEmpty(badge);

        // --- Envelope validation (malformed => 400 via exception) ---
        if (string.IsNullOrWhiteSpace(dto.ActionId))
            throw new GatewayValidationException("actionId is required.");
        // agentId is optional when a trusted connector already tells us which agent this is (badge).
        if (string.IsNullOrWhiteSpace(dto.AgentId) && !identityAsserted)
            throw new GatewayValidationException("agentId is required.");
        // Idempotency is CALLER-owned: the caller declares which calls are the same logical action. The
        // gateway must never invent it (a derived/content key would treat two legitimate identical actions
        // as a duplicate and silently skip the second — unacceptable for a refund/payment).
        if (string.IsNullOrWhiteSpace(dto.IdempotencyKey) || dto.IdempotencyKey.Trim().Length < 8)
            throw new GatewayValidationException("idempotencyKey is required and must be at least 8 characters.");
        dto.IdempotencyKey = dto.IdempotencyKey.Trim();
        if (dto.Parameters.ValueKind != JsonValueKind.Object)
            throw new GatewayValidationException("parameters must be a JSON object.");

        var correlationId = ResolveCorrelationId(dto.CorrelationId);

        // Freeze the payload and its hash up front (needed for the idempotency comparison).
        var payloadJson = dto.Parameters.GetRawText();
        var payloadHash = CanonicalJson.Hash(dto.Parameters);

        // --- Resolve + authorize the agent against the AUTHENTICATED identity (never body alone) ---
        // Identity comes BEFORE the idempotency lookup: a key is private to the agent that owns it, so we
        // must know who is asking before we can tell whether they have asked before. (Looking the key up
        // first would let any caller replay another agent's result, or squat its key, with no identity.)
        // A token the gateway itself validated is the strongest identity available.
        //  - A token that was PRESENTED and rejected always ends the request here: falling back to the header
        //    (or worse, the body) on a bad signature would make validation optional in practice.
        //  - A token that was never presented ends it too once Required is on; until then the APIM header
        //    path still works, so a fleet can be migrated agent by agent.
        if (security.AgentAuth.IsConfigured && !caller.TokenVerified
            && (caller.TokenPresented || security.AgentAuth.Required))
            return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                ReasonCode.TokenInvalid,
                $"A valid agent access token is required. {caller.TokenError}".TrimEnd(), ct);

        if (security.EnforceIdentityBinding)
        {
            if (!caller.OriginVerified)
                return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                    ReasonCode.NotFromApim, "Request did not originate from API Management.", ct);
            if (string.IsNullOrEmpty(caller.AuthenticatedClientId))
                return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                    ReasonCode.IdentityRequired, "An authenticated agent identity is required.", ct);
        }

        AgentInfo? agent;
        if (identityAsserted)
        {
            // Carried-badge path (multi-agent fleets): the request arrived through a TRUSTED AgentPermit
            // connector that carries the CALLING agent's own Entra Agent ID. The connector proves origin;
            // the carried Agent ID identifies which specific agent it is.
            agent = agents.FindByEntraClientId(badge!);
            if (agent is null)
                return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                    ReasonCode.AgentNotRegistered,
                    $"Carried agent identity '{badge}' is not bound to a registered agent.", ct);
        }
        else if (!string.IsNullOrEmpty(caller.AuthenticatedClientId))
        {
            // Authoritative path: the agent is whoever the authenticated identity is bound to.
            agent = agents.FindByEntraClientId(caller.AuthenticatedClientId);
            if (agent is null)
                return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                    ReasonCode.AgentNotRegistered,
                    $"Authenticated identity '{caller.AuthenticatedClientId}' is not bound to a registered agent.", ct);
        }
        else if (!security.AgentAuth.IsConfigured)
        {
            // No identity asserted and binding not enforced (local/dev): fall back to the body agentId.
            // Never reached once agent-token validation is configured — a deployment that validates tokens
            // must not accept a self-declared agentId, whatever else is or isn't set.
            agent = agents.Find(dto.AgentId);
            if (agent is null)
                return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                    ReasonCode.AgentNotRegistered, $"Agent '{dto.AgentId}' is not registered.", ct);
        }
        else
        {
            return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                ReasonCode.IdentityRequired,
                "An authenticated agent identity is required; a body-supplied agentId is not accepted.", ct);
        }

        // Whenever an identity was asserted (connector-carried badge or token), a body agentId that is
        // supplied must match the resolved agent — a body value that disagrees is a spoof attempt.
        // (agentId may be omitted entirely on the badge path; and when the maker put the badge itself in
        // the agentId field, agentId == badge is legitimate. Only a value that is neither is rejected.)
        if ((identityAsserted || !string.IsNullOrEmpty(caller.AuthenticatedClientId))
            && !string.IsNullOrWhiteSpace(dto.AgentId)
            && !string.Equals(dto.AgentId, agent.AgentId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(dto.AgentId, badge, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Agent-id spoof rejected: body '{Body}' != resolved '{Bound}'.", dto.AgentId, agent.AgentId);
            // The TOKEN identity is resolved, so this counts against that agent's deny velocity — but the key
            // is not filed under it (an inconsistent identity assertion does not get to own a key).
            await safety.RecordDenialAsync(agent.AgentId, dto.ActionId!, ReasonCode.AgentIdentityMismatch, correlationId, ct);
            return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                ReasonCode.AgentIdentityMismatch,
                $"Body agentId '{dto.AgentId}' does not match the resolved agent identity.", ct);
        }

        var executingAgentId = agent.AgentId; // authoritative

        // The human the agent acts for is DERIVED from a delegated token when one is present — never trusted
        // from the body, because separation of duties (who may approve this) rests on it. A body value that
        // disagrees with the token is a spoof attempt, exactly like a mismatched agentId.
        if (caller.AuthenticatedUserId is { } tokenUser)
        {
            if (!string.IsNullOrWhiteSpace(dto.InitiatingUserId)
                && !string.Equals(dto.InitiatingUserId.Trim(), tokenUser, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Initiating-user spoof rejected: body '{Body}' != token '{Token}'.",
                    dto.InitiatingUserId, tokenUser);
                // DenyAsync records the denial against the resolved agent; don't double-count it here.
                return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                    ReasonCode.InitiatingUserMismatch,
                    $"Body initiatingUserId '{dto.InitiatingUserId}' does not match the authenticated user.", ct, executingAgentId);
            }
            // Downstream (audit record, policy context, aggregates, approval checks) reads the verified value.
            dto.InitiatingUserId = tokenUser;
        }

        // --- Idempotency (scoped to the resolved agent): same key + same payload replays; same key +
        //     CHANGED payload is rejected. Another agent's use of the same key is invisible here. ---
        var existing = await store.FindByIdempotencyKeyAsync(executingAgentId, dto.IdempotencyKey, ct);
        if (existing is not null)
        {
            if (string.Equals(existing.RequestPayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Replay for agent {Agent} idempotency key {Key} -> request {RequestId}.",
                    executingAgentId, dto.IdempotencyKey, existing.ActionRequestId);
                return await BuildReplayResponseAsync(existing, ct);
            }

            logger.LogWarning("Agent {Agent} reused idempotency key {Key} with a DIFFERENT payload; rejected.",
                executingAgentId, dto.IdempotencyKey);
            await safety.RecordDenialAsync(executingAgentId, dto.ActionId!, ReasonCode.IdempotencyPayloadMismatch, correlationId, ct);
            return BuildResponse(existing, existing.CorrelationId, Decision.Deny, existing.Status,
                "Gateway.Idempotency", "1.0.0", ReasonCode.IdempotencyPayloadMismatch,
                "This idempotency key was already used with a different payload.", existing.RequestPayloadHash);
        }

        // --- Always-on safety gate (kill switch, quarantine, velocity + deny-velocity breakers) ---
        // Checked as soon as the agent is known and before anything else is evaluated, never subject to
        // warm-up: a frozen system, a quarantined agent or a runaway is a hard deny regardless of what the
        // request is or what policy would allow. (A quarantined agent gets AGENT_QUARANTINED for every
        // request — including malformed ones — rather than a stream of other denials.)
        var safetyResult = await safety.CheckAsync(executingAgentId, ct);
        if (!safetyResult.Allowed)
            return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                safetyResult.ReasonCode!, safetyResult.Reason!, ct, executingAgentId);

        // --- Validate action (unknown => audited DENY) ---
        var action = actions.Find(dto.ActionId);
        if (action is null)
        {
            return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                ReasonCode.UnknownAction, $"Action '{dto.ActionId}' is not registered.", ct, executingAgentId);
        }

        // --- Validate parameters against the action schema (invalid => audited DENY) ---
        var paramErrors = actions.ValidateParameters(action, dto.Parameters);
        if (paramErrors.Count > 0)
        {
            return await DenyAsync(dto, correlationId, payloadJson, payloadHash,
                ReasonCode.InvalidParameters, string.Join("; ", paramErrors), ct, executingAgentId);
        }

        // --- Reserve against windowed limits ---
        // Hold the lock of every limit this request is checked against or counts towards, from before the totals
        // are computed until the request is recorded below. A second request in the same group waits, then sees
        // this one in its total, so two requests at the same moment can't both slip under a cap. No aggregate
        // rules => no keys => no lock and no store call.
        await using var reservation = await limitLocks.AcquireAsync(
            aggregateProvider.ReservationKeys(action, dto.Parameters, executingAgentId, dto.RootAgentId, dto.InitiatingUserId), ct);

        // --- Compute any windowed aggregates this snapshot's rules reference for this action ---
        // Returns null (and makes no store call) when no rule uses one, so the engine keeps its "no
        // per-request DB call" property for snapshots that don't aggregate. Fails closed: a store error
        // surfaces rather than letting a limit rule evaluate against a missing value.
        var aggregates = await aggregateProvider.ComputeAsync(
            action, dto.Parameters, executingAgentId, dto.RootAgentId, dto.InitiatingUserId, ct);

        // --- Evaluate policy (against the active immutable snapshot) ---
        var computed = policy.Evaluate(new PolicyEvaluationContext
        {
            Action = action,
            Parameters = dto.Parameters,
            AgentId = executingAgentId,
            RootAgentId = dto.RootAgentId,
            InitiatingUserId = dto.InitiatingUserId,
            Aggregates = aggregates
        });

        // --- Apply the agent's rollout posture (secure-by-default warm-up gate) ---
        // The action's EFFECTIVE consequence is the severest of its own declared tier and its target
        // system's, so an action that hits a money/PII target is treated as HIGH even if it under-declared.
        // During OBSERVE/WARN warm-up a computed block/step-up on a LOW/MEDIUM-consequence action is
        // downgraded to ALLOW and only recorded — but a HIGH/CRITICAL action is always enforced. Both the
        // computed and the effective decision are persisted, so the audit trail shows what WOULD have
        // happened even when a warm-up let it through.
        var effectiveConsequence = ConsequenceTier.Severest(
            action.ConsequenceTier, targets.Find(action.TargetSystem)?.ConsequenceTier);
        var effective = enforcement.Apply(computed, agent, effectiveConsequence);
        if (effective.Downgraded)
            logger.LogWarning("Enforcement {Mode}: agent {Agent} action {Action} would have been {Computed} " +
                "but was allowed ({Tier}, posture {Posture}).",
                effective.Mode, executingAgentId, dto.ActionId, effective.ComputedDecision,
                effective.ConsequenceTier, effective.Posture);

        // --- Persist the request (audit spine) at its decision-derived initial state ---
        var initialStatus = RequestStateMachine.InitialFor(effective.Decision);

        var request = new ActionRequestRecord
        {
            ActionRequestId = Guid.NewGuid(),
            CorrelationId = correlationId,
            IdempotencyKey = dto.IdempotencyKey!,
            RootAgentId = dto.RootAgentId,
            ExecutingAgentId = executingAgentId,
            InitiatingUserId = dto.InitiatingUserId,
            ActionId = dto.ActionId!,
            RequestPayloadJson = payloadJson,
            RequestPayloadHash = payloadHash,
            Status = initialStatus,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        await store.InsertActionRequestAsync(request, ct);
        // Recorded at a state the limits count (pending approval, about to execute) or as a denial: the amount is
        // reserved, so release the limit locks before the slower steps (execution, notifications) run.
        await reservation.DisposeAsync();
        await PersistDecisionAsync(request.ActionRequestId, computed, effective, ct);
        await RecordObservationAsync(request, action, computed, effective, ct);

        switch (effective.Decision)
        {
            case Decision.Allow:
                return await HandleAllowAsync(request, dto.Parameters, computed, effective, correlationId, ct);
            case Decision.RequireApproval:
                return await HandleRequireApprovalAsync(request, computed, effective, correlationId, ct);
            default:
            {
                var denied = BuildResponse(request, correlationId, Decision.Deny, RequestState.Denied, computed.PolicyCode,
                    computed.PolicyVersion, computed.ReasonCode, computed.Reason, payloadHash, effective);
                // A policy DENY is evidence too: sign it into the same chain as executions.
                await AttachDecisionReceiptAsync(denied, request, Decision.Deny, computed.PolicyVersion, computed.ReasonCode, ct);
                return denied;
            }
        }
    }

    private async Task<ActionResponseDto> HandleAllowAsync(
        ActionRequestRecord request, JsonElement parameters, PolicyEvaluationResult computed,
        EffectiveDecision effective, Guid correlationId, CancellationToken ct)
    {
        ExecutionOutcome outcome;
        try
        {
            outcome = await execution.ExecuteAsync(
                request, parameters, Decision.Allow, computed.PolicyVersion, ct);
        }
        catch (TargetExecutionException ex)
        {
            // Policy allowed, but the target system enforced its own rule (defense in depth). Return a clean
            // ALLOW-but-failed result the agent can relay, not a 500.
            var failed = BuildResponse(request, correlationId, Decision.Allow, RequestState.Failed,
                computed.PolicyCode, computed.PolicyVersion, ex.Code, ex.Message, request.RequestPayloadHash, effective);
            return failed;
        }

        // On a warm-up downgrade the reason explains what WOULD have happened; otherwise the policy reason.
        var reason = effective.Downgraded ? effective.Note : computed.Reason;
        var response = BuildResponse(request, correlationId, Decision.Allow, RequestState.Succeeded,
            computed.PolicyCode, computed.PolicyVersion, computed.ReasonCode, reason, request.RequestPayloadHash, effective);
        response.Receipt = ReceiptService.ToDto(outcome.Receipt, request.RequestPayloadHash);
        response.Result = outcome.Execution.ResultJson;
        response.ReceiptNumber = response.Receipt?.ReceiptNumber;
        response.TargetResourceId = response.Receipt?.TargetResourceId ?? outcome.Execution.TargetResourceId;
        return response;
    }

    private async Task<ActionResponseDto> HandleRequireApprovalAsync(
        ActionRequestRecord request, PolicyEvaluationResult result, EffectiveDecision effective,
        Guid correlationId, CancellationToken ct)
    {
        // A rule may require several different people to approve (two-person control for large payments,
        // bank-detail changes and the like); the approval records how many.
        var requiredApprovals = Math.Max(1, result.RequiredApprovals);
        var approval = new ApprovalRequestRecord
        {
            ApprovalRequestId = Guid.NewGuid(),
            ActionRequestId = request.ActionRequestId,
            ApprovalType = requiredApprovals > 1 ? "MultiApprover" : "SingleApprover",
            RequiredApprovals = requiredApprovals,
            ApproverRole = result.RequiredApproverRoles.FirstOrDefault(),
            PayloadHash = request.RequestPayloadHash,
            Status = ApprovalStatus.Pending,
            RequestedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(approvalOptions.ExpiryHours)
        };
        await store.InsertApprovalRequestAsync(approval, ct);

        // Best-effort Teams Adaptive Card to alert approvers (no-op if not configured).
        await teams.NotifyApprovalRequestedAsync(approval, request, ct);

        var response = BuildResponse(request, correlationId, Decision.RequireApproval,
            RequestState.AwaitingApproval, result.PolicyCode, result.PolicyVersion,
            result.ReasonCode, result.Reason, request.RequestPayloadHash, effective);
        response.ApprovalRequestId = approval.ApprovalRequestId;
        // The step-up itself is a signed decision; the execution receipt follows if/when a human approves.
        await AttachDecisionReceiptAsync(response, request, Decision.RequireApproval, result.PolicyVersion, result.ReasonCode, ct);
        return response;
    }

    /// <summary>
    /// Audited early-exit DENY. When the agent was resolved, the record is filed under that agent and the
    /// caller's idempotency key, so a retry replays the denial. When the identity was NOT resolved
    /// (not from APIM, no identity, unregistered, spoofed), the record is filed under an unresolved
    /// marker with a gateway-generated key: an unauthenticated caller must not be able to reserve
    /// ("squat") a key that a legitimate agent will present later. The key the caller supplied is kept
    /// in the decision trace for forensics.
    /// </summary>
    private async Task<ActionResponseDto> DenyAsync(
        ActionRequestDto dto, Guid correlationId, string payloadJson, string payloadHash,
        string reasonCode, string reason, CancellationToken ct, string? resolvedAgentId = null)
    {
        var identityResolved = !string.IsNullOrEmpty(resolvedAgentId);
        // A denial of a RESOLVED agent that never reached policy still counts toward its deny velocity.
        if (identityResolved)
            await safety.RecordDenialAsync(resolvedAgentId!, dto.ActionId ?? string.Empty, reasonCode, correlationId, ct);
        var auditAgentId = identityResolved ? resolvedAgentId!
            // Unresolved: record what the caller CLAIMED (agentId, else the badge, else a placeholder) so
            // the attempt is attributable in the trail; the trace marks it as unverified.
            : !string.IsNullOrWhiteSpace(dto.AgentId) ? dto.AgentId
            : !string.IsNullOrWhiteSpace(dto.AgentIdentity) ? dto.AgentIdentity! : "(unresolved)";

        // Even rejected requests are persisted so every request produces an audit record.
        var request = new ActionRequestRecord
        {
            ActionRequestId = Guid.NewGuid(),
            CorrelationId = correlationId,
            IdempotencyKey = identityResolved ? dto.IdempotencyKey! : $"unresolved:{Guid.NewGuid():N}",
            RootAgentId = dto.RootAgentId,
            ExecutingAgentId = auditAgentId,
            InitiatingUserId = dto.InitiatingUserId,
            ActionId = dto.ActionId!,
            RequestPayloadJson = payloadJson,
            RequestPayloadHash = payloadHash,
            Status = RequestState.Denied,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        await store.InsertActionRequestAsync(request, ct);
        await store.InsertPolicyDecisionAsync(new PolicyDecisionRecord
        {
            PolicyDecisionId = Guid.NewGuid(),
            ActionRequestId = request.ActionRequestId,
            Decision = Decision.Deny,
            PolicyCode = "Gateway.Validation",
            PolicyVersion = "1.0.0",
            DecisionReason = reason,
            EvaluationTraceJson = JsonSerializer.Serialize(new
            {
                reasonCode,
                identityResolved,
                suppliedIdempotencyKey = dto.IdempotencyKey,
                suppliedAgentId = dto.AgentId,
                suppliedAgentIdentity = dto.AgentIdentity
            }),
            EvaluatedUtc = DateTimeOffset.UtcNow
        }, ct);

        var response = BuildResponse(request, correlationId, Decision.Deny, RequestState.Denied,
            "Gateway.Validation", "1.0.0", reasonCode, reason, payloadHash);
        // Sign early-exit denials of a RESOLVED agent (they are attributable evidence of an attempt). Unresolved
        // probes are audited but not signed: an anonymous caller must not be able to grow the chain at will.
        if (identityResolved)
            await AttachDecisionReceiptAsync(response, request, Decision.Deny, "1.0.0", reasonCode, ct);
        return response;
    }

    private Task PersistDecisionAsync(
        Guid requestId, PolicyEvaluationResult computed, EffectiveDecision effective, CancellationToken ct)
        => store.InsertPolicyDecisionAsync(new PolicyDecisionRecord
        {
            PolicyDecisionId = Guid.NewGuid(),
            ActionRequestId = requestId,
            // Record what ACTUALLY happened; the trace preserves the computed decision + posture for evidence.
            Decision = effective.Decision,
            PolicyCode = computed.PolicyCode,
            PolicyVersion = computed.PolicyVersion,
            DecisionReason = effective.Downgraded
                ? $"{computed.Reason} [{effective.Mode}: {effective.Note}]"
                : computed.Reason,
            EvaluationTraceJson = MergeTrace(computed, effective),
            EvaluatedUtc = DateTimeOffset.UtcNow
        }, ct);

    /// <summary>
    /// Append the decision to the observation trail (evidence for rollout). Best-effort: an observation-store
    /// failure must never fail a governed request, so it is logged and swallowed.
    /// </summary>
    private async Task RecordObservationAsync(
        ActionRequestRecord request, ActionDefinition action,
        PolicyEvaluationResult computed, EffectiveDecision effective, CancellationToken ct)
    {
        try
        {
            await observations.AppendAsync(new ObservationRecord
            {
                OccurredUtc = request.CreatedUtc,
                Source = ObservationSource.Gateway,
                AgentId = request.ExecutingAgentId,
                ActionId = request.ActionId,
                TargetSystem = string.IsNullOrWhiteSpace(action.TargetSystem) ? null : action.TargetSystem,
                ComputedDecision = effective.ComputedDecision,
                EffectiveDecision = effective.Decision,
                EnforcementMode = effective.Mode,
                ConsequenceTier = effective.ConsequenceTier,
                WouldHaveBlocked = effective.Downgraded,
                CorrelationId = request.CorrelationId.ToString()
            }, ct);
            feedStatus.RecordGatewayPush();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append observation for request {RequestId} (non-fatal).",
                request.ActionRequestId);
        }
    }

    /// <summary>Combine the policy engine trace with the enforcement-posture outcome for the audit record.</summary>
    private static string MergeTrace(PolicyEvaluationResult computed, EffectiveDecision effective)
    {
        using var doc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(computed.TraceJson) ? "{}" : computed.TraceJson);
        return JsonSerializer.Serialize(new
        {
            enforcement = new
            {
                computedDecision = effective.ComputedDecision,
                effectiveDecision = effective.Decision,
                mode = effective.Mode,
                posture = effective.Posture,
                consequenceTier = effective.ConsequenceTier,
                downgraded = effective.Downgraded,
                note = effective.Note
            },
            policyTrace = doc.RootElement.Clone()
        });
    }

    /// <summary>Current status of a request, or null if the id is unknown. Backs GetActionStatus.</summary>
    public async Task<ActionStatusDto?> GetStatusAsync(Guid actionRequestId, CancellationToken ct)
    {
        var request = await store.GetActionRequestAsync(actionRequestId, ct);
        if (request is null) return null;

        var decision = await store.GetLatestPolicyDecisionAsync(actionRequestId, ct);
        var receipt = await store.GetReceiptForRequestAsync(actionRequestId, ct);

        return new ActionStatusDto
        {
            ActionRequestId = request.ActionRequestId,
            ActionId = request.ActionId,
            AgentId = request.ExecutingAgentId,
            CorrelationId = request.CorrelationId.ToString(),
            Decision = decision?.Decision,
            Status = request.Status,
            PolicyCode = decision?.PolicyCode,
            PolicyVersion = decision?.PolicyVersion,
            Reason = decision?.DecisionReason,
            PayloadHash = request.RequestPayloadHash,
            Receipt = receipt is null ? null : ReceiptService.ToDto(receipt, request.RequestPayloadHash),
            CreatedAtUtc = request.CreatedUtc,
            UpdatedAtUtc = request.UpdatedUtc
        };
    }

    private async Task<ActionResponseDto> BuildReplayResponseAsync(ActionRequestRecord request, CancellationToken ct)
    {
        var decisionRecord = await store.GetLatestPolicyDecisionAsync(request.ActionRequestId, ct);
        var decision = decisionRecord?.Decision ?? Decision.Deny;

        var response = BuildResponse(request, request.CorrelationId, decision, request.Status,
            decisionRecord?.PolicyCode ?? "", decisionRecord?.PolicyVersion ?? "",
            reasonCode: null, decisionRecord?.DecisionReason, request.RequestPayloadHash);
        response.Replayed = true;

        // Any receipt the request has — an execution receipt, or the signed decision receipt for a DENY /
        // REQUIRE_APPROVAL — is replayed with it.
        var receipt = await store.GetReceiptForRequestAsync(request.ActionRequestId, ct);
        if (receipt is not null)
        {
            response.Receipt = ReceiptService.ToDto(receipt, request.RequestPayloadHash);
            response.ReceiptNumber = receipt.ReceiptNumber;
        }
        return response;
    }

    private static ActionResponseDto BuildResponse(
        ActionRequestRecord request, Guid correlationId, string decision, string status,
        string policyCode, string policyVersion, string? reasonCode, string? reason, string payloadHash,
        EffectiveDecision? effective = null)
    {
        // Enrich the request's trace span so the governance decision surfaces in App Insights as
        // customDimensions — correlated (via W3C trace context) with the Copilot Studio conversation.
        var act = System.Diagnostics.Activity.Current;
        if (act is not null)
        {
            act.SetTag("component", "AgentPermit");
            act.SetTag("agentpermit.decision", decision);
            act.SetTag("agentpermit.status", status);
            act.SetTag("agentpermit.actionId", request.ActionId);
            act.SetTag("agentpermit.agentId", request.ExecutingAgentId);
            act.SetTag("agentpermit.correlationId", correlationId.ToString());
            act.SetTag("agentpermit.policyVersion", policyVersion);
            act.SetTag("agentpermit.reasonCode", reasonCode);
            act.SetTag("agentpermit.consequenceTier", effective?.ConsequenceTier?.ToString());
            act.SetTag("agentpermit.enforcementMode", effective?.Mode?.ToString());
        }
        return new()
        {
        CorrelationId = correlationId.ToString(),
        ActionRequestId = request.ActionRequestId,
        ActionId = request.ActionId,
        AgentId = request.ExecutingAgentId,
        Decision = decision,
        Status = status,
        PolicyCode = policyCode,
        PolicyVersion = policyVersion,
        ReasonCode = reasonCode,
        Reason = reason,
        PayloadHash = payloadHash,
        // Surface how binding the decision was, and the computed decision when a warm-up downgraded it.
        EnforcementMode = effective?.Mode,
        ComputedDecision = effective is { Downgraded: true } ? effective.ComputedDecision : null,
        EvaluatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static Guid ResolveCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Guid.NewGuid();
        if (Guid.TryParse(value, out var parsed))
            return parsed;
        throw new GatewayValidationException("correlationId must be a valid UUID when supplied.");
    }
}
