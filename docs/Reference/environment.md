# Environment

`CodeLogicEnvironment` provides read-only runtime information about the current execution environment.

---

## CodeLogicEnvironment

```csharp
public static class CodeLogicEnvironment
{
    static string MachineName  { get; }   // Environment.MachineName
    static string AppRootPath  { get; }   // AppContext.BaseDirectory
    static string AppVersion   { get; internal set; }  // set during InitializeAsync
    static bool   IsDebugging  { get; }   // Debugger.IsAttached
    static bool   IsDevelopment { get; }  // IsDebugging OR #if DEBUG
}
```

---

## Property Reference

### MachineName

The operating system machine name. Used in log entries (when `IncludeMachineName = true`) and health reports.

```csharp
string machine = CodeLogicEnvironment.MachineName;  // "MY-MACHINE"
```

### AppRootPath

The directory containing the application executable. Equivalent to `AppContext.BaseDirectory`.

```csharp
string root = CodeLogicEnvironment.AppRootPath;
// C:\MyApp\bin\Release\net11.0\
```

All framework paths (FrameworkRoot, Libraries, etc.) are relative to this directory.

### AppVersion

The application version string. Set automatically from `CodeLogicOptions.AppVersion` during `InitializeAsync`. Defaults to `"0.0.0"` before initialization.

```csharp
// Before InitializeAsync:
CodeLogicEnvironment.AppVersion  // "0.0.0"

// After InitializeAsync with AppVersion = "1.2.3":
CodeLogicEnvironment.AppVersion  // "1.2.3"
```

`AppVersion` is used in:
- The framework logger startup messages
- `HealthReport.AppVersion`
- The `--version` CLI output

### IsDebugging

`true` when a debugger is currently attached to the process:

```csharp
if (CodeLogicEnvironment.IsDebugging)
    Console.WriteLine("Debugger attached");
```

### IsDevelopment

`true` when running in development mode:

```csharp
public static bool IsDevelopment
{
    get
    {
        if (Debugger.IsAttached) return true;
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
```

This is the same logic the framework uses to select the config file:

| Condition | IsDevelopment | Config file loaded |
|-----------|---------------|--------------------|
| `dotnet run` (Debug build) | `true` | `CodeLogic.Development.json` |
| `dotnet run -c Release` | `false` | `CodeLogic.json` |
| Any build + debugger attached | `true` | `CodeLogic.Development.json` |
| Release build, no debugger | `false` | `CodeLogic.json` |

---

## AppVersion Lifecycle

```
Application starts
    │
    ▼
CodeLogicEnvironment.AppVersion = "0.0.0"   (default)
    │
    ▼
InitializeAsync(o => o.AppVersion = "1.2.3")
    │
    ▼
CodeLogicEnvironment.AppVersion = "1.2.3"   (set internally)
    │
    ▼
Available in: logs, HealthReport, --version output
```

The setter is `internal` — only the framework sets it. Set the version via `CodeLogicOptions.AppVersion`:

```csharp
await CodeLogic.InitializeAsync(o =>
{
    o.AppVersion = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "1.0.0";
});
```

---

## Usage Examples

```csharp
// Log environment info on startup
logger.Info($"Machine: {CodeLogicEnvironment.MachineName}");
logger.Info($"App: {CodeLogicEnvironment.AppVersion}");
logger.Info($"Dev mode: {CodeLogicEnvironment.IsDevelopment}");

// Conditional behavior in development
if (CodeLogicEnvironment.IsDevelopment)
{
    // Seed test data, enable verbose output, etc.
}

// Include version in API responses
app.MapGet("/version", () => new
{
    version = CodeLogicEnvironment.AppVersion,
    machine = CodeLogicEnvironment.MachineName,
    isDevelopment = CodeLogicEnvironment.IsDevelopment
});
```
