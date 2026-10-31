using System.Runtime.CompilerServices;

namespace CodeLogic.Core.Results;

/// <summary>
/// Represents the outcome of an operation that returns a value of type <typeparamref name="T"/> on success.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Result{T}"/> is a C# 15 union of <typeparamref name="T"/> and <see cref="Results.Error"/>,
/// so a <c>switch</c> over it is checked for exhaustiveness:
/// </para>
/// <code>
/// var text = result switch
/// {
///     User user           => user.Name,
///     NotFoundError       => "no such user",
///     Error error         => error.Message,
///     null                => "empty result",   // default(Result&lt;T&gt;) or a null success value
/// };
/// </code>
/// <para>
/// The <see cref="IsSuccess"/> / <see cref="Value"/> / <see cref="Error"/> members from 4.x remain available.
/// A <c>default(Result&lt;T&gt;)</c> is neither a success nor carries an error; it matches the <c>null</c> pattern.
/// A success whose value is <c>null</c> also matches the <c>null</c> pattern, but reports <see cref="IsSuccess"/> = true.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of the success value.</typeparam>
[Union]
public readonly struct Result<T> : Result<T>.IUnionMembers, IEquatable<Result<T>>
{
    private readonly T? _value;
    private readonly Error? _error;
    private readonly bool _isSuccess;

    private Result(T value)
    {
        _value = value;
        _isSuccess = true;
    }

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
        static Result<T> Create(T value) => new(value);

        /// <summary>Creates a failed result.</summary>
        static Result<T> Create(Error value) => new(value);

        /// <summary>The contained success value or error, or null for an empty result.</summary>
        object? Value { get; }

        /// <summary>True when the result contains a non-null success value or an error.</summary>
        bool HasValue { get; }

        /// <summary>Gets the success value when the result is a success with a non-null value.</summary>
        bool TryGetValue(out T value);

        /// <summary>Gets the error when the result is a failure.</summary>
        bool TryGetValue(out Error value);
    }

    object? IUnionMembers.Value => _isSuccess ? _value : _error;

    /// <inheritdoc cref="IUnionMembers.HasValue"/>
    public bool HasValue => _isSuccess ? _value is not null : _error is not null;

    /// <inheritdoc cref="IUnionMembers.TryGetValue(out T)"/>
    public bool TryGetValue(out T value)
    {
        value = _value!;
        return _isSuccess && _value is not null;
    }

    /// <inheritdoc cref="IUnionMembers.TryGetValue(out Results.Error)"/>
    public bool TryGetValue(out Error value)
    {
        value = _error!;
        return _error is not null;
    }

    /// <summary>Gets a value indicating whether the result represents a success.</summary>
    public bool IsSuccess => _isSuccess;

    /// <summary>Gets a value indicating whether the result represents a failure.</summary>
    public bool IsFailure => !_isSuccess;

    /// <summary>Gets the success value, or default if the result is a failure.</summary>
    public T? Value => _value;

    /// <summary>Gets the error, or null if the result is a success.</summary>
    public Error? Error => _error;

    /// <summary>Creates a successful result with the specified value.</summary>
    public static Result<T> Success(T value) => new(value);

    /// <summary>Creates a failed result with the specified error.</summary>
    public static Result<T> Failure(Error error) => new(error);

    /// <summary>Implicitly converts a value to a successful result.</summary>
    public static implicit operator Result<T>(T value) => Success(value);

    /// <summary>Implicitly converts an error to a failed result.</summary>
    public static implicit operator Result<T>(Error error) => Failure(error);

    /// <summary>Transforms the success value using the specified mapper function.</summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> mapper) =>
        _isSuccess ? Result<TOut>.Success(mapper(_value!)) : Result<TOut>.Failure(ErrorOrEmpty());

    /// <summary>Asynchronously transforms the success value.</summary>
    public async Task<Result<TOut>> MapAsync<TOut>(Func<T, Task<TOut>> mapper) =>
        _isSuccess ? Result<TOut>.Success(await mapper(_value!).ConfigureAwait(false)) : Result<TOut>.Failure(ErrorOrEmpty());

    /// <summary>Chains an operation that itself returns a result.</summary>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> binder) =>
        _isSuccess ? binder(_value!) : Result<TOut>.Failure(ErrorOrEmpty());

    /// <summary>Asynchronously chains an operation that itself returns a result.</summary>
    public async Task<Result<TOut>> BindAsync<TOut>(Func<T, Task<Result<TOut>>> binder) =>
        _isSuccess ? await binder(_value!).ConfigureAwait(false) : Result<TOut>.Failure(ErrorOrEmpty());

    /// <summary>Reduces the result to a single value by handling both cases.</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        _isSuccess ? onSuccess(_value!) : onFailure(ErrorOrEmpty());

    /// <summary>Converts this generic result to a non-generic <see cref="Result"/>.</summary>
    public Result ToResult() =>
        _isSuccess ? Result.Success() : Result.Failure(ErrorOrEmpty());

    /// <summary>Executes the specified action if the result is a success.</summary>
    public Result<T> OnSuccess(Action<T> action)
    {
        if (_isSuccess) action(_value!);
        return this;
    }

    /// <summary>Executes the specified action if the result is a failure.</summary>
    public Result<T> OnFailure(Action<Error> action)
    {
        if (!_isSuccess) action(ErrorOrEmpty());
        return this;
    }

    /// <summary>Returns the success value, or the specified default if the result is a failure.</summary>
    public T ValueOrDefault(T defaultValue) => _isSuccess ? _value! : defaultValue;

    /// <summary>Returns the success value, or throws if the result is a failure.</summary>
    public T ValueOrThrow() =>
        _isSuccess ? _value! : throw new InvalidOperationException($"Result is failure: {ErrorOrEmpty()}");

    /// <inheritdoc />
    public bool Equals(Result<T> other) =>
        _isSuccess == other._isSuccess
        && EqualityComparer<T?>.Default.Equals(_value, other._value)
        && Equals(_error, other._error);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_isSuccess, _value, _error);

    /// <summary>Compares two results for equality.</summary>
    public static bool operator ==(Result<T> left, Result<T> right) => left.Equals(right);

    /// <summary>Compares two results for inequality.</summary>
    public static bool operator !=(Result<T> left, Result<T> right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        _isSuccess ? $"Success({_value})" : _error is null ? "Empty" : $"Failure({_error})";

    // A default(Result<T>) has no error; surface a descriptive one instead of null.
    private Error ErrorOrEmpty() => _error ?? Error.Empty;
}
