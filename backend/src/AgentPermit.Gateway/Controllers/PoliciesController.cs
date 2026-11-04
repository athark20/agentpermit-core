using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Policies;
using AgentPermit.Gateway.Registry;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Policy surface: inspect the active published snapshot, simulate a request's decision (never executes,
/// persists or mutates anything), and publish rules — directly, or, with Policy:RequireSecondApprover, as a
/// proposed change that a second administrator approves before it goes live.
/// </summary>
[ApiController]
[Route("api/policies")]
public sealed class PoliciesController(
    IPolicySnapshotProvider snapshots,
    IPolicyEngine engine,
    IActionRegistry actions,
    IPolicyPublisher publisher,
    IPolicySnapshotStore snapshotStore,
    AdminAuthorizationService admin,
    AuditService audit,
    PolicyChangeService changes,
    AccessDirectoryService directory,
    ILogger<PoliciesController> logger) : ControllerBase
{
    [HttpGet("active")]
    public IActionResult Active()
    {
        var s = snapshots.Current;
        return Ok(new
        {
            policyCode = s.PolicyCode,
            version = s.Version,
            hash = snapshots.ActiveHash,
            signed = !string.IsNullOrEmpty(s.Signature),
            signatureAlgorithm = s.SignatureAlgorithm,
            signingKeyId = s.SigningKeyId,
            publishedAtUtc = s.PublishedAtUtc,
            publisher = s.Publisher,
            effectiveFrom = s.EffectiveFrom,
            effectiveTo = s.EffectiveTo,
            defaultDecision = s.DefaultDecision,
            ruleCount = s.Rules.Count,
            requireSecondApprover = changes.Required,
            // Every field a rule can carry, so the console can edit a rule without dropping the parts it doesn't
            // show (a windowed limit, the number of approvers, the reason text).
            rules = s.Rules.OrderBy(r => r.Priority).Select(r => new
            {
                r.Id, r.Name, r.Priority, r.ActionId, r.Decision, r.ApproverRoles, r.RequiredApprovals, r.Description, r.Reason,
                conditions = r.Conditions.Select(c => new { c.Field, c.Operator, value = c.Value, c.Aggregate })
            })
        });
    }

    /// <summary>Publish a new immutable, signed policy snapshot; the runtime enforces it with no redeploy.</summary>
    [HttpPost("publish")]
    [ProducesResponseType(typeof(PublishResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Publish([FromBody] PolicySnapshot authored, CancellationToken ct)
    {
        // Admin gate: publishing policy is an administrative action.
        if (!admin.IsAuthorized(HttpContext))
        {
            logger.LogWarning("Rejected unauthorized policy publish attempt.");
            return Problem(title: "Admin authorization required to publish policy",
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Two-person rule: record the change for a second administrator instead of making it live now.
        if (changes.Required)
        {
            try
            {
                var change = await changes.ProposeAsync(authored, Actor(), ct);
                await audit.WriteAsync(HttpContext, "policy.propose", "Policy", change.Version,
                    $"Proposed {change.PolicyCode} v{change.Version} ({change.RuleCount} rules) for a second administrator's approval (change {change.ChangeId})");
                return Accepted(new
                {
                    status = "PendingApproval",
                    changeId = change.ChangeId,
                    policyCode = change.PolicyCode,
                    version = change.Version,
                    message = "Saved for approval. A different administrator must approve these rules before they go live."
                });
            }
            catch (PolicyValidationException ex)
            {
                logger.LogWarning("Policy proposal rejected: {Message}", ex.Message);
                throw new ValidationException(ex.Message) { Title = "Invalid policy" };
            }
        }

        try
        {
            var publisherId = Request.Headers["X-Agent-Client-Id"].ToString();
            var result = await publisher.PublishAsync(authored, string.IsNullOrWhiteSpace(publisherId) ? "admin" : publisherId, ct);
            await audit.WriteAsync(HttpContext, "policy.publish", "Policy", result.Version,
                $"Published {result.PolicyCode} v{result.Version} (signed={result.Signed})");
            return Ok(result);
        }
        catch (PolicyValidationException ex)
        {
            logger.LogWarning("Policy publish rejected: {Message}", ex.Message);
            throw new ValidationException(ex.Message) { Title = "Invalid policy" };
        }
    }

    [HttpGet("versions")]
    public IActionResult Versions() => Ok(new { versions = snapshotStore.ListVersions() });

    /// <summary>Proposed rule changes (newest first) and whether a second administrator is required.</summary>
    [HttpGet("changes")]
    public async Task<IActionResult> Changes(CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct))
            return Problem(title: "Workspace access required", statusCode: StatusCodes.Status403Forbidden);
        return Ok(new { requireSecondApprover = changes.Required, changes = await changes.ListAsync(ct) });
    }

    [HttpGet("changes/{changeId}")]
    public async Task<IActionResult> Change(string changeId, CancellationToken ct)
    {
        if (!await admin.IsMemberAsync(HttpContext, ct))
            return Problem(title: "Workspace access required", statusCode: StatusCodes.Status403Forbidden);
        var change = await changes.GetAsync(changeId, ct)
            ?? throw new NotFoundException("No proposed rule change exists with that id.") { Title = "Change not found" };
        return Ok(change);
    }

    /// <summary>A second, signed-in administrator approves a proposed change: it is signed and published.</summary>
    [HttpPost("changes/{changeId}/approve")]
    public async Task<IActionResult> ApproveChange(string changeId, CancellationToken ct)
    {
        if (!admin.IsAuthorized(HttpContext))
            return Problem(title: "Admin authorization required to approve rule changes", statusCode: StatusCodes.Status403Forbidden);
        try
        {
            var (change, result) = await changes.ApproveAsync(changeId, Actor(), ct);
            await audit.WriteAsync(HttpContext, "policy.publish", "Policy", result.Version,
                $"Published {result.PolicyCode} v{result.Version} (signed={result.Signed}): proposed by {change.ProposedBy}, approved by {change.DecidedBy} (change {change.ChangeId})");
            return Ok(new { change.ChangeId, change.Status, change.ProposedBy, approvedBy = change.DecidedBy, result });
        }
        catch (PolicyValidationException ex)
        {
            throw new ValidationException(ex.Message) { Title = "Invalid policy" };
        }
    }

    /// <summary>Turn a proposed change down (any administrator, including the proposer).</summary>
    [HttpPost("changes/{changeId}/reject")]
    public async Task<IActionResult> RejectChange(string changeId, [FromBody] PolicyChangeDecisionDto? body, CancellationToken ct)
    {
        if (!admin.IsAuthorized(HttpContext))
            return Problem(title: "Admin authorization required to reject rule changes", statusCode: StatusCodes.Status403Forbidden);
        var change = await changes.RejectAsync(changeId, Actor(), body?.Comment, ct);
        await audit.WriteAsync(HttpContext, "policy.change.reject", "Policy", change.Version,
            $"Rejected proposed {change.PolicyCode} v{change.Version} (change {change.ChangeId}) — {change.Comment ?? "no comment"}");
        return Ok(new { change.ChangeId, change.Status, rejectedBy = change.DecidedBy });
    }

    /// <summary>
    /// Who is acting. A person is someone signed in with Entra ID; the shared admin key and dev-open mode are not
    /// people, so they can propose and reject changes but never approve one.
    /// </summary>
    private PolicyChangeActor Actor()
    {
        var (name, email) = directory.Describe(HttpContext);
        var isPerson = !directory.DevOpen && !directory.IsServiceCaller(HttpContext)
                       && HttpContext.User?.Identity?.IsAuthenticated == true;
        return new PolicyChangeActor(name, string.IsNullOrWhiteSpace(email) ? null : email, isPerson);
    }

    [HttpPost("simulate")]
    [ProducesResponseType(typeof(SimulationResultDto), StatusCodes.Status200OK)]
    public IActionResult Simulate([FromBody] SimulationRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.ActionId))
            throw new ValidationException("An action id is required.",
                new Dictionary<string, string[]> { ["actionId"] = new[] { "An action id is required." } });

        // Use the registered action for risk/domain context; fall back to a stub if unknown.
        var action = actions.Find(request.ActionId)
                     ?? new ActionDefinition { ActionId = request.ActionId! };

        var result = engine.Evaluate(new PolicyEvaluationContext
        {
            Action = action,
            Parameters = request.Parameters.ValueKind == JsonValueKind.Object
                ? request.Parameters
                : JsonDocument.Parse("{}").RootElement,
            AgentId = request.AgentId ?? string.Empty,
            RootAgentId = request.RootAgentId,
            InitiatingUserId = request.InitiatingUserId
        });

        return Ok(new SimulationResultDto
        {
            Decision = result.Decision,
            ReasonCode = result.ReasonCode,
            Reason = result.Reason,
            PolicyCode = result.PolicyCode,
            PolicyVersion = result.PolicyVersion,
            MatchedRuleId = result.MatchedRuleId,
            RequiredApproverRoles = result.RequiredApproverRoles,
            SnapshotHash = snapshots.ActiveHash,
            ActionRegistered = actions.Find(request.ActionId) is not null,
            Note = "Simulation only — no action was executed or recorded.",
            EvaluatedAtUtc = DateTimeOffset.UtcNow
        });
    }
}

public sealed class PolicyChangeDecisionDto
{
    [JsonPropertyName("comment")] public string? Comment { get; set; }
}

public sealed class SimulationRequestDto
{
    [JsonPropertyName("actionId")] public string? ActionId { get; set; }
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("rootAgentId")] public string? RootAgentId { get; set; }
    [JsonPropertyName("initiatingUserId")] public string? InitiatingUserId { get; set; }
    [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
}

public sealed class SimulationResultDto
{
    [JsonPropertyName("decision")] public string Decision { get; set; } = "";
    [JsonPropertyName("reasonCode")] public string ReasonCode { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("policyCode")] public string PolicyCode { get; set; } = "";
    [JsonPropertyName("policyVersion")] public string PolicyVersion { get; set; } = "";
    [JsonPropertyName("matchedRuleId")] public string? MatchedRuleId { get; set; }
    [JsonPropertyName("requiredApproverRoles")] public string[] RequiredApproverRoles { get; set; } = Array.Empty<string>();
    [JsonPropertyName("snapshotHash")] public string SnapshotHash { get; set; } = "";
    [JsonPropertyName("actionRegistered")] public bool ActionRegistered { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("evaluatedAtUtc")] public DateTimeOffset EvaluatedAtUtc { get; set; }
}
