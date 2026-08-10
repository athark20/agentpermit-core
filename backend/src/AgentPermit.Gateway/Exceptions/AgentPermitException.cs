namespace AgentPermit.Gateway.Exceptions;

/// <summary>
/// Base class for all domain (expected) errors the gateway raises. These are OPT-IN helpers a
/// controller or service may throw to surface a precise, machine-readable reason to the frontend.
/// The <see cref="Middleware.GlobalExceptionHandler"/> maps every subclass to an RFC 7807
/// ProblemDetails response carrying <see cref="Code"/>, the HTTP <see cref="StatusCode"/>, and the
/// human message (as <c>detail</c>). Unlike an unexpected <see cref="System.Exception"/>, the
/// message on these types is considered SAFE to return to the caller.
/// </summary>
public abstract class AgentPermitException : Exception
{
    /// <summary>Stable, machine-readable error code surfaced in <c>Extensions["code"]</c> (e.g. "NOT_FOUND").</summary>
    public abstract string Code { get; }

    /// <summary>HTTP status code the handler should return for this error.</summary>
    public abstract int StatusCode { get; }

    /// <summary>
    /// Optional per-field validation errors (field name -> messages). When present (typically on a
    /// <see cref="ValidationException"/>) the handler includes them under <c>Extensions["errors"]</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }

    /// <summary>Optional plain-language title override; when null the handler picks a default per <see cref="Code"/>.</summary>
    public string? Title { get; init; }

    /// <summary>Creates the exception with a human-readable, client-safe message.</summary>
    protected AgentPermitException(string message) : base(message) { }

    /// <summary>Creates the exception with a client-safe message and an inner cause (logged, never leaked).</summary>
    protected AgentPermitException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The request was well-formed HTTP but failed business/field validation. Maps to HTTP 400.
/// Carry per-field details via <see cref="AgentPermitException.Errors"/>.
/// </summary>
public sealed class ValidationException : AgentPermitException
{
    /// <inheritdoc />
    public override string Code => "VALIDATION";

    /// <inheritdoc />
    public override int StatusCode => 400;

    /// <summary>Creates a validation error with a summary message.</summary>
    public ValidationException(string message = "One or more validation errors occurred.")
        : base(message) { }

    /// <summary>Creates a validation error with a summary message and per-field errors.</summary>
    public ValidationException(string message, IReadOnlyDictionary<string, string[]> errors)
        : base(message) => Errors = errors;
}

/// <summary>The requested resource does not exist. Maps to HTTP 404.</summary>
public sealed class NotFoundException : AgentPermitException
{
    /// <inheritdoc />
    public override string Code => "NOT_FOUND";

    /// <inheritdoc />
    public override int StatusCode => 404;

    /// <summary>Creates a not-found error with a human-readable message.</summary>
    public NotFoundException(string message = "The requested resource was not found.")
        : base(message) { }
}

/// <summary>
/// The request conflicts with the current state of the resource (e.g. a duplicate or a concurrent
/// change). Maps to HTTP 409.
/// </summary>
public sealed class ConflictException : AgentPermitException
{
    /// <inheritdoc />
    public override string Code => "CONFLICT";

    /// <inheritdoc />
    public override int StatusCode => 409;

    /// <summary>Creates a conflict error with a human-readable message.</summary>
    public ConflictException(string message = "The request conflicts with the current state.")
        : base(message) { }
}

/// <summary>
/// The action is gated by the deployment's licence/entitlement (e.g. plan limit reached). Maps to
/// HTTP 402 Payment Required.
/// </summary>
public sealed class LicenceRequiredException : AgentPermitException
{
    /// <inheritdoc />
    public override string Code => "LICENCE_REQUIRED";

    /// <inheritdoc />
    public override int StatusCode => 402;

    /// <summary>Creates a licence-required error with a human-readable message.</summary>
    public LicenceRequiredException(string message = "A licence upgrade is required to perform this action.")
        : base(message) { }
}

/// <summary>
/// The caller is authenticated but not permitted to perform this action. Maps to HTTP 403.
/// (Use for authorization failures; do not use to weaken existing enforcement paths.)
/// </summary>
public sealed class ForbiddenActionException : AgentPermitException
{
    /// <inheritdoc />
    public override string Code => "FORBIDDEN";

    /// <inheritdoc />
    public override int StatusCode => 403;

    /// <summary>Creates a forbidden-action error with a human-readable message.</summary>
    public ForbiddenActionException(string message = "You are not permitted to perform this action.")
        : base(message) { }
}
