# Plugins

Plugins are hot-loadable components managed by the consuming application. Unlike libraries, plugins are loaded into separate collectible `AssemblyLoadContext` instances and can be unloaded and reloaded at runtime without restarting the host process. That isolates assembly resolution, not execution: see [Isolation](#isolation).

---

## IPlugin Interface

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

The 4-phase lifecycle mirrors `ILibrary`. Note that the stop phase is called `OnUnloadAsync` instead of `OnStopAsync`.

---

## PluginContext

Full parity with `LibraryContext`. Scoped to the plugin's directory:

```csharp
public sealed class PluginContext
{
    string PluginId               { get; }   // "myapp.dashboard"
    string PluginDirectory        { get; }   // {FrameworkRoot}/Plugins/MyApp.Plugin/
    string ConfigDirectory        { get; }   // same as PluginDirectory
    string LocalizationDirectory  { get; }   // {PluginDirectory}/localization/
    string LogsDirectory          { get; }   // {PluginDirectory}/logs/
    string DataDirectory          { get; }   // {PluginDirectory}/data/

    ILogger Logger               { get; }
    IConfigurationManager Configuration { get; }
    ILocalizationManager Localization   { get; }
    IEventBus Events             { get; }   // same shared bus as libraries + app
}
```

---

## PluginManifest

```csharp
public sealed class PluginManifest
{
    string Id { get; init; }                   // "myapp.dashboard"
    string Name { get; init; }                 // "Dashboard Plugin"
    string Version { get; init; }              // "1.0.0"
    string? Description { get; init; }
    string? Author { get; init; }
    string? MinFrameworkVersion { get; init; } // "5.0.0" — informational
}
```

---

## PluginState

```csharp
public enum PluginState
{
    Discovered,   // Found on disk but not yet loaded
    Loaded,       // Assembly loaded, instance created
    Configured,   // OnConfigureAsync completed
    Initialized,  // OnInitializeAsync completed
    Started,      // OnStartAsync completed — fully operational
    Stopped,      // OnUnloadAsync completed
    Failed        // Exception during any phase
}
```

---

## PluginOptions

Configure the `PluginManager` on construction:

```csharp
public sealed class PluginOptions
{
    string PluginsDirectory { get; set; } = "CodeLogic/Plugins";
    bool EnableHotReload { get; set; } = true;
    bool WatchForChanges { get; set; } = false;      // FileSystemWatcher
    TimeSpan ReloadDebounce { get; set; } = TimeSpan.FromSeconds(1);
}
```

---

## PluginManager

The `PluginManager` is created and managed by the consuming application. Register it with the runtime via `CodeLogic.SetPluginManager(manager)` to participate in health checks and graceful shutdown.

```csharp
public sealed class PluginManager : IAsyncDisposable
{
    // Events
    event Action<string>? OnPluginLoaded;
    event Action<string>? OnPluginUnloaded;
    event Action<string, Exception>? OnPluginError;

    // Discovery
    Task<List<string>> DiscoverAsync();

    // Load
    Task LoadPluginAsync(string pluginPath);
    Task LoadAllAsync();

    // Unload
    Task UnloadPluginAsync(string pluginId);
    Task UnloadAllAsync();

    // Reload (unload + load)
    Task ReloadPluginAsync(string pluginId);

    // Accessors
    T? GetPlugin<T>(string pluginId) where T : class, IPlugin;
    IEnumerable<IPlugin> GetAllPlugins();
    IEnumerable<LoadedPlugin> GetLoadedPlugins();

    // Health
    Task<Dictionary<string, HealthStatus>> GetHealthAsync();
}
```

### Constructor

```csharp
var pluginManager = new PluginManager(
    eventBus:          CodeLogic.GetEventBus(),
    options:           new PluginOptions { PluginsDirectory = "CodeLogic/Plugins" },
    loggingOptions:    CodeLogic.GetConfiguration().Logging.ToLoggingOptions(),
    defaultCulture:    "en-US",
    supportedCultures: new[] { "en-US", "da-DK" }
);

// Register with the runtime for health checks and shutdown
CodeLogic.SetPluginManager(pluginManager);
```

---

## Directory Structure

Plugins live in subdirectories named `*.Plugin` under the `Plugins/` directory:

```
CodeLogic/
  Plugins/
    MyApp.Dashboard.Plugin/
      MyApp.Dashboard.Plugin.dll    ← must contain an IPlugin implementation
      MyApp.Dashboard.Plugin.deps.json
      config.json                   ← auto-generated
      localization/
        strings.en-US.json
      logs/
      data/
```

The `PluginManager` discovers plugins by looking for `*.Plugin/` directories and finding a `*.Plugin.dll` inside each.

---

## Hot-Reload via FileSystemWatcher

Enable `WatchForChanges = true` to automatically reload plugins when their DLL changes:

```csharp
var options = new PluginOptions
{
    PluginsDirectory = "CodeLogic/Plugins",
    EnableHotReload  = true,
    WatchForChanges  = true,
    ReloadDebounce   = TimeSpan.FromSeconds(2)
};
```

When the DLL changes:
1. The watcher fires (debounced to avoid double-reloads)
2. `ReloadPluginAsync` is called automatically
3. The old assembly is unloaded from memory (GC.Collect loop)
4. The new assembly is loaded and the full 4-phase lifecycle runs

```
~ Hot-reload: MyApp.Dashboard.Plugin.dll
  + Plugin loaded: Dashboard Plugin v1.1.0
```

---

## In-Process vs DLL Plugins

### DLL plugins (standard, hot-reloadable)

The standard case — plugin lives in its own directory as a `.dll`:

```csharp
await pluginManager.LoadPluginAsync("CodeLogic/Plugins/MyApp.Dashboard.Plugin/MyApp.Dashboard.Plugin.dll");
```

The assembly is loaded into its own `PluginLoadContext`. When unloaded, the context is released and the GC can collect the assembly once nothing references its types any more.

### In-process plugins (testing / embedded)

For testing or when you don't need isolation, implement `IPlugin` directly in the host project, run its lifecycle yourself and register it with `RegisterInProcessPluginAsync(plugin, context)` so it takes part in health checks and shutdown. Don't call `LoadPluginAsync` for it.

---

## Isolation

- `CodeLogic` (and any simple names in `PluginOptions.SharedAssemblies`) always resolves to the host's copy, so `IPlugin` and other contract types have one identity.
- Every other dependency resolves from the plugin's own `.deps.json` / folder first, then from the host. A plugin never receives an assembly loaded by another plugin.
- Plugins still run in the host process with its permissions and share the event bus. It is not a security boundary.

## Concurrency and failures

- Lifecycle operations (load, register, unload, reload, dispose, hot reload) run one at a time; readers work on snapshots.
- Plugin IDs are unique: loading a second plugin with a loaded ID throws before it starts.
- A failed load disposes the instance (after `OnUnloadAsync` if `OnStartAsync` was entered) and unloads its context. A failing `OnUnloadAsync` is reported but the plugin is still disposed and removed.

---

## SetPluginManager / GetPluginManager

Register the `PluginManager` with the runtime so it participates in health checks and graceful shutdown:

```csharp
// After InitializeAsync
CodeLogic.SetPluginManager(pluginManager);

// The runtime will now:
// - Include plugin health in GetHealthAsync()
// - Call UnloadAllAsync() during StopAsync()
// - Dispose the manager and clear the registration during ResetAsync()

// Retrieve it later:
var mgr = CodeLogic.GetPluginManager();
```

---

## Full Example Plugin Implementation

```csharp
// Plugin project: MyApp.Dashboard.Plugin.csproj

using CodeLogic.Framework.Application.Plugins;
using CodeLogic.Framework.Libraries;
using CodeLogic.Core.Configuration;

// Config model
public class DashboardConfig : ConfigModelBase
{
    public int RefreshIntervalSeconds { get; set; } = 5;
    public string Theme { get; set; } = "dark";
}

// Plugin implementation
public class DashboardPlugin : IPlugin
{
    public PluginManifest Manifest => new()
    {
        Id                 = "myapp.dashboard",
        Name               = "Dashboard Plugin",
        Version            = "1.0.0",
        Description        = "Real-time dashboard UI",
        MinFrameworkVersion = "5.0.0"
    };

    public PluginState State { get; private set; } = PluginState.Discovered;

    private PluginContext _context = null!;
    private DashboardConfig _config = null!;
    private DashboardServer? _server;

    public Task OnConfigureAsync(PluginContext context)
    {
        State = PluginState.Configured;
        context.Configuration.Register<DashboardConfig>();
        return Task.CompletedTask;
    }

    public Task OnInitializeAsync(PluginContext context)
    {
        State = PluginState.Initialized;
        _context = context;
        _config = context.Configuration.Get<DashboardConfig>();

        context.Logger.Info($"Dashboard initializing (theme={_config.Theme})");
        _server = new DashboardServer(_config.RefreshIntervalSeconds);

        return Task.CompletedTask;
    }

    public async Task OnStartAsync(PluginContext context)
    {
        State = PluginState.Started;
        await _server!.StartAsync();
        context.Logger.Info("Dashboard started");
    }

    public async Task OnUnloadAsync()
    {
        State = PluginState.Stopped;
        _context.Logger.Info("Dashboard unloading");
        if (_server != null)
            await _server.StopAsync();
    }

    public Task<HealthStatus> HealthCheckAsync()
    {
        return Task.FromResult(_server?.IsRunning == true
            ? HealthStatus.Healthy("Dashboard running")
            : HealthStatus.Unhealthy("Dashboard server not running"));
    }

    public void Dispose()
    {
        _server?.Dispose();
    }
}
```

### Loading in the application

```csharp
public class MyApp : IApplication
{
    private PluginManager? _plugins;

    public Task OnInitializeAsync(ApplicationContext context)
    {
        _plugins = new PluginManager(
            context.Events,
            new PluginOptions
            {
                PluginsDirectory = CodeLogic.GetOptions().GetPluginsPath(),
                WatchForChanges  = true
            });

        CodeLogic.SetPluginManager(_plugins);
        return Task.CompletedTask;
    }

    public async Task OnStartAsync(ApplicationContext context)
    {
        // Load all discovered plugins...
        await _plugins!.LoadAllAsync();

        // ...or, instead, load specific ones (loading an already-loaded ID throws):
        // var pluginPath = Path.Combine(
        //     CodeLogic.GetOptions().GetPluginsPath(),
        //     "MyApp.Dashboard.Plugin",
        //     "MyApp.Dashboard.Plugin.dll");
        // await _plugins.LoadPluginAsync(pluginPath);
    }

    public async Task OnStopAsync()
    {
        // Runtime will call UnloadAllAsync via SetPluginManager —
        // but you can also call it explicitly:
        if (_plugins != null)
            await _plugins.DisposeAsync();
    }
}
```

### Accessing a loaded plugin

The host sees a DLL plugin only through shared types: `IPlugin`, or a contract interface from an assembly listed in `PluginOptions.SharedAssemblies`. A class defined in the plugin assembly itself is a different type in the host, so ask for the shared interface:

```csharp
// IDashboard lives in a contracts assembly shared with the host.
var dashboard = CodeLogic.GetPluginManager()?.GetPlugin<IPlugin>("myapp.dashboard") as IDashboard;
dashboard?.SetTheme("light");
```

### Reloading a plugin at runtime

```csharp
await CodeLogic.GetPluginManager()!.ReloadPluginAsync("myapp.dashboard");
```
