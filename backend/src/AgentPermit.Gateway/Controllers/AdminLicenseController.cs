using System.Text.Json.Serialization;
using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Licensing;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// The deployment's edition &amp; entitlements. Read for the console's plan display; a signed licence can be
/// hot-applied here (PUT) or removed (DELETE) at runtime — no restart. Admin-gated.
/// </summary>
[ApiController]
[Route("api/admin/license")]
public sealed class AdminLicenseController(LicenseService license, AdminAuthorizationService admin) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
        => await admin.IsMemberAsync(HttpContext, ct) ? Ok(Status()) : Forbidden();

    /// <summary>Hot-apply a signed licence token (verify → persist → activate). No restart.</summary>
    [HttpPut]
    public async Task<IActionResult> Apply([FromBody] ApplyLicenseRequest body, CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        if (string.IsNullOrWhiteSpace(body.Token))
            throw new ValidationException("A licence token is required.",
                new Dictionary<string, string[]> { ["token"] = new[] { "A licence token is required." } });
        try
        {
            await license.ApplyAsync(body.Token.Trim(), ct);
            return Ok(Status());
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "Licence rejected", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Remove the applied licence; revert to the bootstrap/config licence.</summary>
    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        if (!Authorized()) return Forbidden();
        await license.ClearAsync(ct);
        return Ok(Status());
    }

    private object Status()
    {
        var e = license.Current;
        return new
        {
            edition = e.Edition,
            maxAgents = e.MaxAgents == int.MaxValue ? (int?)null : e.MaxAgents,
            maxEnvironments = e.MaxEnvironments,
            features = e.FeatureSet,
            expiresUtc = e.ExpiresUtc,
            expired = e.Expired,
            managementLocked = e.ManagementLocked,
            customerId = e.CustomerId,
        };
    }

    public sealed class ApplyLicenseRequest
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
    }
}
