using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// The single entry point agents use to request an action. Returns one of three
/// deterministic decisions (ALLOW / DENY / REQUIRE_APPROVAL). Agents never touch
/// a target system directly — everything flows through here.
/// </summary>
[ApiController]
[Route("api/actions")]
public sealed class ActionsController(
    ActionRequestService service, SecurityOptions security, ILogger<ActionsController> logger) : ControllerBase
{
    [HttpPost("request")]
    [ProducesResponseType(typeof(ActionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestAction([FromBody] ActionRequestDto request, CancellationToken ct)
    {
        try
        {
            var response = await service.ProcessAsync(request, BuildCaller(), ct);
            Response.Headers["X-Correlation-Id"] = response.CorrelationId;
            return Ok(response);
        }
        catch (GatewayValidationException ex)
        {
            logger.LogWarning("Rejected malformed action request: {Message}", ex.Message);
            throw new ValidationException(ex.Message) { Title = "Invalid action request" };
        }
    }

    /// <summary>Status of a previously submitted request. Backs the connector's GetActionStatus.</summary>
    [HttpGet("{actionRequestId:guid}")]
    [ProducesResponseType(typeof(ActionStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActionStatus(Guid actionRequestId, CancellationToken ct)
    {
        var status = await service.GetStatusAsync(actionRequestId, ct);
        if (status is null)
            throw new NotFoundException("Action request not found") { Title = "Action request not found" };

        Response.Headers["X-Correlation-Id"] = status.CorrelationId;
        return Ok(status);
    }

    /// <summary>Establish the caller identity from APIM-forwarded headers (never from the body).</summary>
    private CallerContext BuildCaller()
    {
        var clientId = Request.Headers[security.IdentityHeader].ToString();
        var origin = Request.Headers[security.ApimOriginHeader].ToString();
        var agentIdentity = Request.Headers[security.AgentIdentityHeader].ToString();
        return new CallerContext
        {
            AuthenticatedClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId,
            OriginVerified = !string.IsNullOrEmpty(security.ApimSharedSecret)
                             && string.Equals(origin, security.ApimSharedSecret, StringComparison.Ordinal),
            AssertedAgentIdentity = string.IsNullOrWhiteSpace(agentIdentity) ? null : agentIdentity,
            FromTrustedConnector = !string.IsNullOrWhiteSpace(clientId)
                && security.TrustedConnectorClientIds.Contains(clientId, StringComparer.OrdinalIgnoreCase),
        };
    }
}
