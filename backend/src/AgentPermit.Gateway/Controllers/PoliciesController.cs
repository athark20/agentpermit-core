using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Policies;
using AgentPermit.Gateway.Registry;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Read-only policy surface: inspect the active published snapshot and simulate a request's
/// decision. Simulation never executes, persists, or mutates anything.
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
            rules = s.Rules.OrderBy(r => r.Priority).Select(r => new
            {
                r.Id, r.Name, r.Priority, r.ActionId, r.Decision, r.ApproverRoles, r.Description,
                conditions = r.Conditions.Select(c => new { c.Field, c.Operator, value = c.Value })
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
