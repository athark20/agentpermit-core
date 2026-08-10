using AgentPermit.Gateway.Exceptions;
using AgentPermit.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentPermit.Gateway.Controllers;

/// <summary>
/// Admin-only diagnostics for verifying the error + notification contract end to end. Each
/// <c>error/{kind}</c> route deliberately throws one typed <see cref="AgentPermitException"/> (or an
/// unexpected <see cref="System.Exception"/>) so admins/QA can confirm the
/// <see cref="Middleware.GlobalExceptionHandler"/> renders the exact RFC 7807 problem+json — status,
/// stable <c>code</c>, plain-language <c>title</c>, human <c>detail</c>, per-field <c>errors</c> and a
/// correlation <c>traceId</c> — and that the frontend surfaces it correctly. The <c>unexpected</c> kind
/// proves the safe-500 path: the raw exception text is NEVER returned to the client. <c>ping</c> gives a
/// matching success path. Everything here is gated by <see cref="AdminAuthorizationService"/> exactly
/// like the other admin controllers; it changes no state and touches no enforcement boundary.
/// </summary>
[ApiController]
[Route("api/admin/diagnostics")]
public sealed class AdminDiagnosticsController(AdminAuthorizationService admin) : ControllerBase
{
    private bool Authorized() => admin.IsAuthorized(HttpContext);
    private IActionResult Forbidden() => Problem(title: "Admin authorization required", statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Throws the typed exception matching <paramref name="kind"/> so the error contract can be tested on demand.</summary>
    [HttpGet("error/{kind}")]
    public IActionResult Error(string kind)
    {
        if (!Authorized()) return Forbidden();
        throw (kind?.Trim().ToLowerInvariant()) switch
        {
            "validation" => new ValidationException("This is a sample validation error.",
                new Dictionary<string, string[]> { ["example"] = new[] { "This field is just an example." } }),
            "not-found" => new NotFoundException("This is a sample 'not found' error.") { Title = "Sample not found" },
            "conflict" => new ConflictException("This is a sample conflict — something already exists."),
            "licence" => new LicenceRequiredException("This is a sample plan-limit error.") { Title = "Sample plan limit" },
            "forbidden" => new ForbiddenActionException("This is a sample 'not allowed' error."),
            "unexpected" => new InvalidOperationException("Sample unhandled server error — the client must NOT see this text."),
            _ => new ValidationException($"Unknown test kind '{kind}'. Try: validation, not-found, conflict, licence, forbidden, unexpected."),
        };
    }

    /// <summary>A trivial success path so the happy-case (200) can be verified alongside the error kinds.</summary>
    [HttpGet("ping")]
    public IActionResult Ping()
        => Authorized() ? Ok(new { ok = true, at = DateTimeOffset.UtcNow }) : Forbidden();
}
