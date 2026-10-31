# Plugins

Plugins are hot-reloadable components that **your application owns and manages** via `PluginManager`. They are not part of the framework's built-in startup: you create a `PluginManager` in your `IApplication`, register it with `CodeLogic.SetPluginManager()`, and load plugins yourself during `OnStartAsync`.

Unlike libraries (which are fully started before your application runs), plugins are loaded and managed entirely within the application layer. The framework participates only in health reporting and shutdown: `StopAsync()` unloads plugins before libraries stop, and `ResetAsync()` disposes the registered manager and forgets it.

```
Libraries (framework-managed, start first)
    ↓ fully started
IApplication.OnInitializeAsync / OnStartAsync
    → creates PluginManager
    → CodeLogic.SetPluginManager(manager)
    → manager.LoadAllAsync()
        ↓
      Plugins running (app-managed)
```

---

## IPlugin Interface

```csharp
public interface IPlugin : IDisposable
{
    PluginManifest Manifest { get; }
    PluginState State       { get; }

    Task OnConfigureAsync(PluginContext context);
    Task OnInitializeAsync(PluginContext context);
    Task OnStartAsync(PluginContext context);
    Task OnUnloadAsync();

    Task<HealthStatus> HealthCheckAsync();
}
```

The 4-phase lifecycle mirrors `ILibrary`. The stop phase is called `OnUnloadAsync` instead of `OnStopAsync`. `Dispose` is the guaranteed cleanup hook: it runs after every unload and after every failed load, so make it safe to call on a partly initialized instance.

---

## PluginManifest

```csharp
public sealed class PluginManifest
{
    public required string Id      { get; init; }   // "myapp.dashboard" — unique per manager
    public required string Name    { get; init; }   // "Dashboard Plugin"
    public required string Version { get; init; }   // "1.0.0"
    public string? Description         { get; init; }
    public string? Author              { get; init; }
    public string? MinFrameworkVersion { get; init; }   // informational
}
```

---

## PluginContext

Full parity with `LibraryContext`. Scoped to the plugin's directory:

```csharp
public sealed class PluginContext
{
    public string PluginId               { get; }
    public string PluginDirectory        { get; }   // the folder that holds the plugin assembly
    public string ConfigDirectory        { get; }   // same as PluginDirectory
    public string LocalizationDirectory  { get; }   // {PluginDirectory}/localization
    public string LogsDirectory          { get; }   // {PluginDirectory}/logs
    public string DataDirectory          { get; }   // {PluginDirectory}/data

    public ILogger Logger                { get; }
    public IConfigurationManager Configuration { get; }
    public ILocalizationManager Localization   { get; }
    public IEventBus Events              { get; }   // the shared framework bus
}
```

---

## PluginState

```csharp
public enum PluginState
{
    Discovered,   // found on disk, not loaded
    Loaded,       // assembly loaded, instance created
    Configured,   // OnConfigureAsync done, config and localization loaded
    Initialized,  // OnInitializeAsync done
    Started,      // OnStartAsync done — operational
    Stopped,      // unloaded cleanly
    Failed        // a lifecycle phase threw
}
```

---

## PluginManager

Your application creates and owns the `PluginManager`. Register it with `CodeLogic.SetPluginManager()` so the framework includes plugins in health checks and shuts them down before libraries stop:

```csharp
public sealed class MyApp : IApplication
{
    // Phase 2: create and register the PluginManager
    public Task OnInitializeAsync(ApplicationContext context)
    {
        var manager = new PluginManager(
            context.Events,
            new PluginOptions
            {
                PluginsDirectory = CodeLogic.GetOptions().GetPluginsPath(),
                WatchForChanges  = true,   // hot reload when a plugin DLL changes
            });

        // Register with the framework — participates in health + shutdown
        CodeLogic.SetPluginManager(manager);

        return Task.CompletedTask;
    }

    // Phase 3: load plugins once the app is starting
    public async Task OnStartAsync(ApplicationContext context)
    {
        var manager = CodeLogic.GetPluginManager()!;
        await manager.LoadAllAsync();
    }

    // ...OnStopAsync, etc.
}
```

Direct plugin operations. Loading takes the path of the plugin assembly; the other operations take the manifest ID:

```csharp
var manager = CodeLogic.GetPluginManager()!;
var path = Path.Combine(CodeLogic.GetOptions().GetPluginsPath(),
    "MyApp.Dashboard.Plugin", "MyApp.Dashboard.Plugin.dll");

await manager.LoadPluginAsync(path);                     // load and start one plugin
await manager.UnloadPluginAsync("myapp.dashboard");      // unload one plugin
await manager.ReloadPluginAsync("myapp.dashboard");      // unload + load from the same path
await manager.UnloadAllAsync();                          // unload everything
```

### Concurrency and failures

- Load, unload, reload, register, dispose and hot reload are **serialized**: one lifecycle operation runs at a time. `GetPlugin`, `GetAllPlugins`, `GetLoadedPlugins` and `GetHealthAsync` never block and work on a snapshot.
- Plugin IDs are **unique**. Loading a plugin whose ID is already loaded throws `InvalidOperationException` before the new instance is configured or started; the running plugin is untouched. Use `ReloadPluginAsync` to replace it.
- A **failed load** cleans up: a plugin whose `OnStartAsync` was entered gets `OnUnloadAsync`, the instance is disposed, and its load context is unloaded. The exception is rethrown and also reported through `PluginFailedEvent` and `OnPluginError`.
- A **failing `OnUnloadAsync`** doesn't stop the cleanup: the plugin is still disposed, removed and its context unloaded. The error is reported through `PluginFailedEvent` and `OnPluginError`, not thrown.
- Exceptions from `OnPluginLoaded` / `OnPluginUnloaded` handlers are not plugin failures; they are written to `Console.Error` and ignored.
- Don't call `PluginManager` lifecycle methods from inside a plugin's own lifecycle callbacks; that throws `InvalidOperationException` instead of deadlocking.

---

## Hot Reload with FileSystemWatcher

With `WatchForChanges = true` (and `EnableHotReload`, which is on by default), `PluginManager` watches the plugins directory and reloads a loaded plugin when its DLL changes:

```csharp
var options = new PluginOptions
{
    PluginsDirectory = CodeLogic.GetOptions().GetPluginsPath(),
    EnableHotReload  = true,
    WatchForChanges  = true,
    ReloadDebounce   = TimeSpan.FromMilliseconds(500),
};
```

When a plugin's assembly is replaced on disk, `PluginManager`:

1. Calls `OnUnloadAsync()` and `Dispose()` on the running plugin
2. Unloads its `AssemblyLoadContext`
3. Loads the new assembly into a fresh context
4. Calls `OnConfigureAsync`, `OnInitializeAsync`, `OnStartAsync` on the new instance

---

## Writing a Plugin

```csharp
public sealed class DashboardPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id      = "myapp.dashboard",
        Name    = "Dashboard Plugin",
        Version = "1.0.0"
    };

    public PluginState State { get; private set; } = PluginState.Loaded;

    private DashboardConfig _config = null!;
    private IEventSubscription? _sub;

    public Task OnConfigureAsync(PluginContext context)
    {
        context.Configuration.Register<DashboardConfig>();
        State = PluginState.Configured;
        return Task.CompletedTask;
    }

    public Task OnInitializeAsync(PluginContext context)
    {
        _config = context.Configuration.Get<DashboardConfig>();
        State = PluginState.Initialized;
        return Task.CompletedTask;
    }

    public Task OnStartAsync(PluginContext context)
    {
        _sub = context.Events.Subscribe<DeviceStateChangedEvent>(OnDeviceState);
        State = PluginState.Started;
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync()
    {
        _sub?.Dispose();
        _sub = null;
        State = PluginState.Stopped;
        return Task.CompletedTask;
    }

    public Task<HealthStatus> HealthCheckAsync()
        => Task.FromResult(HealthStatus.Healthy("Dashboard running"));

    public void Dispose() => _sub?.Dispose();

    private void OnDeviceState(DeviceStateChangedEvent e)
    {
        // update dashboard state
    }
}
```

---

## Plugin Directory Layout

`LoadAllAsync` discovers plugins in subdirectories named `*.Plugin`, each containing an assembly with the same name:

```
CodeLogic/
  Plugins/
    MyApp.Dashboard.Plugin/
      MyApp.Dashboard.Plugin.dll        ← plugin assembly
      MyApp.Dashboard.Plugin.deps.json  ← its private dependencies
      config.json                       ← plugin config (generated)
      localization/
      logs/
      data/
```

The plugin assembly must contain a class implementing `IPlugin`. `PluginManager` finds it by scanning the assembly's types. `LoadPluginAsync` accepts any path; the naming convention only matters for discovery.

---

## Isolation Model

Each plugin loads into its own collectible `PluginLoadContext`. The isolation is about **assembly resolution**, nothing more:

- **Host contracts are shared.** `CodeLogic` always resolves to the host's copy, so `IPlugin`, `PluginContext`, `IEventBus` and the framework events have one identity on both sides. Add other assemblies whose types cross the boundary (for example a library's interfaces) to `PluginOptions.SharedAssemblies`; they also resolve to the host's copy, whatever version the plugin ships.
- **Plugin dependencies are private.** Anything listed in the plugin's `.deps.json` or present in its folder loads into the plugin's own context, so two plugins can use different versions of the same package. A plugin never receives an assembly from another plugin's context.
- **Everything else comes from the host.** The .NET runtime and assemblies the plugin doesn't ship fall back to the default context.
- **Plugin types are not host types.** The host can only see a plugin through shared types (`IPlugin`, or a shared contract interface). `GetPlugin<T>` with a type defined in the plugin assembly only works for in-process plugins registered with `RegisterInProcessPluginAsync`.
- **It is not a sandbox.** Plugins run in the host process with its permissions, share the event bus and the statics of shared assemblies, and can still keep their context alive (for example with an event subscription that `OnUnloadAsync` doesn't dispose), which prevents the assembly from being collected.

Hot reload works by discarding the old context and creating a fresh one.
