using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Generates a Power Platform DLP policy (policy-as-code) from a set of connectors + the risk catalog.
/// Zero-bypass default (Blocked); AgentPermit + low-risk allowed; high-risk blocked. Every decision is
/// explained. The generated artifact is reviewed before deployment. Admin-gated.
/// </summary>
[ApiController]
[Route("api/admin/dlp")]
public sealed class AdminDlpController(
    DlpPolicyGenerator generator, AdminAuthorizationService admin, AuditService audit,
    AgentPermit.Gateway.Licensing.IEntitlementsProvider entitlements) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);
    private static void FeatureGate(AgentPermit.Gateway.Licensing.IEntitlementsProvider entitlements)
    {
        if (!entitlements.Current.Has(AgentPermit.Gateway.Licensing.Features.ConnectorGovernance))
            throw new LicenceRequiredException("Connector governance & automated DLP generation are Enterprise features.") { Title = "Not in your plan" };
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateRequest body, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        FeatureGate(entitlements);
        if (body.Connectors is null)
            throw new ValidationException("A list of connectors is required.",
                new Dictionary<string, string[]> { ["connectors"] = new[] { "A list of connectors is required." } });
        var env = string.IsNullOrWhiteSpace(body.Environment) ? "default" : body.Environment.Trim();
        var result = generator.Generate(body.Connectors, env);
        await audit.WriteAsync(HttpContext, "dlp.generate", "Dlp", env,
            $"Generated DLP policy for {result.Decisions.Count} connector(s) (catalog v{result.CatalogVersion}).");
        return Ok(result);
    }

    public sealed class GenerateRequest
    {
        public List<string>? Connectors { get; set; }
        public string? Environment { get; set; }
    }
}
