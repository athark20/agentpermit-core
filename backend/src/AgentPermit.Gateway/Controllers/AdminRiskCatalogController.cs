using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Read + preview access to the connector risk catalog — the versioned data that classifies what an agent's
/// connectors can do. Feeds DLP generation and ungoverned-activity risk. Admin-gated.
/// </summary>
[ApiController]
[Route("api/admin/risk-catalog")]
public sealed class AdminRiskCatalogController(
    ConnectorRiskCatalog catalog, AdminAuthorizationService admin) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    /// <summary>The catalog version + all curated entries.</summary>
    [HttpGet]
    public IActionResult Get()
        => Authorized() ? Ok(new { version = catalog.Version, count = catalog.Entries.Count, entries = catalog.Entries }) : Forbidden();

    /// <summary>Classify a connector id and explain the result (catalog / heuristic / default).</summary>
    [HttpGet("classify")]
    public IActionResult Classify([FromQuery] string connector)
    {
        if (!Authorized()) return Forbidden();
        if (string.IsNullOrWhiteSpace(connector))
            throw new ValidationException("A connector id is required.",
                new Dictionary<string, string[]> { ["connector"] = new[] { "A connector id is required." } });
        return Ok(catalog.Classify(connector));
    }
}
