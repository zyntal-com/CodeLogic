using System.Globalization;
using CodeLogic.Core.Logging;

namespace CodeLogic;

/// <summary>
/// Strongly-typed representation of <c>CodeLogic.json</c> (or <c>CodeLogic.Development.json</c>).
/// Deserialized by the runtime during <see cref="ICodeLogicRuntime.InitializeAsync"/>.
/// Do not instantiate directly — the runtime owns this object.
/// </summary>
public sealed class CodeLogicConfiguration
{
    /// <summary>Framework identity information (name and version).</summary>
    public FrameworkConfig Framework { get; set; } = new();

    /// <summary>Logging behavior: file mode, levels, console output, and format.</summary>
    public LoggingConfig Logging { get; set; } = new();

    /// <summary>Localization settings: default culture, supported cultures, and template generation.</summary>
    public LocalizationConfig Localization { get; set; } = new();

    /// <summary>Library discovery settings: naming pattern and dependency resolution.</summary>
    public LibrariesConfig Libraries { get; set; } = new();

    /// <summary>Scheduled health check settings: enabled flag and interval.</summary>
    public HealthChecksConfig HealthChecks { get; set; } = new();

    /// <summary>
    /// Checks the values that would otherwise fail later at runtime (on the health timer or the first log
    /// line). The runtime calls this while loading the file and fails initialization when anything is reported.
    /// </summary>
    /// <returns>One message per invalid value; empty when the configuration is usable.</returns>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (HealthChecks is { Enabled: true, IntervalSeconds: <= 0 })
            errors.Add($"healthChecks.intervalSeconds must be greater than 0 (was {HealthChecks.IntervalSeconds}).");
        if (string.IsNullOrWhiteSpace(Logging.TimestampFormat))
        {
            errors.Add("logging.timestampFormat must not be empty.");
        }
        else
        {
            try { _ = DateTime.Now.ToString(Logging.TimestampFormat, CultureInfo.InvariantCulture); }
            catch (FormatException) { errors.Add($"logging.timestampFormat '{Logging.TimestampFormat}' is not a valid date/time format."); }
        }
        if (Logging.MaxFileSizeMb <= 0)
            errors.Add($"logging.maxFileSizeMb must be greater than 0 (was {Logging.MaxFileSizeMb}).");
        if (Logging.MaxRolledFiles < 0)
            errors.Add($"logging.maxRolledFiles must not be negative (was {Logging.MaxRolledFiles}).");
        return errors;
    }
}

/// <summary>
/// Framework identity block within <see cref="CodeLogicConfiguration"/>.
/// Informational only — not used at runtime for any behavior decisions.
/// </summary>
public sealed class FrameworkConfig
{
    /// <summary>Display name for the framework. Default: "CodeLogic".</summary>
    public string Name { get; set; } = "CodeLogic";

    /// <summary>Framework version string. Default: <see cref="CodeLogicEnvironment.FrameworkVersion"/>.</summary>
    public string Version { get; set; } = CodeLogicEnvironment.FrameworkVersion;
}

/// <summary>
/// Controls log file behavior, log levels, console output, and format.
/// Maps to the <c>"logging"</c> section in CodeLogic.json.
/// </summary>
public sealed class LoggingConfig
{
    /// <summary>
    /// Log file mode: <c>"singleFile"</c> (default, rolling by size) or
    /// <c>"dateFolder"</c> (files sorted into year/month/day directories).
    /// </summary>
    public string Mode { get; set; } = "singleFile";

    /// <summary>
    /// Maximum size of a single log file in megabytes before it rolls over.
    /// Applies only in <c>singleFile</c> mode. Default: 10.
    /// </summary>
    public int MaxFileSizeMb { get; set; } = 10;

    /// <summary>
    /// Maximum number of rolled (archived) log files to keep.
    /// Oldest files are deleted when the limit is exceeded. Default: 5.
    /// </summary>
    public int MaxRolledFiles { get; set; } = 5;

    /// <summary>
    /// File name pattern used in <c>dateFolder</c> mode.
    /// Supports <c>{date:format}</c> and <c>{level}</c> tokens.
    /// Default: <c>"{date:yyyy}/{date:MM}/{date:dd}/{level}.log"</c>.
    /// </summary>
    public string FileNamePattern { get; set; } = "{date:yyyy}/{date:MM}/{date:dd}/{level}.log";

    /// <summary>
    /// Minimum log level written to disk. Values: Trace, Debug, Info, Warning, Error, Critical.
    /// Log entries below this level are discarded. Default: "Warning".
    /// Override to "Debug" in CodeLogic.Development.json for development.
    /// </summary>
    public string GlobalLevel { get; set; } = "Warning";

    /// <summary>
    /// When true, logs are also written to the console (stdout).
    /// Default: false. Set to true in CodeLogic.Development.json for development.
    /// </summary>
    public bool EnableConsoleOutput { get; set; } = false;

    /// <summary>
    /// Minimum log level written to the console when <see cref="EnableConsoleOutput"/> is true.
    /// Default: "Debug".
    /// </summary>
    public string ConsoleMinimumLevel { get; set; } = "Debug";

    /// <summary>
    /// Debug flag passed on to <see cref="LoggingOptions.EnableDebugMode"/>. CodeLogic's own loggers do not
    /// read it (it is no longer needed for <see cref="CentralizedDebugLog"/>); libraries may use it as a hint.
    /// Default: false.
    /// </summary>
    public bool EnableDebugMode { get; set; } = false;

    /// <summary>
    /// When true, every framework, application and library logger also writes all of its entries (every
    /// level) to a shared central debug log, in addition to its own log file. Useful for correlating
    /// events across components. Default: false.
    /// </summary>
    public bool CentralizedDebugLog { get; set; } = false;

    /// <summary>
    /// Directory of the centralized debug log. Entries go to <c>{path}/yyyy/MM/dd/debug_all.log</c>.
    /// When null, the runtime uses <c>{FrameworkRoot}/Framework/logs/debug</c>; a relative path is resolved
    /// against the framework root.
    /// </summary>
    public string? CentralizedLogsPath { get; set; }

    /// <summary>
    /// When true, the machine name is prepended to each log entry.
    /// Useful when log files are aggregated from multiple machines. Default: true.
    /// </summary>
    public bool IncludeMachineName { get; set; } = true;

    /// <summary>
    /// Timestamp format string used for each log entry. Default: <c>"yyyy-MM-dd HH:mm:ss.fff"</c>.
    /// </summary>
    public string TimestampFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>
    /// Parses <see cref="GlobalLevel"/> to a <see cref="LogLevel"/> enum value.
    /// Falls back to <see cref="LogLevel.Warning"/> if the string is invalid.
    /// </summary>
    public LogLevel GetGlobalLogLevel() => ParseLevel(GlobalLevel, LogLevel.Warning);

    /// <summary>
    /// Parses <see cref="ConsoleMinimumLevel"/> to a <see cref="LogLevel"/> enum value.
    /// Falls back to <see cref="LogLevel.Debug"/> if the string is invalid.
    /// </summary>
    public LogLevel GetConsoleLogLevel() => ParseLevel(ConsoleMinimumLevel, LogLevel.Debug);

    private static LogLevel ParseLevel(string value, LogLevel fallback) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : fallback;

    /// <summary>
    /// Like <see cref="ToLoggingOptions()"/>, and resolves <see cref="CentralizedLogsPath"/> against
    /// <paramref name="frameworkRootPath"/>: null becomes <c>{frameworkRootPath}/Framework/logs/debug</c>
    /// and a relative path is combined with the framework root. The runtime uses this overload.
    /// </summary>
    /// <param name="frameworkRootPath">Absolute framework root (<see cref="CodeLogicOptions.GetFrameworkPath"/>).</param>
    public LoggingOptions ToLoggingOptions(string frameworkRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(frameworkRootPath);
        var options = ToLoggingOptions();
        options.CentralizedLogsPath = string.IsNullOrWhiteSpace(CentralizedLogsPath)
            ? Path.Combine(frameworkRootPath, "Framework", "logs", "debug")
            : Path.GetFullPath(CentralizedLogsPath, Path.GetFullPath(frameworkRootPath));
        return options;
    }

    /// <summary>
    /// Converts this configuration section to a <see cref="LoggingOptions"/> instance
    /// suitable for passing to the <see cref="Core.Logging.Logger"/> constructor.
    /// <see cref="CentralizedLogsPath"/> is copied as-is; the centralized log needs a path, so use
    /// <see cref="ToLoggingOptions(string)"/> to get the default one.
    /// </summary>
    public LoggingOptions ToLoggingOptions() => new()
    {
        Mode                = Mode == "dateFolder" ? Core.Logging.LoggingMode.DateFolder : Core.Logging.LoggingMode.SingleFile,
        MaxFileSizeMb       = MaxFileSizeMb,
        MaxRolledFiles      = MaxRolledFiles,
        FileNamePattern     = FileNamePattern,
        GlobalLevel         = GetGlobalLogLevel(),
        EnableDebugMode     = EnableDebugMode,
        CentralizedDebugLog = CentralizedDebugLog,
        CentralizedLogsPath = CentralizedLogsPath,
        EnableConsoleOutput = EnableConsoleOutput,
        ConsoleMinimumLevel = GetConsoleLogLevel(),
        TimestampFormat     = TimestampFormat,
        IncludeMachineName  = IncludeMachineName
    };
}

/// <summary>
/// Localization settings within <see cref="CodeLogicConfiguration"/>.
/// Maps to the <c>"localization"</c> section in CodeLogic.json.
/// </summary>
public sealed class LocalizationConfig
{
    /// <summary>
    /// The default culture used when a requested culture is not available.
    /// All string models must have a complete translation for this culture.
    /// Default: "en-US".
    /// </summary>
    public string DefaultCulture { get; set; } = "en-US";

    /// <summary>
    /// List of all culture codes that will be loaded and supported.
    /// Template files are generated for each culture on first run.
    /// Default: ["en-US"].
    /// </summary>
    public List<string> SupportedCultures { get; set; } = ["en-US"];

    /// <summary>
    /// When true, missing localization template files are auto-generated with default strings for the
    /// application, libraries and plugins. When false, nothing is written: a missing culture file falls back
    /// to the model's built-in strings in memory. Default: true.
    /// </summary>
    public bool AutoGenerateTemplates { get; set; } = true;
}

/// <summary>
/// Library discovery and resolution settings within <see cref="CodeLogicConfiguration"/>.
/// Maps to the <c>"libraries"</c> section in CodeLogic.json.
/// </summary>
public sealed class LibrariesConfig
{
    /// <summary>
    /// Glob pattern used to discover library subdirectories under <c>Libraries/</c>.
    /// Only directories matching this pattern are loaded. Default: "CL.*".
    /// </summary>
    public string DiscoveryPattern { get; set; } = "CL.*";

    /// <summary>
    /// When true, libraries are sorted by their declared dependencies before startup
    /// (topological sort). When false, libraries start in registration order.
    /// Default: true.
    /// </summary>
    public bool EnableDependencyResolution { get; set; } = true;

    /// <summary>
    /// Watches library config files (<c>Libraries/*/config*.json</c>) and reloads a section
    /// when its file is edited on disk. Off by default.
    /// </summary>
    public ConfigHotReloadConfig ConfigHotReload { get; set; } = new();
}

/// <summary>
/// Library config file watching within <see cref="LibrariesConfig"/>.
/// Maps to <c>"libraries.configHotReload"</c> in CodeLogic.json.
/// </summary>
public sealed class ConfigHotReloadConfig
{
    /// <summary>When true, edits to library config files are reloaded automatically. Default: false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Quiet period after the last file event before reloading. Default: 500 ms.</summary>
    public int DebounceMs { get; set; } = 500;
}

/// <summary>
/// Scheduled health check settings within <see cref="CodeLogicConfiguration"/>.
/// Maps to the <c>"healthChecks"</c> section in CodeLogic.json.
/// </summary>
public sealed class HealthChecksConfig
{
    /// <summary>
    /// When true, the framework runs health checks on a timer after startup.
    /// Results are published as <see cref="Core.Events.HealthCheckCompletedEvent"/> on the event bus.
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often (in seconds) to run scheduled health checks across all libraries and plugins.
    /// Must be greater than 0 when <see cref="Enabled"/> is true. Default: 30.
    /// </summary>
    public int IntervalSeconds { get; set; } = 30;
}
