using AgentPermit.Gateway.Contracts;
using AgentPermit.Gateway.Services;
using AgentPermit.Gateway.Simulator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// The simulated external refund target system. It records a refund ONLY when the
/// caller presents the internal execution key — which only the execution service
/// holds. A Copilot agent (or anyone) calling this directly is rejected with 401.
/// </summary>
[ApiController]
[Route("api/simulator")]
public sealed class SimulatorController(
    IRefundLedger ledger,
    IOptions<SimulatorOptions> options,
    ILogger<SimulatorController> logger) : ControllerBase
{
    [HttpPost("refunds")]
    [ProducesResponseType(typeof(SimulatorRefundResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult IssueRefund([FromBody] SimulatorRefundRequest request)
    {
        var expectedKey = options.Value.ExecutionKey;
        var providedKey = Request.Headers[ExecutionAuth.HeaderName].ToString();

        if (string.IsNullOrEmpty(expectedKey) ||
            !string.Equals(providedKey, expectedKey, StringComparison.Ordinal))
        {
            logger.LogWarning("Rejected direct/unauthenticated call to refund simulator for execution {ExecutionId}.",
                request.ExecutionId);
            return Unauthorized(new { error = "Direct target-system access is not permitted." });
        }

        var response = ledger.Record(request);
        return Ok(response);
    }
}
