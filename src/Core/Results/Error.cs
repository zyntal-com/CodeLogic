namespace CodeLogic.Core.Results;

/// <summary>
/// Represents a structured error with a machine-readable code, human-readable message,
/// optional details, and optional inner error for chaining.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Error"/> is a C# 15 <c>closed</c> record: its direct subtypes are fixed, so a <c>switch</c>
/// over the kind of error is exhaustive without a default arm:
/// </para>
/// <code>
/// int status = error switch
/// {
///     NotFoundError     => 404,
///     ValidationError   => 422,
///     UnauthorizedError => 401,
///     ConflictError     => 409,
///     TimeoutError      => 504,
///     UnavailableError  => 503,
///     InternalError     => 500,
/// };
/// </code>
/// <para>Use the static factory methods (<see cref="NotFound"/>, <see cref="Validation"/>, ...) or construct a subtype directly.</para>
/// </remarks>
/// <param name="Code">
/// Machine-readable error code in <c>"category.specific_issue"</c> format, e.g. <c>"user.not_found"</c>.
/// Use constants from <see cref="ErrorCode"/> for framework errors.
/// </param>
/// <param name="Message">Human-readable description of the error. Suitable for logging and display.</param>
/// <param name="Details">Optional additional context (e.g., the field name, the invalid value, the file path).</param>
/// <param name="InnerError">Optional chained error that caused this error.</param>
public closed record Error(string Code, string Message, string? Details = null, Error? InnerError = null)
{
    /// <summary>
    /// The error reported by a <c>default</c> <see cref="Result"/> / <see cref="Result{T}"/>, which was never
    /// assigned a success or a failure.
    /// </summary>
    public static Error Empty { get; } =
        new InternalError("result.empty", "The result holds neither a value nor an error (default Result).");

    // Factory methods

    /// <summary>Creates a <see cref="NotFoundError"/>. HTTP analog: 404 Not Found.</summary>
    public static Error NotFound(string code, string message, string? details = null)
        => new NotFoundError(code, message, details);

    /// <summary>Creates a <see cref="ValidationError"/> (invalid input data). HTTP analog: 422 Unprocessable Entity.</summary>
    public static Error Validation(string code, string message, string? details = null)
        => new ValidationError(code, message, details);

    /// <summary>Creates an <see cref="InternalError"/> (internal/unexpected failure). HTTP analog: 500 Internal Server Error.</summary>
    /// <param name="code">Error code identifying the failure.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="details">Optional additional details.</param>
    /// <param name="innerError">Optional chained error that caused this failure.</param>
    public static Error Internal(string code, string message, string? details = null, Error? innerError = null)
        => new InternalError(code, message, details, innerError);

    /// <summary>Creates an <see cref="UnauthorizedError"/> (authentication failure). HTTP analog: 401 Unauthorized.</summary>
    public static Error Unauthorized(string code, string message, string? details = null)
        => new UnauthorizedError(code, message, details);

    /// <summary>Creates a <see cref="ConflictError"/> (duplicate, concurrent modification). HTTP analog: 409 Conflict.</summary>
    public static Error Conflict(string code, string message, string? details = null)
        => new ConflictError(code, message, details);

    /// <summary>Creates a <see cref="TimeoutError"/>. HTTP analog: 408 Request Timeout / 504 Gateway Timeout.</summary>
    public static Error Timeout(string code, string message, string? details = null)
        => new TimeoutError(code, message, details);

    /// <summary>Creates an <see cref="UnavailableError"/> (dependency temporarily unavailable). HTTP analog: 503 Service Unavailable.</summary>
    public static Error Unavailable(string code, string message, string? details = null)
        => new UnavailableError(code, message, details);

    /// <summary>
    /// Wraps an exception as an <see cref="InternalError"/>.
    /// The exception message becomes the error message; the exception type name becomes the details.
    /// </summary>
    /// <param name="ex">The exception to wrap.</param>
    /// <param name="code">Error code to use. Default: "internal.exception".</param>
    public static Error FromException(Exception ex, string code = "internal.exception")
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new InternalError(code, ex.Message, ex.GetType().Name);
    }

    /// <summary>
    /// Returns a copy of this error (same kind) with the given inner error attached.
    /// </summary>
    /// <param name="inner">The root cause error.</param>
    public Error WithInner(Error inner) => this with { InnerError = inner };

    /// <summary>
    /// Returns a copy of this error (same kind) with the given details string.
    /// </summary>
    /// <param name="details">Additional context to attach.</param>
    public Error WithDetails(string details) => this with { Details = details };

    /// <summary>
    /// Returns a string representation: <c>[code] message (details) → innerError</c>.
    /// </summary>
    public sealed override string ToString() =>
        InnerError is null
            ? $"[{Code}] {Message}{(Details is null ? "" : $" ({Details})")}"
            : $"[{Code}] {Message}{(Details is null ? "" : $" ({Details})")} → {InnerError}";
}

/// <summary>A resource could not be found. HTTP analog: 404 Not Found.</summary>
/// <inheritdoc cref="Error" path="/param"/>
public sealed record NotFoundError(string Code, string Message, string? Details = null, Error? InnerError = null)
    : Error(Code, Message, Details, InnerError);

/// <summary>Input data failed validation. HTTP analog: 422 Unprocessable Entity.</summary>
/// <inheritdoc cref="Error" path="/param"/>
public sealed record ValidationError(string Code, string Message, string? Details = null, Error? InnerError = null)
    : Error(Code, Message, Details, InnerError);

/// <summary>An internal or unexpected failure. HTTP analog: 500 Internal Server Error.</summary>
/// <inheritdoc cref="Error" path="/param"/>
public sealed record InternalError(string Code, string Message, string? Details = null, Error? InnerError = null)
    : Error(Code, Message, Details, InnerError);

/// <summary>Authentication failed or is missing. HTTP analog: 401 Unauthorized.</summary>
/// <inheritdoc cref="Error" path="/param"/>
public sealed record UnauthorizedError(string Code, string Message, string? Details = null, Error? InnerError = null)
    : Error(Code, Message, Details, InnerError);

/// <summary>The operation conflicts with current state (duplicate, concurrent modification). HTTP analog: 409 Conflict.</summary>
/// <inheritdoc cref="Error" path="/param"/>
public sealed record ConflictError(string Code, string Message, string? Details = null, Error? InnerError = null)
    : Error(Code, Message, Details, InnerError);

/// <summary>The operation exceeded its time limit. HTTP analog: 408 Request Timeout / 504 Gateway Timeout.</summary>
/// <inheritdoc cref="Error" path="/param"/>
public sealed record TimeoutError(string Code, string Message, string? Details = null, Error? InnerError = null)
    : Error(Code, Message, Details, InnerError);

/// <summary>A service or dependency is temporarily unavailable. HTTP analog: 503 Service Unavailable.</summary>
/// <inheritdoc cref="Error" path="/param"/>
public sealed record UnavailableError(string Code, string Message, string? Details = null, Error? InnerError = null)
    : Error(Code, Message, Details, InnerError);
