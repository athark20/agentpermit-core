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
    ActionRequestService service, CallerContextFactory callers, ILogger<ActionsController> logger) : ControllerBase
{
    [HttpPost("request")]
    [ProducesResponseType(typeof(ActionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestAction([FromBody] ActionRequestDto request, CancellationToken ct)
    {
        try
        {
            var response = await service.ProcessAsync(request, await callers.BuildAsync(HttpContext), ct);
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
}
