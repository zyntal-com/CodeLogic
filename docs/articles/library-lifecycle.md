# Library Lifecycle

Every CodeLogic library and plugin implements the same 4-phase lifecycle. The framework calls these phases in a deterministic order across all registered libraries, ensuring dependencies are always fully ready before their consumers start.

---

## The 4 Phases

| Phase | Method | Called during | Purpose |
|-------|--------|---------------|---------|
| 1 — Configure | `OnConfigureAsync` | `CodeLogic.ConfigureAsync()` | Register config and localization models |
| 2 — Initialize | `OnInitializeAsync` | `CodeLogic.StartAsync()`, first pass | Read config, open connections, set up services |
| 3 — Start | `OnStartAsync` | `CodeLogic.StartAsync()`, second pass | Start background tasks, begin accepting work |
| 4 — Stop | `OnStopAsync` | `CodeLogic.StopAsync()` | Graceful shutdown (called in reverse start order) |

---

## Phase 1 — Configure

`OnConfigureAsync` is called for all libraries before any config file is loaded. Its only job is to register which config and localization models the library needs.

```csharp
public Task OnConfigureAsync(LibraryContext context)
{
    // Register config models
    context.Configuration.Register<MyConfig>();
    context.Configuration.Register<AdvancedConfig>();

    // Register localization models
    context.Localization.Register<MyStrings>();

    return Task.CompletedTask;
}
```

**Critical rule:** Do NOT read config values in this phase. Config files are generated/loaded *after* all `OnConfigureAsync` calls complete.

---

## Phase 2 — Initialize

`OnInitializeAsync` is called after all libraries have configured and all config files are loaded. Dependencies are initialized before their dependants.

```csharp
private MyConfig _config = null!;
private ILogger _logger = null!;
private DbConnection _db = null!;

public async Task OnInitializeAsync(LibraryContext context)
{
    _logger = context.Logger;
    _config = context.Configuration.Get<MyConfig>();

    // Validate config
    var validation = _config.Validate();
    if (!validation.IsValid)
        throw new InvalidOperationException($"Config invalid: {string.Join(", ", validation.Errors)}");

    // Open connection
    _db = new DbConnection(_config.ConnectionString);
    await _db.OpenAsync();

    _logger.LogInformation("Initialized with connection pool size {Size}", _config.PoolSize);
}
```

---

## Phase 3 — Start

`OnStartAsync` is called after all libraries have initialized. This is where you start background tasks and begin accepting work.

```csharp
private CancellationTokenSource _cts = new();
private Task _backgroundTask = Task.CompletedTask;

public Task OnStartAsync(LibraryContext context)
{
    _backgroundTask = RunBackgroundAsync(_cts.Token);
    context.Logger.LogInformation("Library started");
    return Task.CompletedTask;
}

private async Task RunBackgroundAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await DoWorkAsync();
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }
}
```

---

## Phase 4 — Stop

`OnStopAsync` is called in **reverse start order** (last started, first stopped). Stop background tasks, flush pending work, and close connections.

```csharp
public async Task OnStopAsync()
{
    // Signal background task to stop
    _cts.Cancel();

    // Wait for it to finish
    await _backgroundTask.ConfigureAwait(false);

    // Close database connection
    await _db.CloseAsync();
    _db.Dispose();
}
```

---

## Live config changes

Optional: a library that also implements `IConfigReloadable` gets `OnConfigChangedAsync` after its config changes at runtime (admin UI, reload, file watcher), and can apply it without a restart — for example reconnecting only the one database whose settings changed. See [Configuration › Hot Reload](configuration.md#hot-reload).

---

## LibraryContext

The same `LibraryContext` instance is passed to all four phases. Store it in a field if you need it during `OnStopAsync`:

```csharp
public sealed class LibraryContext
{
    public string LibraryId          { get; }
    public string LibraryDirectory   { get; }   // {FrameworkRoot}/Libraries/{Id}/
    public string ConfigDirectory    { get; }
    public string LocalizationDirectory { get; }
    public string LogsDirectory      { get; }
    public string DataDirectory      { get; }

    public ILogger Logger                        { get; }
    public IConfigurationManager Configuration   { get; }
    public ILocalizationManager Localization     { get; }
    public IEventBus Events                      { get; }
}
```

---

## LibraryManifest

The manifest describes the library's identity and requirements:

```csharp
public LibraryManifest Manifest => new()
{
    Id          = "MyApp.Database",          // determines directory name
    Name        = "Database Library",        // shown in console output
    Version     = "1.0.0",
    Description = "Manages database access",
    Author      = "Media2A",
    Dependencies = [
        new LibraryDependency("MyApp.Config", "1.0.0")
    ],
    Tags = ["database", "infrastructure"]
};
```

### Dependency Resolution

Dependencies are resolved by ID and minimum version. The framework initializes libraries in dependency order — if library B depends on library A, A's `OnInitializeAsync` and `OnStartAsync` will complete before B's.

---

## Ordering Summary

Given libraries A, B (depends on A), and C (depends on B):

```
ConfigureAsync():
  A.OnConfigureAsync()
  B.OnConfigureAsync()
  C.OnConfigureAsync()

StartAsync() - Initialize pass (dependency order):
  A.OnInitializeAsync()
  B.OnInitializeAsync()
  C.OnInitializeAsync()

StartAsync() - Start pass:
  A.OnStartAsync()
  B.OnStartAsync()
  C.OnStartAsync()

StopAsync() - reverse start order:
  C.OnStopAsync()
  B.OnStopAsync()
  A.OnStopAsync()
```

---

## IDisposable

`ILibrary` extends `IDisposable`. The framework calls `Dispose()` after `OnStopAsync`. Use it for any final synchronous cleanup:

```csharp
public void Dispose()
{
    _cts.Dispose();
    _db?.Dispose();
}
```
