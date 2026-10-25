# CodeLogic 3 — Complete Build Plan

> Historical design document. Some implementation details here may no longer match the current codebase or published documentation.

## Vision

A modular .NET 10 framework with clean layered architecture:
- **Core** — standalone engines, zero framework coupling, usable in any .NET app
- **Framework** — lifecycle orchestration wiring Core into Libraries and Application (which includes Plugins)
- **Libs** — official CL.* integrations implementing ILibrary
- **Static facade** — unchanged public API + `ICodeLogicRuntime` for DI/testing

---

## Repository Structure

```
CodeLogic/                          ← this repo
├── src/
│   ├── Core/
│   │   ├── Logging/
│   │   ├── Configuration/
│   │   ├── Localization/
│   │   ├── Events/
│   │   ├── Results/
│   │   └── Utilities/
│   │
│   ├── Framework/
│   │   ├── Libraries/
│   │   └── Application/        ← IApplication + Plugins (app-managed)
│   │       └── Plugins/
│   │
│   ├── Runtime/                ← orchestrator internals
│   │   ├── ICodeLogicRuntime.cs
│   │   ├── CodeLogicRuntime.cs
│   │   ├── CodeLogicOptions.cs
│   │   ├── CodeLogicConfiguration.cs
│   │   ├── HealthReport.cs
│   │   └── InitializationResult.cs
│   │
│   ├── CodeLogic.cs            ← static facade (public entry point)
│   ├── Libraries.cs            ← static accessor (framework-managed libs)
│   └── CodeLogicEnvironment.cs ← global static
│
├── docs/
│   ├── BUILD_PLAN.md
│   ├── Core/
│   └── Framework/
│
├── samples/                        ← separate git repo later
├── CodeLogic.sln
├── LICENSE
└── README.md

CodeLogic.Libs/                     ← separate dir in same monorepo
├── CL.Core/
├── CL.SQLite/
├── CL.MySQL2/
└── ... (11 libs total)
```

---

## Namespace Map

```
CodeLogic.Core.Logging          ILogger, Logger, NullLogger, LogLevel, LoggingOptions, LoggingMode
CodeLogic.Core.Configuration    IConfigurationManager, ConfigModelBase, ConfigValidationResult
CodeLogic.Core.Localization     ILocalizationManager, LocalizationModelBase, attributes
CodeLogic.Core.Events           IEventBus, EventBus, IEvent, EventSubscription
CodeLogic.Core.Results          Result<T>, Result, Error, ErrorCode
CodeLogic.Core.Utilities        SemanticVersion, StartupValidator, FirstRunManager

CodeLogic.Framework.Libraries            ILibrary, LibraryContext, LibraryManager, LibraryManifest
                                         LibraryDependency, LoadedLibrary, LibraryState
CodeLogic.Framework.Application          IApplication, ApplicationContext, ApplicationManifest
CodeLogic.Framework.Application.Plugins  IPlugin, PluginContext, PluginManifest, PluginManager
                                         PluginLoadContext, LoadedPlugin, PluginState, PluginOptions

CodeLogic                       CodeLogic (static facade), Libraries (static accessor)
                                CodeLogicEnvironment
                                ICodeLogicRuntime, CodeLogicRuntime
                                CodeLogicOptions, CodeLogicConfiguration
                                InitializationResult, HealthReport

Note: No static Plugins accessor — plugins are app-managed.
      Apps instantiate PluginManager directly:
        new PluginManager(CodeLogic.GetEventBus(), options)
```

---

## Phase 1 — Core

### 1.1 Results (`src/Core/Results/`)

Unified result types used by everything — libs, framework, application code.
Eliminates each lib defining its own `Result<T>`.

**Files:**
- `Result.cs` — success/failure, no data
- `Result{T}.cs` — success/failure with typed data
- `Error.cs` — structured error: Code, Message, Details?, InnerError?
- `ErrorCode.cs` — well-known error code constants

**Design:**
```csharp
// Success
Result<User> result = Result<User>.Success(user);
Result result = Result.Success();

// Failure
Result<User> result = Result<User>.Failure(
    Error.NotFound("user.not_found", "User does not exist"));

// Implicit conversion
Result<string> r = "hello";             // auto-wraps as success
Result<string> r = Error.NotFound(..); // auto-wraps as failure

// Consumption
if (result.IsSuccess)
    DoSomething(result.Value);
else
    logger.Error(result.Error.Message);
```

**Error factory methods:**
```csharp
Error.NotFound(code, message)
Error.Validation(code, message)
Error.Internal(code, message, Exception? ex = null)
Error.Unauthorized(code, message)
Error.Conflict(code, message)
Error.Timeout(code, message)
```

---

### 1.2 Logging (`src/Core/Logging/`)

**Files:**
- `ILogger.cs`
- `Logger.cs`
- `NullLogger.cs` — no-op for testing and optional logging
- `LogLevel.cs`
- `LoggingOptions.cs`
- `LoggingMode.cs` — enum: `SingleFile` | `DateFolder`

**Log Modes:**

`SingleFile` (DEFAULT) — simple, one file per component, rolls by size:
```
component/logs/
├── component.log        ← active
├── component.1.log      ← rolled
├── component.2.log      ← older
└── component.3.log      ← oldest (deleted when maxRolledFiles exceeded)
```

`DateFolder` (opt-in) — sorted by date, useful for high-volume production:
```
component/logs/
└── 2026/04/06/
    ├── info.log
    └── error.log
```

**Debug/Console defaults via `Debugger.IsAttached`:**
- Attached: `GlobalLevel=Debug`, `EnableConsoleOutput=true`, `ConsoleMinimumLevel=Debug`
- Not attached: `GlobalLevel=Warning`, `EnableConsoleOutput=false`
- CodeLogic.json always overrides these — user has full control

**Log entry format includes machine name:**
```
[CL.SQLITE][SERVER-01] 2026-04-06 14:23:01.123 [INFO] Table synced: users
```

**LoggingOptions:**
```csharp
public class LoggingOptions
{
    public LoggingMode Mode { get; set; } = LoggingMode.SingleFile;

    // SingleFile settings
    public int MaxFileSizeMb { get; set; } = 10;
    public int MaxRolledFiles { get; set; } = 5;

    // DateFolder settings
    public string FileNamePattern { get; set; } = "{date:yyyy}/{date:MM}/{date:dd}/{level}.log";

    // Levels (set automatically from Debugger.IsAttached, overridable via config)
    public LogLevel GlobalLevel { get; set; } = LogLevel.Warning;
    public bool EnableDebugMode { get; set; } = false;
    public bool CentralizedDebugLog { get; set; } = false;
    public string? CentralizedLogsPath { get; set; }

    // Console (set automatically from Debugger.IsAttached, overridable via config)
    public bool EnableConsoleOutput { get; set; } = false;
    public LogLevel ConsoleMinimumLevel { get; set; } = LogLevel.Debug;

    // Format
    public string TimestampFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff";
    public bool IncludeMachineName { get; set; } = true;
}
```

---

### 1.3 Configuration (`src/Core/Configuration/`)

**Files:**
- `IConfigurationManager.cs`
- `ConfigModelBase.cs`
- `ConfigurationManager.cs`
- `ConfigValidationResult.cs`

**What's preserved from old framework (all fixes):**
- `SaveAsync` validates before writing
- Reflection uses null-safe calls
- `LoadAllAsync` / `GenerateAllDefaultsAsync` with proper error handling

**What's new:**
- `ReloadAsync<T>()` — reload a single config type from disk (opt-in per lib)
- `ReloadAllAsync()` — reload all registered configs
- Publishes `ConfigReloadedEvent` via EventBus after reload so interested
  parties can react (e.g. Logger reloading its level)

**Config reload policy:**
- Libraries load config once at startup — it is stable and reliable
- Reloading is SAFE for: log levels, health check intervals, pool sizes
- Reloading is NOT SAFE for: connection strings, database paths, core settings
- Libraries opt-in explicitly by calling `context.Configuration.ReloadAsync<T>()`
- Localization reload is ALWAYS safe — pure UI strings with no side effects

---

### 1.4 Localization (`src/Core/Localization/`)

**Files:**
- `ILocalizationManager.cs`
- `LocalizationModelBase.cs`
- `LocalizationManager.cs`
- `LocalizationSectionAttribute.cs`
- `LocalizedStringAttribute.cs`

**What's new:**
- `ReloadAsync()` — reload all localization files from disk (always safe)
- `GetSupportedCultures()` — list loaded cultures
- Publishes `LocalizationReloadedEvent` via EventBus after reload

---

### 1.5 Events (`src/Core/Events/`)

Decoupled in-process pub/sub. Libraries, apps, and plugins communicate
without direct references. No external broker dependency.

One `EventBus` instance is created by `CodeLogicRuntime` and injected into
every context (`LibraryContext`, `ApplicationContext`, `PluginContext`) so all
components share the same bus.

**Files:**
- `IEvent.cs` — marker interface (empty, just for type constraint)
- `IEventBus.cs` — interface
- `EventBus.cs` — thread-safe implementation
- `EventSubscription.cs` — disposable subscription handle
- `FrameworkEvents.cs` — all built-in framework event types

---

#### Event ownership — Option A+B combined

**Where event types live determines what references are needed:**

| Scenario | Event lives in | Reference needed |
|----------|---------------|-----------------|
| Framework → any component | `CodeLogic.Core.Events` (FrameworkEvents.cs) | None — always available |
| Lib → App | The lib (e.g. `CL.SQLite`) | App already refs the lib ✓ |
| App → Lib | The app | Lib never refs app ✓ |
| Lib → Lib (cross-lib) | `CodeLogic.Core.Events` (generic bridge events) | None needed ✓ |

**Rule:** If an event needs to cross a reference boundary (lib-to-lib, or
lib-to-unknown-consumer), use a framework bridge event. If the consumer
already references the publisher, use a typed event in the publisher.

---

#### Built-in framework events (`FrameworkEvents.cs` in Core)

These are published **by the framework itself** and by libs that need to
communicate across reference boundaries:

```csharp
// ── Lifecycle ──────────────────────────────────────────────────────
record LibraryStartedEvent(string LibraryId, string LibraryName) : IEvent;
record LibraryStoppedEvent(string LibraryId, string LibraryName) : IEvent;
record LibraryFailedEvent(string LibraryId, string LibraryName, Exception Error) : IEvent;
record PluginLoadedEvent(string PluginId, string PluginName) : IEvent;
record PluginUnloadedEvent(string PluginId, string PluginName) : IEvent;
record PluginFailedEvent(string PluginId, string PluginName, Exception Error) : IEvent;

// ── Config / Localization ───────────────────────────────────────────
record ConfigReloadedEvent(string ComponentId, Type ConfigType) : IEvent;
record LocalizationReloadedEvent(string ComponentId) : IEvent;

// ── Health ──────────────────────────────────────────────────────────
record HealthCheckCompletedEvent(HealthReport Report) : IEvent;

// ── Shutdown ────────────────────────────────────────────────────────
record ShutdownRequestedEvent(string Reason) : IEvent;

// ── Generic bridge — for lib-to-lib communication ───────────────────
// Libs publish this when they need to signal something to unknown consumers
// without requiring those consumers to reference them.
// Use ComponentId + AlertType to scope:  "cl.sqlite" + "connection.lost"
record ComponentAlertEvent(
    string ComponentId,    // "cl.sqlite"
    string AlertType,      // "connection.lost", "pool.exhausted", etc.
    string Message,        // human-readable
    object? Payload = null // optional structured data
) : IEvent;
```

---

#### Lib-specific events (defined in the lib)

Each lib defines its own strongly-typed events for consumers that already
reference it. Example in `CL.SQLite`:

```csharp
// CL.SQLite/Events/SQLiteEvents.cs
namespace CL.SQLite.Events;

public record ConnectionAcquiredEvent(string DatabasePath, TimeSpan WaitTime) : IEvent;
public record SlowQueryEvent(string Sql, TimeSpan Duration) : IEvent;
public record MigrationAppliedEvent(string MigrationId) : IEvent;
```

The lib publishes both its own typed event AND a framework bridge event:
```csharp
// In CL.SQLite — connection lost scenario:
context.Events.Publish(new ConnectionLostEvent(config.DatabasePath));       // typed (for app)
context.Events.Publish(new ComponentAlertEvent(                             // bridge (for any lib)
    "cl.sqlite", "connection.lost", $"Lost connection to {config.DatabasePath}"));
```

---

#### Usage patterns

```csharp
// ── App subscribing to lib events (app refs CL.SQLite) ─────────────
using CL.SQLite.Events;
var sub = context.Events.Subscribe<SlowQueryEvent>(e =>
    logger.Warning($"Slow query ({e.Duration.TotalMs}ms): {e.Sql}"));

// ── Lib subscribing to framework events (no extra reference) ────────
var sub = context.Events.Subscribe<ShutdownRequestedEvent>(e =>
    CloseConnections());

// ── Lib-to-lib via bridge event (no cross reference) ────────────────
var sub = context.Events.Subscribe<ComponentAlertEvent>(e => {
    if (e.ComponentId == "cl.sqlite" && e.AlertType == "connection.lost")
        PauseEmailQueue();
});

// ── Async subscribe ──────────────────────────────────────────────────
var sub = context.Events.SubscribeAsync<LibraryFailedEvent>(async e =>
    await notificationService.AlertAsync($"{e.LibraryName} failed: {e.Error.Message}"));

// ── Unsubscribe (IDisposable) ────────────────────────────────────────
sub.Dispose();
```

---

#### `IEventBus` interface

```csharp
public interface IEventBus
{
    // Publish (fire and don't wait for async subscribers)
    void Publish<T>(T @event) where T : IEvent;

    // Publish and wait for all async subscribers to complete
    Task PublishAsync<T>(T @event) where T : IEvent;

    // Subscribe synchronously
    IEventSubscription Subscribe<T>(Action<T> handler) where T : IEvent;

    // Subscribe asynchronously
    IEventSubscription SubscribeAsync<T>(Func<T, Task> handler) where T : IEvent;
}

public interface IEventSubscription : IDisposable { }
```

---

### 1.6 Utilities (`src/Core/Utilities/`)

**Files:**
- `SemanticVersion.cs` — parse/compare semver
- `StartupValidator.cs` — validates directory structure
- `FirstRunManager.cs` — first-run detection + scaffolding
- `CliArgParser.cs` — NEW: parses `Environment.GetCommandLineArgs()`

**`CliArgParser` handles:**
```
--generate-configs                  → GenerateConfigs = true
--generate-configs-force            → GenerateConfigsForce = true
--generate-configs CL.SQLite        → GenerateConfigsFor = ["cl.sqlite"]
--generate-configs-force CL.SQLite  → scoped force
--dry-run                           → DryRun = true, exit after
--version                           → print versions, exit
--info                              → print runtime info, exit
--health                            → run health checks, print report, exit
```

---

### 1.7 `CodeLogicEnvironment` (`src/`)

```csharp
public static class CodeLogicEnvironment
{
    public static string MachineName  => Environment.MachineName;
    public static string AppRootPath  => AppContext.BaseDirectory;
    public static string AppVersion   { get; internal set; } = "0.0.0";
    public static bool   IsDebugging  => Debugger.IsAttached;
}
```

---

## Phase 2 — Framework/Libraries

**Files:**
- `ILibrary.cs`
- `LibraryContext.cs` — adds `IEventBus Events`
- `LibraryManager.cs` — adds health check scheduling
- `LibraryManifest.cs`
- `LibraryDependency.cs`
- `LoadedLibrary.cs`
- `LibraryState.cs`
- `HealthReport.cs` — aggregated health result

**Health check scheduling in LibraryManager:**
```csharp
// Configured in CodeLogic.json:
// "healthChecks": { "enabled": true, "intervalSeconds": 30 }
//
// LibraryManager runs checks on schedule, publishes HealthCheckCompletedEvent
// Libraries/plugins do NOT need to manage their own health timers
// (removes the async timer bug from CL.MySQL2)
```

---

## Phase 3 — Framework/Application

**Files** (all in `src/Framework/Application/`):
- `IApplication.cs`
- `ApplicationContext.cs` — adds `IEventBus Events`
- `ApplicationManifest.cs`

Plugins are a sub-concern of Application and live in `src/Framework/Application/Plugins/` — see Phase 4.

---

## Phase 4 — Framework/Application/Plugins (UPGRADED TO PARITY)

Plugins live under Application because they are **app-managed** — the consuming
application decides what plugins to load, when to load them, and manages their
lifecycle. The framework provides the infrastructure (PluginManager, PluginContext)
but plugins are not framework-managed the way libraries are.

**4-phase lifecycle matching ILibrary exactly:**
```csharp
public interface IPlugin : IDisposable
{
    PluginManifest Manifest { get; }
    PluginState State { get; }

    Task OnConfigureAsync(PluginContext context);
    Task OnInitializeAsync(PluginContext context);
    Task OnStartAsync(PluginContext context);
    Task OnUnloadAsync();

    Task<HealthStatus> HealthCheckAsync();
}
```

**PluginContext — full parity with LibraryContext:**
```csharp
public class PluginContext
{
    public required string PluginId { get; init; }
    public required string PluginDirectory { get; init; }
    public required string ConfigDirectory { get; init; }
    public required string LocalizationDirectory { get; init; }
    public required string LogsDirectory { get; init; }
    public required string DataDirectory { get; init; }

    public required ILogger Logger { get; init; }
    public required IConfigurationManager Configuration { get; init; }
    public required ILocalizationManager Localization { get; init; }
    public required IEventBus Events { get; init; }
}
```

**Files** (all in `src/Framework/Application/Plugins/`):
- `IPlugin.cs`
- `PluginContext.cs`
- `PluginManifest.cs`
- `PluginManager.cs`
- `PluginLoadContext.cs`
- `LoadedPlugin.cs`
- `PluginState.cs`
- `PluginOptions.cs`

---

## Phase 5 — Orchestrator

### 5.1 `CodeLogicOptions`

```csharp
public class CodeLogicOptions
{
    // === Paths ===
    public string FrameworkRootPath   { get; set; } = "CodeLogic";
    public string? ApplicationRootPath { get; set; } = null;  // defaults to FrameworkRootPath/Application

    // === App Identity ===
    public string AppVersion { get; set; } = "0.0.0";

    // === Config Generation ===
    public bool GenerateConfigs      { get; set; } = true;
    public bool GenerateConfigsForce { get; set; } = false;
    public string[]? GenerateConfigsFor { get; set; } = null;  // null = all
    public bool ExitAfterGenerate    { get; set; } = false;

    // === Shutdown ===
    public bool HandleShutdownSignals { get; set; } = true;  // hooks SIGTERM/CTRL+C

    // === Health Checks ===
    // (detailed config in CodeLogic.json healthChecks section)

    // === Path helpers ===
    public string GetFrameworkPath()
    public string GetLibrariesPath()
    public string GetLibraryPath(string id)
    public string GetApplicationPath()
    public string GetApplicationConfigPath()
    public string GetApplicationLocalizationPath()
    public string GetApplicationLogsPath()
    public string GetApplicationDataPath()
    public string GetPluginsPath()
    public string GetCodeLogicConfigPath()
}
```

### 5.2 `CodeLogicConfiguration` (CodeLogic.json)

```json
{
  "framework": {
    "name": "CodeLogic",
    "version": "3.0.0"
  },
  "logging": {
    "mode": "singleFile",
    "maxFileSizeMb": 10,
    "maxRolledFiles": 5,
    "globalLevel": "Warning",
    "enableConsoleOutput": false,
    "consoleMinimumLevel": "Debug",
    "enableDebugMode": false,
    "centralizedDebugLog": false,
    "includeMachineName": true,
    "timestampFormat": "yyyy-MM-dd HH:mm:ss.fff"
  },
  "localization": {
    "defaultCulture": "en-US",
    "supportedCultures": ["en-US"],
    "autoGenerateTemplates": true
  },
  "libraries": {
    "discoveryPattern": "CL.*",
    "enableDependencyResolution": true
  },
  "healthChecks": {
    "enabled": true,
    "intervalSeconds": 30
  }
}
```

### 5.3 Startup Flow

```
1. Parse Environment.GetCommandLineArgs() via CliArgParser
   → detect all supported flags + scoping

2. Merge CLI args into options (CLI always wins)

3. Resolve FrameworkRootPath + ApplicationRootPath

4. Check Debugger.IsAttached
   → set debug-aware logging defaults

5. First-run check
   → scaffold directory structure if .codelogic marker missing
   → generate CodeLogic.json with debug-aware defaults
   → do NOT exit — continue startup

6. Load CodeLogic.json

7. Handle special CLI modes (exit after):
   --version  → print framework + app version, exit
   --info     → print runtime info (paths, machine, libs loaded), exit
   --dry-run  → print what would be generated, exit
   --health   → run all health checks, print HealthReport, exit

8. If GenerateConfigs:
   → generate missing (or force-overwrite) configs for scoped/all libs

9. If ExitAfterGenerate: exit

10. Normal startup:
    RegisterApplication() → ConfigureAsync() → StartAsync()

11. If HandleShutdownSignals:
    → hook Console.CancelKeyPress + AppDomain.ProcessExit
    → call StopAsync() on SIGTERM/CTRL+C
```

### 5.4 `ICodeLogicRuntime`

```csharp
public interface ICodeLogicRuntime
{
    Task<InitializationResult> InitializeAsync(Action<CodeLogicOptions>? configure = null);
    void RegisterApplication(IApplication application);
    Task ConfigureAsync();
    Task StartAsync();
    Task StopAsync();
    Task ResetAsync();

    Task<HealthReport> GetHealthStatusAsync();
    LibraryManager?    GetLibraryManager();
    PluginManager?     GetPluginManager();
    IApplication?      GetApplication();
    ApplicationContext? GetApplicationContext();
    IEventBus          GetEventBus();
    CodeLogicOptions   GetOptions();
    CodeLogicConfiguration GetConfiguration();
}
```

### 5.5 `HealthReport`

```csharp
public class HealthReport
{
    public bool IsHealthy { get; }                              // all healthy
    public DateTime CheckedAt { get; }
    public string MachineName { get; }
    public string AppVersion { get; }
    public Dictionary<string, HealthStatus> Libraries { get; } // id → status
    public Dictionary<string, HealthStatus> Plugins { get; }   // id → status
    public HealthStatus? Application { get; }

    // Formatted for --health CLI output:
    public string ToConsoleString();

    // Formatted for a health endpoint response:
    public string ToJson();
}
```

### 5.6 `CodeLogic.cs` (static facade)

```csharp
public static class CodeLogic
{
    private static readonly ICodeLogicRuntime _runtime = new CodeLogicRuntime();

    public static Task<InitializationResult> InitializeAsync(
        Action<CodeLogicOptions>? configure = null) => _runtime.InitializeAsync(configure);
    public static void RegisterApplication(IApplication app) => _runtime.RegisterApplication(app);
    public static Task ConfigureAsync()  => _runtime.ConfigureAsync();
    public static Task StartAsync()      => _runtime.StartAsync();
    public static Task StopAsync()       => _runtime.StopAsync();
    public static Task ResetAsync()      => _runtime.ResetAsync();
    public static Task<HealthReport> GetHealthStatusAsync() => _runtime.GetHealthStatusAsync();
    public static LibraryManager?    GetLibraryManager()    => _runtime.GetLibraryManager();
    public static PluginManager?     GetPluginManager()     => _runtime.GetPluginManager();
    public static IApplication?      GetApplication()       => _runtime.GetApplication();
    public static ApplicationContext? GetApplicationContext() => _runtime.GetApplicationContext();
    public static IEventBus          GetEventBus()          => _runtime.GetEventBus();
}
```

### 5.7 `Libraries.cs` and `Plugins.cs`

Both unchanged API — just delegate to runtime.

---

## Phase 6 — Project File + Solution

### `CodeLogic/src/CodeLogic.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>CodeLogic</RootNamespace>
    <AssemblyName>CodeLogic</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>13</LangVersion>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <Version>3.0.0</Version>
    <Authors>Zyntal.com</Authors>
    <Description>
      Modular .NET 10 framework with lifecycle-managed libraries,
      applications, and plugins. Zero external dependencies.
    </Description>
  </PropertyGroup>
</Project>
```

**Zero NuGet dependencies in this project.**

### `CodeLogic.sln`

```
CodeLogic/src/CodeLogic.csproj
CodeLogic.Libs/CL.Core/CL.Core.csproj
CodeLogic.Libs/CL.SQLite/CL.SQLite.csproj
... (all libs)
```

---

## Phase 7 — Libs (Port from CodeLogic3.Libs)

Copy each lib and update:

| Change | Old | New |
|--------|-----|-----|
| Project ref | `CodeLogic3/src/CodeLogic.csproj` | `../../CodeLogic/src/CodeLogic.csproj` |
| ILogger namespace | `CodeLogic.Logging` | `CodeLogic.Core.Logging` |
| Config namespace | `CodeLogic.Configuration` | `CodeLogic.Core.Configuration` |
| Localization namespace | `CodeLogic.Localization` | `CodeLogic.Core.Localization` |
| ILibrary namespace | `CodeLogic.Abstractions` | `CodeLogic.Framework.Libraries` |
| LibraryContext | `CodeLogic.Models` | `CodeLogic.Framework.Libraries` |
| Result types | Per-lib own version | `CodeLogic.Core.Results` |
| Health check timer | Each lib manages own | Removed — framework handles scheduling |

---

## Phase 8 — Documentation

### `CodeLogic/docs/Core/`
- `Results.md`
- `Logging.md`
- `Configuration.md`
- `Localization.md`
- `Events.md`

### `CodeLogic/docs/Framework/`
- `Libraries.md`
- `Application.md`
- `Plugins.md`
- `Startup.md` — boot sequence, CLI args, config generation
- `HealthChecks.md`

### `CodeLogic.Libs/docs/`
One `.md` per lib.

---

## Complete Feature Delta vs Old Framework

| Feature | Old | New |
|---------|-----|-----|
| Core standalone | No | Yes — zero framework coupling |
| Static + injectable | Static only | Static facade + `ICodeLogicRuntime` |
| Result types | Per-lib, inconsistent | `Result<T>` / `Error` in Core |
| Event bus | No | `IEventBus` in Core, all contexts |
| `CodeLogicEnvironment` | No | `MachineName`, `AppVersion`, `AppRootPath`, `IsDebugging` |
| Debug-aware log defaults | No | `Debugger.IsAttached` auto-configures |
| Log mode | Date folders only | **SingleFile (default)** + date folder opt-in |
| Log rolling | No | By size, configurable count + kept files |
| Machine name in logs | No | Yes (configurable) |
| Separate app/framework paths | No | `FrameworkRootPath` + `ApplicationRootPath` |
| CLI: `--generate-configs` | No | Yes, with scoping |
| CLI: `--generate-configs-force` | No | Yes, with scoping |
| CLI: `--dry-run` | No | Yes |
| CLI: `--version` | No | Yes |
| CLI: `--info` | No | Yes |
| CLI: `--health` | No | Yes — runs checks, prints HealthReport |
| `ExitAfterGenerate` | Always exited | Defaults false — keeps running |
| Config reload | No | Opt-in per lib, safe values only |
| Localization reload | No | Always safe, fire and forget |
| Config reload events | No | `ConfigReloadedEvent` via EventBus |
| Graceful shutdown signals | No | `HandleShutdownSignals = true` (default) |
| Health check scheduling | Per-lib timers | Framework-managed, configurable interval |
| `HealthReport` | Dict only | Typed report with ToJson/ToConsoleString |
| `--health` CLI | No | Runs checks, prints report, exits |
| Plugin config + localization | No | Full `ConfigurationManager` + `LocalizationManager` |
| Plugin lifecycle phases | 2 | 4 — parity with libraries |
| Plugin state tracking | Dead code | `PluginState` enum |
| `Plugins` static accessor | No | Yes — mirrors `Libraries` |
| NullLogger | CL.SQLite only | Core — available everywhere |
| Thread safety | Fixed in v2 | Preserved |
| LibraryState tracking | Fixed in v2 | Preserved |
| `ResetAsync()` | Fixed in v2 | Preserved |
| Zero NuGet deps (framework) | No | Yes |

---

## Build Order

1. `src/Core/Results/`
2. `src/Core/Logging/`
3. `src/Core/Configuration/`
4. `src/Core/Localization/`
5. `src/Core/Events/`
6. `src/Core/Utilities/` (incl. CliArgParser)
7. `src/Framework/Libraries/` (incl. HealthReport)
8. `src/Framework/Application/`
9. `src/Framework/Plugins/`
10. `src/CodeLogicOptions.cs` + `src/CodeLogicConfiguration.cs`
11. `src/CodeLogicEnvironment.cs`
12. `src/ICodeLogicRuntime.cs`
13. `src/CodeLogicRuntime.cs`
14. `src/CodeLogic.cs` + `src/Libraries.cs` + `src/Plugins.cs`
15. `src/CodeLogic.csproj` + `CodeLogic.sln`
16. Libs port (all 11)
17. `docs/`
18. `samples/` (separate repo later)
