using System.Runtime.CompilerServices;

namespace CodeLogic.Core.Results;

/// <summary>The success case of a non-generic <see cref="Result"/>: "it worked, nothing to return".</summary>
public readonly record struct Ok
{
    /// <summary>The single <see cref="Ok"/> value.</summary>
    public static Ok Value => default;
}

/// <summary>Represents the outcome of an operation that can succeed or fail.</summary>
/// <remarks>
/// <see cref="Result"/> is a C# 15 union of <see cref="Ok"/> and <see cref="Results.Error"/>:
/// <code>
/// var message = result switch
/// {
///     Ok           => "saved",
///     Error error  => error.Message,
///     null         => "empty result",   // default(Result)
/// };
/// </code>
/// </remarks>
[Union]
public readonly struct Result : Result.IUnionMembers, IEquatable<Result>
{
    private readonly Error? _error;
    private readonly bool _isSuccess;

    private Result(Ok _) => _isSuccess = true;

    private Result(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _error = error;
    }

    /// <summary>
    /// The union member provider used by the C# compiler for union conversions and pattern matching.
    /// Not intended to be called directly; use <see cref="Success"/>, <see cref="Failure"/> or the implicit conversions.
    /// </summary>
    public interface IUnionMembers
    {
        /// <summary>Creates a successful result.</summary>
        static Result Create(Ok value) => new(value);

        /// <summary>Creates a failed result.</summary>
        static Result Create(Error value) => new(value);

        /// <summary>The contained <see cref="Ok"/> or error, or null for an empty result.</summary>
        object? Value { get; }

        /// <summary>True when the result is a success or carries an error.</summary>
        bool HasValue { get; }

        /// <summary>Gets <see cref="Ok"/> when the result is a success.</summary>
        bool TryGetValue(out Ok value);

        /// <summary>Gets the error when the result is a failure.</summary>
        bool TryGetValue(out Error value);
    }

    object? IUnionMembers.Value => _isSuccess ? Ok.Value : _error;

    /// <inheritdoc cref="IUnionMembers.HasValue"/>
    public bool HasValue => _isSuccess || _error is not null;

    /// <inheritdoc cref="IUnionMembers.TryGetValue(out Ok)"/>
    public bool TryGetValue(out Ok value)
    {
        value = default;
        return _isSuccess;
    }

    /// <inheritdoc cref="IUnionMembers.TryGetValue(out Results.Error)"/>
    public bool TryGetValue(out Error value)
    {
        value = _error!;
        return _error is not null;
    }

    /// <summary>Gets whether the operation succeeded.</summary>
    public bool IsSuccess => _isSuccess;

    /// <summary>Gets whether the operation failed.</summary>
    public bool IsFailure => !_isSuccess;

    /// <summary>Gets the error details when the operation failed; null on success.</summary>
    public Error? Error => _error;

    /// <summary>Creates a successful result.</summary>
    public static Result Success() => new(Ok.Value);

    /// <summary>Creates a failed result with the specified error.</summary>
    public static Result Failure(Error error) => new(error);

    /// <summary>Implicitly converts an <see cref="Results.Error"/> to a failed result.</summary>
    public static implicit operator Result(Error error) => Failure(error);

    /// <summary>Implicitly converts <see cref="Ok"/> to a successful result.</summary>
    public static implicit operator Result(Ok ok) => Success();

    /// <summary>Chains an operation that returns a value when this result is a success.</summary>
    public Result<TOut> Bind<TOut>(Func<Result<TOut>> binder) =>
        _isSuccess ? binder() : Result<TOut>.Failure(ErrorOrEmpty());

    /// <summary>Reduces the result to a single value by handling both cases.</summary>
    public TOut Match<TOut>(Func<TOut> onSuccess, Func<Error, TOut> onFailure) =>
        _isSuccess ? onSuccess() : onFailure(ErrorOrEmpty());

    /// <summary>Executes the action only when the result is a success.</summary>
    public Result OnSuccess(Action action)
    {
        if (_isSuccess) action();
        return this;
    }

    /// <summary>Executes the action only when the result is a failure.</summary>
    public Result OnFailure(Action<Error> action)
    {
        if (!_isSuccess) action(ErrorOrEmpty());
        return this;
    }

    /// <inheritdoc />
    public bool Equals(Result other) => _isSuccess == other._isSuccess && Equals(_error, other._error);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_isSuccess, _error);

    /// <summary>Compares two results for equality.</summary>
    public static bool operator ==(Result left, Result right) => left.Equals(right);

    /// <summary>Compares two results for inequality.</summary>
    public static bool operator !=(Result left, Result right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        _isSuccess ? "Success" : _error is null ? "Empty" : $"Failure({_error})";

    private Error ErrorOrEmpty() => _error ?? Error.Empty;
}
