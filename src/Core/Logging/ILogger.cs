using System.Globalization;
using System.Runtime.CompilerServices;

namespace CodeLogic.Core.Logging;

/// <summary>
/// Provides structured log writing for a single named component (library, application, or plugin).
/// Each component receives its own scoped logger from its context object.
/// Log entries are written to disk (and optionally to the console) based on configured levels.
/// </summary>
public interface ILogger
{
    /// <summary>
    /// Logs a trace-level message. The most verbose level — for fine-grained
    /// execution flow tracing. Disabled by default in all environments.
    /// </summary>
    /// <param name="message">The message to log.</param>
    void Trace(string message);

    /// <summary>
    /// Logs a debug-level message. Intended for diagnostic information during development.
    /// Enabled by default in development (CodeLogic.Development.json sets GlobalLevel to Debug).
    /// </summary>
    /// <param name="message">The message to log.</param>
    void Debug(string message);

    /// <summary>
    /// Logs an informational message about normal application flow.
    /// Use for significant milestones (startup, connections established, jobs completed).
    /// </summary>
    /// <param name="message">The message to log.</param>
    void Info(string message);

    /// <summary>
    /// Logs a warning — an unexpected condition that the application can recover from.
    /// Warnings do not halt execution but should be investigated.
    /// This is the default minimum log level in production.
    /// </summary>
    /// <param name="message">The message to log.</param>
    void Warning(string message);

    /// <summary>
    /// Logs an error — a failure in the current operation.
    /// The application can continue but the operation did not succeed.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="exception">Optional exception to include in the log entry.</param>
    void Error(string message, Exception? exception = null);

    /// <summary>
    /// Logs a critical error — a severe failure that may require immediate attention
    /// or will cause the application to shut down.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="exception">Optional exception to include in the log entry.</param>
    void Critical(string message, Exception? exception = null);

    /// <summary>
    /// True when a message at <paramref name="level"/> would be written anywhere. Lets callers skip
    /// building expensive messages; the interpolated-string overloads already check it for you.
    /// </summary>
    /// <param name="level">The level to test.</param>
    /// <returns>True by default; loggers that filter by level override it.</returns>
    bool IsEnabled(LogLevel level) => true;

    // ── Interpolated strings ─────────────────────────────────────────────────────────────
    // logger.Debug($"user {id} took {ms} ms") binds to these overloads. When the level is disabled
    // the interpolation holes are not evaluated and nothing is allocated.

    /// <summary>Logs a trace message built from an interpolated string, only if Trace is enabled.</summary>
    /// <param name="message">The interpolated message.</param>
    void Trace([InterpolatedStringHandlerArgument("")] ref LogTraceInterpolatedStringHandler message)
    {
        if (message.IsEnabled) Trace(message.ToStringAndClear());
    }

    /// <summary>Logs a debug message built from an interpolated string, only if Debug is enabled.</summary>
    /// <param name="message">The interpolated message.</param>
    void Debug([InterpolatedStringHandlerArgument("")] ref LogDebugInterpolatedStringHandler message)
    {
        if (message.IsEnabled) Debug(message.ToStringAndClear());
    }

    /// <summary>Logs an informational message built from an interpolated string, only if Info is enabled.</summary>
    /// <param name="message">The interpolated message.</param>
    void Info([InterpolatedStringHandlerArgument("")] ref LogInfoInterpolatedStringHandler message)
    {
        if (message.IsEnabled) Info(message.ToStringAndClear());
    }

    /// <summary>Logs a warning built from an interpolated string, only if Warning is enabled.</summary>
    /// <param name="message">The interpolated message.</param>
    void Warning([InterpolatedStringHandlerArgument("")] ref LogWarningInterpolatedStringHandler message)
    {
        if (message.IsEnabled) Warning(message.ToStringAndClear());
    }

    // ── Composite format strings ─────────────────────────────────────────────────────────
    // params ReadOnlySpan: no argument array is allocated, and nothing is formatted when the level is off.

    /// <summary>
    /// Logs a formatted debug message using <see cref="string.Format(IFormatProvider, string, ReadOnlySpan{object})"/>.
    /// </summary>
    /// <param name="message">A composite format string.</param>
    /// <param name="args">Arguments to substitute into the format string.</param>
    void Debug(string message, params ReadOnlySpan<object?> args)
    {
        if (IsEnabled(LogLevel.Debug)) Debug(string.Format(CultureInfo.CurrentCulture, message, args));
    }

    /// <summary>
    /// Logs a formatted informational message using <see cref="string.Format(IFormatProvider, string, ReadOnlySpan{object})"/>.
    /// </summary>
    /// <param name="message">A composite format string.</param>
    /// <param name="args">Arguments to substitute into the format string.</param>
    void Info(string message, params ReadOnlySpan<object?> args)
    {
        if (IsEnabled(LogLevel.Info)) Info(string.Format(CultureInfo.CurrentCulture, message, args));
    }

    /// <summary>
    /// Logs a formatted warning message using <see cref="string.Format(IFormatProvider, string, ReadOnlySpan{object})"/>.
    /// </summary>
    /// <param name="message">A composite format string.</param>
    /// <param name="args">Arguments to substitute into the format string.</param>
    void Warning(string message, params ReadOnlySpan<object?> args)
    {
        if (IsEnabled(LogLevel.Warning)) Warning(string.Format(CultureInfo.CurrentCulture, message, args));
    }

    /// <summary>
    /// Logs a formatted error message using <see cref="string.Format(IFormatProvider, string, ReadOnlySpan{object})"/>.
    /// </summary>
    /// <param name="message">A composite format string.</param>
    /// <param name="args">Arguments to substitute into the format string.</param>
    void Error(string message, params ReadOnlySpan<object?> args)
    {
        if (IsEnabled(LogLevel.Error)) Error(string.Format(CultureInfo.CurrentCulture, message, args));
    }
}
