using System.Diagnostics;
using AgentPermit.Gateway.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace AgentPermit.Gateway.Middleware;

/// <summary>
/// Catches UNHANDLED exceptions escaping the pipeline and converts them into a single, consistent
/// RFC 7807 <c>application/problem+json</c> error contract so the frontend can render the exact
/// reason. Every response carries a plain-language <c>title</c>, a human <c>detail</c>, the HTTP
/// <c>status</c>, a stable machine <c>code</c>, and a correlation <c>traceId</c>.
/// <para>
/// Security: for unexpected (500) errors the client only ever sees a generic safe message; the full
/// exception is logged server-side with the traceId. This handler does NOT alter any controller that
/// already returns <c>Problem(...)</c> or a specific status code — it only handles what would
/// otherwise become an unhandled 500.
/// </para>
/// </summary>
public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    private const string ErrorTypeBase = "https://agentpermit.dev/errors/";

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // The client went away (disconnect / aborted request). Not our error: don't log noise,
        // don't try to write a body — let the framework treat it as an aborted request.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
            return false;

        var (status, code, title, detail, errors) = Map(exception);

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Substitute the real correlation id into the generic 500 message.
        detail = detail.Replace("{traceId}", traceId, StringComparison.Ordinal);

        // Log: 5xx are real faults (full exception + stack); 4xx are expected/client errors (no stack noise).
        if (status >= 500)
            logger.LogError(exception, "Unhandled exception [{Code}] traceId={TraceId} path={Path}",
                code, traceId, httpContext.Request.Path);
        else
            logger.LogWarning("Handled domain error [{Code}] status={Status} traceId={TraceId} path={Path}: {Message}",
                code, status, traceId, httpContext.Request.Path, exception.Message);

        // Status must be set before writing the body.
        httpContext.Response.StatusCode = status;

        var problemContext = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Detail = detail,
                Type = ErrorTypeBase + code.ToLowerInvariant(),
            }
        };
        problemContext.ProblemDetails.Extensions["code"] = code;
        problemContext.ProblemDetails.Extensions["traceId"] = traceId;
        if (errors is not null)
            problemContext.ProblemDetails.Extensions["errors"] = errors;

        // TryWriteAsync returns false if no writer accepts the request (e.g. response already started).
        return await problemDetailsService.TryWriteAsync(problemContext);
    }

    /// <summary>Maps an exception to the (status, code, title, detail, errors) tuple of the error contract.</summary>
    private static (int Status, string Code, string Title, string Detail, IReadOnlyDictionary<string, string[]>? Errors)
        Map(Exception exception) => exception switch
    {
        AgentPermitException ex => (ex.StatusCode, ex.Code, ex.Title ?? TitleFor(ex.Code), ex.Message, ex.Errors),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, "BAD_REQUEST", "Bad request",
            "The request could not be read. Please check the format and try again.", null),
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "FORBIDDEN", "Not allowed",
            "You are not permitted to perform this action.", null),
        // Never leak the real exception for unexpected errors.
        _ => (StatusCodes.Status500InternalServerError, "UNEXPECTED", "Something went wrong",
            "Something went wrong on our side. Quote reference {traceId} to support.", null),
    };

    /// <summary>Friendly, plain-language title per stable error code.</summary>
    private static string TitleFor(string code) => code switch
    {
        "VALIDATION" => "Check your details",
        "NOT_FOUND" => "Not found",
        "CONFLICT" => "Already exists",
        "LICENCE_REQUIRED" => "Licence required",
        "FORBIDDEN" => "Not allowed",
        "UNAUTHENTICATED" => "Sign-in required",
        "BAD_REQUEST" => "Bad request",
        _ => "Something went wrong",
    };
}
