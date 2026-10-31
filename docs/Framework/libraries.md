# Libraries

A CodeLogic library is a self-contained, reusable service component. Libraries are loaded before the application starts, and stopped after the application stops.

---

## ILibrary Interface

```csharp
public interface ILibrary : IDisposable
{
    LibraryManifest Manifest { get; }

    Task OnConfigureAsync(LibraryContext context);
    Task OnInitializeAsync(LibraryContext context);
    Task OnStartAsync(LibraryContext context);
    Task OnStopAsync();

    Task<HealthStatus> HealthCheckAsync();
}
```

### The 4 Lifecycle Phases

| Phase | Method | When called | What to do |
|-------|--------|-------------|-----------|
| 1 | `OnConfigureAsync` | During `ConfigureAsync()` | Register config and localization models. Do NOT read config. |
| 2 | `OnInitializeAsync` | During `StartAsync()`, after all configs loaded | Set up services, validate config, open connections. |
| 3 | `OnStartAsync` | During `StartAsync()`, after all libraries initialized | Start background tasks, begin processing. |
| 4 | `OnStopAsync` | During `StopAsync()`, in reverse start order | Stop tasks, close connections, flush, release. |

**Key rule:** Do not read config in `OnConfigureAsync`. Config is loaded *after* this method returns. Read it in `OnInitializeAsync`.

### Live config changes (optional)

Implement `IConfigReloadable` to apply config changes while running, instead of on the next restart:

```csharp
public interface IConfigReloadable
{
    Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken ct);
}
```

It is called after a section changes (admin UI update, reload from disk, reset, file watcher, or the library's own `SaveAsync`), once the library is initialized. `change.GetOld<T>()` / `change.GetNew<T>()` give before/after snapshots; `ConfigDiff.Keyed(...)` tells you which named instances were added, removed, changed, or untouched, so you rebuild only those. Throwing reports the change as *saved but not applied* and the library is reported Degraded until a later change applies (`LoadedLibrary.ConfigApplyError`).

Libraries that don't implement it still get changes persisted; they take effect on restart (a warning is logged when fields marked `RequiresRestart` changed). Full pattern: [Configuration › Live reload for libraries](../Core/configuration.md#live-reload-for-libraries).

---

## LibraryContext

Provided to each library at every lifecycle phase. Scoped to that specific library.

```csharp
public sealed class LibraryContext
{
    string LibraryId          { get; }   // "CL.Data"
    string LibraryDirectory   { get; }   // {FrameworkRoot}/Libraries/CL.Data/
    string ConfigDirectory    { get; }   // same as LibraryDirectory by default
    string LocalizationDirectory { get; } // {LibraryDirectory}/localization/
    string LogsDirectory      { get; }   // {LibraryDirectory}/logs/
    string DataDirectory      { get; }   // {LibraryDirectory}/data/

    ILogger Logger            { get; }   // scoped to this library
    IConfigurationManager Configuration { get; }
    ILocalizationManager Localization  { get; }
    IEventBus Events          { get; }   // shared instance
}
```

The same context instance is passed to all phases — store it in a field if you need it during `OnStopAsync`.

---

## LibraryManifest

Describes a library's identity and requirements:

```csharp
public sealed class LibraryManifest
{
    string Id { get; init; }                      // "CL.Data" — determines directory name
    string Name { get; init; }                    // "Data Library" — shown in console
    string Version { get; init; }                 // "1.0.0" — checked against dependencies
    string? Description { get; init; }
    string? Author { get; init; }
    LibraryDependency[] Dependencies { get; init; } // dependency declarations
    string[] Tags { get; init; }
}
```

### ID Naming Convention

Library IDs follow the `CL.Name` convention:

```
CL.Data
CL.Mail
CL.ZWave
CL.Dashboard
```

The ID is used as the library's directory name under `Libraries/`. The `CL.` prefix is normalized automatically (case-insensitive).

---

## LibraryDependency

Declares a dependency on another library:

```csharp
public sealed record LibraryDependency
{
    string Id { get; init; }         // "CL.Data"
    string? MinVersion { get; init; } // "5.0.0" or null (any version)
    bool IsOptional { get; init; }   // false = required, true = optional

    // Factory methods (preferred over constructors)
    static LibraryDependency Required(string id);
    static LibraryDependency Required(string id, string minVersion);
    static LibraryDependency Optional(string id);
    static LibraryDependency Optional(string id, string minVersion);
}
```

```csharp
public LibraryManifest Manifest => new()
{
    Id   = "CL.Mail",
    Name = "Mail Library",
    Version = "1.0.0",
    Dependencies = [
        LibraryDependency.Required("CL.Data", "5.0.0"),  // must exist, >= 5.0.0
        LibraryDependency.Optional("CL.Template"),          // nice to have
    ]
};
```

---

## LibraryDependencyAttribute

Alternative to declaring dependencies in the manifest — use attributes on the class:

```csharp
[LibraryDependency(Id = "CL.Data", MinVersion = "5.0.0")]
[LibraryDependency(Id = "CL.Template", IsOptional = true)]
public class MailLibrary : ILibrary
{
    public LibraryManifest Manifest => new()
    {
        Id   = "CL.Mail",
        Name = "Mail Library",
        Version = "1.0.0"
        // No Dependencies array — declared via attributes above
    };
}
```

---

## LibraryState

Tracks the current lifecycle state of a library:

```csharp
public enum LibraryState
{
    Loaded,       // Registered but not yet configured
    Configured,   // OnConfigureAsync done, config loaded
    Initialized,  // OnInitializeAsync done
    Started,      // OnStartAsync done — fully operational
    Stopped,      // OnStopAsync done
    Failed        // Exception during any phase
}
```

---

## Dependency Resolution

When `EnableDependencyResolution = true` (default), the `LibraryManager` performs a topological sort before starting libraries:

1. Builds a dependency graph from `LibraryManifest.Dependencies`
2. Validates that all required dependencies are registered (throws if missing)
3. Validates version constraints (throws if version is too old)
4. Starts libraries in dependency order (dependencies first)
5. Stops libraries in reverse order (dependencies last)

```
CL.Data → CL.Mail → CL.Notifications
Start order:  Data, Mail, Notifications
Stop order:   Notifications, Mail, Data
```

Circular dependencies are detected and throw an `InvalidOperationException`.

---

## HealthStatus

Returned by `HealthCheckAsync()`:

```csharp
public sealed class HealthStatus
{
    HealthStatusLevel Status { get; init; }  // Healthy, Degraded, Unhealthy
    string Message { get; init; }
    Dictionary<string, object>? Data { get; init; }
    DateTime CheckedAt { get; init; }

    bool IsHealthy   => Status == HealthStatusLevel.Healthy;
    bool IsDegraded  => Status == HealthStatusLevel.Degraded;
    bool IsUnhealthy => Status == HealthStatusLevel.Unhealthy;

    static HealthStatus Healthy(string message = "Healthy");
    static HealthStatus Degraded(string message);
    static HealthStatus Unhealthy(string message);
    static HealthStatus FromException(Exception ex);   // → Unhealthy
}
```

### Status levels

| Level | Meaning |
|-------|---------|
| `Healthy` | Component is fully operational |
| `Degraded` | Component is running but with reduced capability (e.g., high latency, partial failure) |
| `Unhealthy` | Component is not functioning — alerts should fire |

---

## How to Build a Complete Library

```csharp
using CodeLogic.Framework.Libraries;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using System.ComponentModel.DataAnnotations;

// Config model
public class DataLibraryConfig : ConfigModelBase
{
    [Required]
    public string DatabasePath { get; set; } = "data/mydb.sqlite";

    [Range(1, 100)]
    public int MaxConnections { get; set; } = 10;
}

// Library implementation
public class DataLibrary : ILibrary
{
    public LibraryManifest Manifest => new()
    {
        Id          = "CL.Data",
        Name        = "Data Library",
        Version     = "1.0.0",
        Description = "Provides database access"
    };

    private LibraryContext _context = null!;
    private DataLibraryConfig _config = null!;
    private Database? _db;

    // Phase 1: Register models (config not yet loaded)
    public Task OnConfigureAsync(LibraryContext context)
    {
        context.Configuration.Register<DataLibraryConfig>();
        return Task.CompletedTask;
    }

    // Phase 2: Config is loaded — validate and initialize
    public Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        _config = context.Configuration.Get<DataLibraryConfig>();

        context.Logger.Info($"Opening database at: {_config.DatabasePath}");
        _db = new Database(_config.DatabasePath, _config.MaxConnections);

        return Task.CompletedTask;
    }

    // Phase 3: Start — begin accepting queries
    public Task OnStartAsync(LibraryContext context)
    {
        context.Logger.Info("Data library started");
        context.Events.Publish(new ComponentAlertEvent(
            "CL.Data", "started", "Database is ready"));
        return Task.CompletedTask;
    }

    // Phase 4: Stop — close connections, flush writes
    public Task OnStopAsync()
    {
        _context.Logger.Info("Closing database");
        _db?.Dispose();
        return Task.CompletedTask;
    }

    // Health check — test the database connection
    public async Task<HealthStatus> HealthCheckAsync()
    {
        try
        {
            await _db!.PingAsync();
            return HealthStatus.Healthy($"Database at {_config.DatabasePath}");
        }
        catch (Exception ex)
        {
            return HealthStatus.FromException(ex);
        }
    }

    public void Dispose()
    {
        _db?.Dispose();
    }
}
```

### Registering the library

```csharp
// In Program.cs, after InitializeAsync:
await Libraries.LoadAsync<DataLibrary>();
```

### Accessing the library from the application

```csharp
// In MyApp.OnInitializeAsync:
var data = Libraries.Get<DataLibrary>()
    ?? throw new InvalidOperationException("Data library not loaded");

// Or by ID:
var data = Libraries.Get("CL.Data");
```
