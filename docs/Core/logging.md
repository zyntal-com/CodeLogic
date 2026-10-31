# Logging

CodeLogic provides a scoped, file-based logging system. Each component (library, application, plugin) receives its own `ILogger` instance configured to write to that component's `logs/` directory.

---

## ILogger Interface

Every component receives an `ILogger` via its context object (`LibraryContext.Logger`, `ApplicationContext.Logger`, `PluginContext.Logger`).

```csharp
public interface ILogger
{
    void Trace(string message);
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
    void Critical(string message, Exception? exception = null);

    // Convenience format overloads
    void Debug(string message, params object?[] args);
    void Info(string message, params object?[] args);
    void Warning(string message, params object?[] args);
    void Error(string message, params object?[] args);
}
```

### Usage

```csharp
public Task OnInitializeAsync(LibraryContext context)
{
    context.Logger.Info("Library initializing");
    context.Logger.Debug("Connection string: {0}", config.ConnectionString);
    context.Logger.Warning("Retry limit reached after {0} attempts", retries);

    try { Connect(); }
    catch (Exception ex)
    {
        context.Logger.Error("Failed to connect to database", ex);
        throw;
    }

    return Task.CompletedTask;
}
```

The format overloads use `string.Format` internally — use `{0}`, `{1}`, etc.

### Cost of a log call (5.0)

Disabled levels are close to free, so leave Debug and Trace calls in hot code:

| Call | Level disabled | Level enabled |
|------|----------------|---------------|
| `logger.Debug($"user {id} took {ms} ms")` | ~8 ns, no allocation. The holes are **not evaluated**. | The message is built and queued. |
| `logger.Debug("user {0} took {1} ms", id, ms)` | ~6 ns. No argument array (`params ReadOnlySpan<object?>`); value-type arguments are still boxed. | `string.Format`, then queued. |
| `logger.Debug("constant text")` | A level check. | Queued. |

Interpolated strings bind to handler overloads (`LogDebugInterpolatedStringHandler`, …) that ask
`ILogger.IsEnabled(level)` first. Use `IsEnabled` yourself when building a message needs more than an
interpolated string:

```csharp
if (logger.IsEnabled(LogLevel.Debug))
    logger.Debug(DescribeQueryPlan(query));
```

Custom `ILogger` implementations get `IsEnabled` (returning `true`) and the new overloads as default
interface members, so they keep compiling; override `IsEnabled` to get the savings.

### Background writing and flushing

Log calls never touch the disk. They queue the line for one background writer per process, which keeps
each log file open, writes lines in order and flushes as soon as the queue is empty. An `Info` line costs
well under a microsecond instead of an open/append/close of the file.

- The queue is bounded (10 000 lines). If the disk cannot keep up, callers wait rather than lines being
  dropped or memory growing.
- Files are opened with `FileShare.ReadWrite | FileShare.Delete`, so log viewers and `tail` keep working.
  Files not written to for two minutes are closed.
- `CodeLogic.StopAsync()` flushes before it returns. Pending lines are also flushed on process exit and on
  an unhandled exception.
- Hosts that need everything on disk at a specific point (tests, tools) call `await Logger.FlushAsync()`.

---

## LogLevel Enum

```csharp
public enum LogLevel
{
    Trace    = 0,   // Most verbose — fine-grained execution tracing
    Debug    = 1,   // Development diagnostics
    Info     = 2,   // Significant milestones in normal flow
    Warning  = 3,   // Unexpected but recoverable conditions (default production minimum)
    Error    = 4,   // Operation failed, application continues
    Critical = 5    // Severe failure, may require shutdown
}
```

### When to use each level

| Level | Use for |
|-------|---------|
| `Trace` | Internal loops, very high frequency calls — disabled by default always |
| `Debug` | Variable values, branching decisions, method entry/exit during development |
| `Info` | Started/stopped, connection established, job completed, config loaded |
| `Warning` | Retry triggered, optional resource missing, deprecated call |
| `Error` | Operation failed but app keeps running — always include the exception |
| `Critical` | Startup failure, unrecoverable state, imminent shutdown |

---

## LoggingMode

Controls how log files are organized on disk:

```csharp
public enum LoggingMode
{
    SingleFile,   // Default: one rolling file per component
    DateFolder    // Files sorted into year/month/day directories
}
```

### SingleFile (default)

Writes to `{componentId}.log`. When the file reaches `MaxFileSizeMb`, it rolls:

```
CL.MAIL.log       ← current
CL.MAIL.1.log     ← previous
CL.MAIL.2.log     ← older
```

Old files beyond `MaxRolledFiles` are deleted.

### DateFolder

Uses a configurable `FileNamePattern` to sort logs into subdirectories:

```
logs/
  2026/
    04/
      06/
        info.log
        warning.log
        error.log
```

Default pattern: `{date:yyyy}/{date:MM}/{date:dd}/{level}.log`

---

## LoggingOptions

The `LoggingOptions` class is passed to the `Logger` constructor. It is produced from `CodeLogic.json` via `LoggingConfig.ToLoggingOptions()`.

```csharp
public class LoggingOptions
{
    public LoggingMode Mode { get; set; } = LoggingMode.SingleFile;

    // SingleFile settings
    public int MaxFileSizeMb { get; set; } = 10;
    public int MaxRolledFiles { get; set; } = 5;

    // DateFolder settings
    public string FileNamePattern { get; set; } = "{date:yyyy}/{date:MM}/{date:dd}/{level}.log";

    // Levels
    public LogLevel GlobalLevel { get; set; } = LogLevel.Warning;
    public bool EnableDebugMode { get; set; } = false;
    public bool CentralizedDebugLog { get; set; } = false;
    public string? CentralizedLogsPath { get; set; }

    // Console output
    public bool EnableConsoleOutput { get; set; } = false;
    public LogLevel ConsoleMinimumLevel { get; set; } = LogLevel.Debug;

    // Format
    public string TimestampFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff";
    public bool IncludeMachineName { get; set; } = true;
}
```

### Creating options manually

```csharp
// Debug-aware defaults: verbose when debugger attached, quiet otherwise
var options = LoggingOptions.CreateWithDebugDefaults();

// Or configure manually
var options = new LoggingOptions
{
    GlobalLevel = LogLevel.Debug,
    EnableConsoleOutput = true,
    ConsoleMinimumLevel = LogLevel.Info,
    Mode = LoggingMode.SingleFile,
    MaxFileSizeMb = 25,
    MaxRolledFiles = 10
};
```

---

## NullLogger

A no-op logger that discards all messages. Use for testing or when logging is optional:

```csharp
public class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();
    // All methods are empty — no-op
}
```

```csharp
// In tests
var lib = new MyLibrary();
var context = new LibraryContext
{
    Logger = NullLogger.Instance,
    // ...
};
```

---

## Development vs Production Behavior

The framework selects `CodeLogic.Development.json` when:
- A debugger is attached at runtime (`Debugger.IsAttached`), OR
- The build is a `DEBUG` build (`#if DEBUG`)

**Production `CodeLogic.json`** (quiet defaults):

```json
{
  "logging": {
    "globalLevel": "Warning",
    "enableConsoleOutput": false
  }
}
```

**Development `CodeLogic.Development.json`** (verbose):

```json
{
  "logging": {
    "globalLevel": "Debug",
    "enableConsoleOutput": true,
    "consoleMinimumLevel": "Debug"
  }
}
```

The framework logger itself always outputs at `Info` or higher to the console during startup, regardless of `globalLevel`. This ensures startup messages are always visible.

---

## Log File Locations

| Component | Log directory |
|-----------|---------------|
| Framework | `CodeLogic/Framework/logs/` |
| Library `CL.Mail` | `CodeLogic/Libraries/CL.Mail/logs/` |
| Application | `CodeLogic/Application/logs/` (or `{ApplicationRootPath}/logs/`) |
| Plugin `MyApp.Plugin` | `CodeLogic/Plugins/MyApp.Plugin/logs/` |

---

## Console Output

Console output is configured per the `logging.enableConsoleOutput` and `logging.consoleMinimumLevel` settings in `CodeLogic.json`.

```json
{
  "logging": {
    "enableConsoleOutput": true,
    "consoleMinimumLevel": "Info"
  }
}
```

The framework logger always outputs startup info to console, independently of this setting.

---

## Centralized Debug Log

When `CentralizedDebugLog = true`, all library loggers also append to a single shared debug log file. This is useful for correlating events across components in production without maintaining separate file handles.

```json
{
  "logging": {
    "centralizedDebugLog": true,
    "centralizedLogsPath": "C:/Logs/myapp-debug.log"
  }
}
```

When `centralizedLogsPath` is null, the log is written to `{FrameworkRoot}/Framework/logs/debug.log`.
