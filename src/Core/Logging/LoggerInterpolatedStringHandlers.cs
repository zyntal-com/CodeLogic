using System.Globalization;
using System.Runtime.CompilerServices;

namespace CodeLogic.Core.Logging;

// One handler type per level: the C# compiler picks the handler by the method's parameter type, and
// each handler asks the logger whether its level is enabled before anything is formatted.

/// <summary>Builds a <see cref="LogLevel.Trace"/> message from an interpolated string, only when that level is enabled.</summary>
[InterpolatedStringHandler]
public ref struct LogTraceInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _builder;

    /// <summary>Creates the handler; called by the compiler.</summary>
    public LogTraceInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool isEnabled)
    {
        IsEnabled = isEnabled = logger.IsEnabled(LogLevel.Trace);
        _builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.CurrentCulture) : default;
    }

    /// <summary>Whether the level is enabled (the message is being built).</summary>
    public bool IsEnabled { get; }

    /// <summary>Appends a literal segment; called by the compiler.</summary>
    public void AppendLiteral(string value) => _builder.AppendLiteral(value);

    /// <summary>Appends a formatted value; called by the compiler.</summary>
    public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);

    /// <summary>Appends a formatted value with a format string; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);

    /// <summary>Appends a formatted value with an alignment; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, int alignment) => _builder.AppendFormatted(value, alignment);

    /// <summary>Appends a formatted value with an alignment and format string; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a span of characters; called by the compiler.</summary>
    public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a string; called by the compiler.</summary>
    public void AppendFormatted(string? value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a value with no natural type (e.g. a conditional mixing types); called by the compiler.</summary>
    public void AppendFormatted(object? value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Returns the built message and releases its buffer.</summary>
    public string ToStringAndClear() => IsEnabled ? _builder.ToStringAndClear() : string.Empty;
}

/// <summary>Builds a <see cref="LogLevel.Debug"/> message from an interpolated string, only when that level is enabled.</summary>
[InterpolatedStringHandler]
public ref struct LogDebugInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _builder;

    /// <summary>Creates the handler; called by the compiler.</summary>
    public LogDebugInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool isEnabled)
    {
        IsEnabled = isEnabled = logger.IsEnabled(LogLevel.Debug);
        _builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.CurrentCulture) : default;
    }

    /// <summary>Whether the level is enabled (the message is being built).</summary>
    public bool IsEnabled { get; }

    /// <summary>Appends a literal segment; called by the compiler.</summary>
    public void AppendLiteral(string value) => _builder.AppendLiteral(value);

    /// <summary>Appends a formatted value; called by the compiler.</summary>
    public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);

    /// <summary>Appends a formatted value with a format string; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);

    /// <summary>Appends a formatted value with an alignment; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, int alignment) => _builder.AppendFormatted(value, alignment);

    /// <summary>Appends a formatted value with an alignment and format string; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a span of characters; called by the compiler.</summary>
    public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a string; called by the compiler.</summary>
    public void AppendFormatted(string? value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a value with no natural type (e.g. a conditional mixing types); called by the compiler.</summary>
    public void AppendFormatted(object? value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Returns the built message and releases its buffer.</summary>
    public string ToStringAndClear() => IsEnabled ? _builder.ToStringAndClear() : string.Empty;
}

/// <summary>Builds a <see cref="LogLevel.Info"/> message from an interpolated string, only when that level is enabled.</summary>
[InterpolatedStringHandler]
public ref struct LogInfoInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _builder;

    /// <summary>Creates the handler; called by the compiler.</summary>
    public LogInfoInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool isEnabled)
    {
        IsEnabled = isEnabled = logger.IsEnabled(LogLevel.Info);
        _builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.CurrentCulture) : default;
    }

    /// <summary>Whether the level is enabled (the message is being built).</summary>
    public bool IsEnabled { get; }

    /// <summary>Appends a literal segment; called by the compiler.</summary>
    public void AppendLiteral(string value) => _builder.AppendLiteral(value);

    /// <summary>Appends a formatted value; called by the compiler.</summary>
    public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);

    /// <summary>Appends a formatted value with a format string; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);

    /// <summary>Appends a formatted value with an alignment; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, int alignment) => _builder.AppendFormatted(value, alignment);

    /// <summary>Appends a formatted value with an alignment and format string; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a span of characters; called by the compiler.</summary>
    public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a string; called by the compiler.</summary>
    public void AppendFormatted(string? value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a value with no natural type (e.g. a conditional mixing types); called by the compiler.</summary>
    public void AppendFormatted(object? value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Returns the built message and releases its buffer.</summary>
    public string ToStringAndClear() => IsEnabled ? _builder.ToStringAndClear() : string.Empty;
}

/// <summary>Builds a <see cref="LogLevel.Warning"/> message from an interpolated string, only when that level is enabled.</summary>
[InterpolatedStringHandler]
public ref struct LogWarningInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _builder;

    /// <summary>Creates the handler; called by the compiler.</summary>
    public LogWarningInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool isEnabled)
    {
        IsEnabled = isEnabled = logger.IsEnabled(LogLevel.Warning);
        _builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.CurrentCulture) : default;
    }

    /// <summary>Whether the level is enabled (the message is being built).</summary>
    public bool IsEnabled { get; }

    /// <summary>Appends a literal segment; called by the compiler.</summary>
    public void AppendLiteral(string value) => _builder.AppendLiteral(value);

    /// <summary>Appends a formatted value; called by the compiler.</summary>
    public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);

    /// <summary>Appends a formatted value with a format string; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);

    /// <summary>Appends a formatted value with an alignment; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, int alignment) => _builder.AppendFormatted(value, alignment);

    /// <summary>Appends a formatted value with an alignment and format string; called by the compiler.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a span of characters; called by the compiler.</summary>
    public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a string; called by the compiler.</summary>
    public void AppendFormatted(string? value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Appends a value with no natural type (e.g. a conditional mixing types); called by the compiler.</summary>
    public void AppendFormatted(object? value, int alignment = 0, string? format = null) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>Returns the built message and releases its buffer.</summary>
    public string ToStringAndClear() => IsEnabled ? _builder.ToStringAndClear() : string.Empty;
}
