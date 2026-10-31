namespace CodeLogic.Core.Results;

/// <summary>
/// Thrown by <see cref="Result.ThrowIfFailure"/> and <see cref="Result{T}.ValueOrThrow"/> when the result is a
/// failure. Carries the <see cref="Results.Error"/>, so callers that catch it can still inspect the code and kind.
/// Derives from <see cref="InvalidOperationException"/>, which these members threw before.
/// </summary>
public sealed class ResultFailedException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    public ResultFailedException(Error error)
        : base($"Result is failure: {error}")
    {
        Error = error;
    }

    /// <summary>The error the result carried (<see cref="Results.Error.Empty"/> for an empty result).</summary>
    public Error Error { get; }
}
