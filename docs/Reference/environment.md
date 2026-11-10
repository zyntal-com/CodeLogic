# Environment

`CodeLogicEnvironment` provides read-only runtime information about the current execution environment.

---

## CodeLogicEnvironment

```csharp
public static class CodeLogicEnvironment
{
    public static string  MachineName      { get; }                 // Environment.MachineName
    public static string  AppRootPath      { get; }                 // AppContext.BaseDirectory
    public static string  AppVersion       { get; internal set; }   // set during InitializeAsync
    public static string  FrameworkVersion { get; }                 // CodeLogic assembly version, e.g. "5.0.42"
    public static bool    IsDebugging      { get; }                 // Debugger.IsAttached
    public static string? EnvironmentName  { get; }                 // CODELOGIC_ / DOTNET_ / ASPNETCORE_ENVIRONMENT
    public static bool    IsDevelopment    { get; }                 // EnvironmentName == "Development"
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

### FrameworkVersion

The version of the CodeLogic assembly, from its informational version without the `+commit` build
metadata (for example `5.0.42` or `5.0.42-preview`). It appears in `--version` and `--info` output and is
the default of `framework.version` in `CodeLogic.json`.

```csharp
string framework = CodeLogicEnvironment.FrameworkVersion;  // "5.0.42"
```

### IsDebugging

`true` when a debugger is currently attached to the process. It is informational only and does not
select the config file:

```csharp
if (CodeLogicEnvironment.IsDebugging)
    Console.WriteLine("Debugger attached");
```

### IsDevelopment

`true` when the environment name is `Development` (case-insensitive). The name comes from the first set
variable of `CODELOGIC_ENVIRONMENT`, `DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT`
(`CodeLogicEnvironment.EnvironmentName`).

Unless `CodeLogicOptions.UseDevelopmentConfig` overrides it, this selects the framework config file:

| Condition | IsDevelopment | Config file loaded |
|-----------|---------------|--------------------|
| `DOTNET_ENVIRONMENT=Development` (or `ASPNETCORE_`/`CODELOGIC_ENVIRONMENT`) | `true` | `CodeLogic.Development.json` (if it exists) |
| No environment variable, or any other value | `false` | `CodeLogic.json` |
| `UseDevelopmentConfig = true` | n/a | `CodeLogic.Development.json` (if it exists) |
| `UseDevelopmentConfig = false` | n/a | `CodeLogic.json` |

An attached debugger or a `DEBUG` build does not count as development: attaching a debugger to a
production process never switches its configuration.

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
    framework = CodeLogicEnvironment.FrameworkVersion,
    machine = CodeLogicEnvironment.MachineName,
    isDevelopment = CodeLogicEnvironment.IsDevelopment
});
```
