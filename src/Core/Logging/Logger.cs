using System.Diagnostics;
using System.Globalization;

namespace CodeLogic.Core.Logging;

/// <summary>File-based logger that writes entries to disk with optional console output.</summary>
public sealed class Logger : ILogger
{
    private readonly string _componentName;
    private readonly string _componentLogsPath;
    private readonly LogLevel _minimumLevel;
    private readonly LoggingOptions _options;
    private readonly string _singleFileName;

    /// <summary>Initializes a new logger for the specified component.</summary>
    public Logger(
        string componentName,
        string componentLogsPath,
        LogLevel minimumLevel,
        LoggingOptions options)
    {
        _componentName = componentName;
        _componentLogsPath = componentLogsPath;
        _minimumLevel = minimumLevel;
        _options = options;
        _singleFileName = componentName.ToLowerInvariant() + ".log";
        Directory.CreateDirectory(_componentLogsPath);
    }

    /// <inheritdoc />
    public void Trace(string message)    => Log(LogLevel.Trace, message);
    /// <inheritdoc />
    public void Debug(string message)    => Log(LogLevel.Debug, message);
    /// <inheritdoc />
    public void Info(string message)     => Log(LogLevel.Info, message);
    /// <inheritdoc />
    public void Warning(string message)  => Log(LogLevel.Warning, message);
    /// <inheritdoc />
    public void Error(string message, Exception? exception = null)    => Log(LogLevel.Error, message, exception);
    /// <inheritdoc />
    public void Critical(string message, Exception? exception = null) => Log(LogLevel.Critical, message, exception);

    /// <summary>
    /// True when a message at <paramref name="level"/> reaches at least one destination: the component
    /// log, the centralized debug log or the console. Disabled levels cost (almost) nothing to call.
    /// </summary>
    public bool IsEnabled(LogLevel level) =>
        level >= _minimumLevel || CentralizedLogEnabled || ConsoleEnabled(level);

    /// <summary>
    /// Completes once every log line written so far has reached disk. Lines are written by a background
    /// writer; the runtime flushes on stop and process exit, so call this only from hosts that need a
    /// point where everything is on disk (tests, tools, custom shutdown).
    /// </summary>
    public static Task FlushAsync() => LogFileWriter.Shared.FlushAsync();

    private bool CentralizedLogEnabled =>
        _options.EnableDebugMode && _options.CentralizedDebugLog && !string.IsNullOrWhiteSpace(_options.CentralizedLogsPath);

    private bool ConsoleEnabled(LogLevel level) =>
        _options.EnableConsoleOutput && level >= _options.ConsoleMinimumLevel;

    private void Log(LogLevel level, string message, Exception? exception = null)
    {
        var toFile = level >= _minimumLevel;
        var toCentral = CentralizedLogEnabled;
        var toConsole = ConsoleEnabled(level);
        if (!toFile && !toCentral && !toConsole)
            return; // nothing to do: skip all formatting

        var timestamp = DateTime.Now.ToString(_options.TimestampFormat, CultureInfo.InvariantCulture);
        var prefix = BuildPrefix(timestamp, level);

        // Component log file
        if (toFile)
            WriteToFile(ResolveFilePath(level), BuildEntry(prefix, message, exception));

        // Centralized debug log (always includes the component name)
        if (toCentral)
            WriteToFile(ResolveCentralizedFilePath(), BuildEntry($"[{_componentName}] {prefix}", message, exception));

        // Console output
        if (toConsole)
            WriteToConsole(level, prefix, message, exception);
    }

    private string BuildPrefix(string timestamp, LogLevel level)
    {
        var levelStr = LevelName(level);
        return _options.IncludeMachineName
            ? $"[{_componentName}][{Environment.MachineName}] {timestamp} [{levelStr}]"
            : $"[{_componentName}] {timestamp} [{levelStr}]";
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => level.ToString().ToUpperInvariant(),
    };

    private static string BuildEntry(string prefix, string message, Exception? exception) =>
        exception is null ? $"{prefix} {message}" : $"{prefix} {message}{Environment.NewLine}{exception}";

    private string ResolveFilePath(LogLevel level)
    {
        if (_options.Mode == LoggingMode.SingleFile)
        {
            // component.log — size-based rolling is done by the background writer
            return Path.Combine(_componentLogsPath, _singleFileName);
        }

        // Date folder pattern. The background writer creates the directory once.
        var now = DateTime.Now;
        var relative = _options.FileNamePattern
            .Replace("{date:yyyy}", now.ToString("yyyy", CultureInfo.InvariantCulture))
            .Replace("{date:MM}",   now.ToString("MM", CultureInfo.InvariantCulture))
            .Replace("{date:dd}",   now.ToString("dd", CultureInfo.InvariantCulture))
            .Replace("{level}",     LevelName(level).ToLowerInvariant());
        return Path.Combine(_componentLogsPath, relative);
    }

    private string ResolveCentralizedFilePath()
    {
        var now = DateTime.Now;
        return Path.Combine(_options.CentralizedLogsPath!,
            now.ToString("yyyy", CultureInfo.InvariantCulture),
            now.ToString("MM", CultureInfo.InvariantCulture),
            now.ToString("dd", CultureInfo.InvariantCulture),
            "debug_all.log");
    }

    private void WriteToFile(string filePath, string content) =>
        LogFileWriter.Shared.Enqueue(filePath, content,
            rollAtBytes: _options.Mode == LoggingMode.SingleFile ? _options.MaxFileSizeMb * 1024L * 1024L : 0,
            maxRolledFiles: _options.MaxRolledFiles);

    private void WriteToConsole(LogLevel level, string prefix, string message, Exception? exception)
    {
        lock (Console.Out)
        {
            try
            {
                Console.ForegroundColor = GetConsoleColor(level);
                Console.WriteLine($"{prefix} {message}");
                if (exception != null)
                    Console.WriteLine(exception);
                Console.ResetColor();
            }
            catch { /* console write failures are silently ignored */ }
        }
    }

    private static ConsoleColor GetConsoleColor(LogLevel level) => level switch
    {
        LogLevel.Trace    => ConsoleColor.DarkGray,
        LogLevel.Debug    => ConsoleColor.Cyan,
        LogLevel.Info     => ConsoleColor.White,
        LogLevel.Warning  => ConsoleColor.Yellow,
        LogLevel.Error    => ConsoleColor.Red,
        LogLevel.Critical => ConsoleColor.DarkRed,
        _                 => ConsoleColor.White
    };
}
