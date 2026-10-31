# Plugins

Plugins are hot-loadable, isolated components managed by the consuming application. Unlike libraries, plugins are loaded into separate `AssemblyLoadContext` instances and can be unloaded and reloaded at runtime without restarting the host process.

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

The assembly is loaded into an isolated `PluginLoadContext`. When unloaded, the context is released and the GC collects the assembly.

### In-process plugins (testing / embedded)

For testing or when you don't need isolation, implement `IPlugin` directly in the host project and load it in-process. Just don't call `LoadPluginAsync` — manage the lifecycle manually using the same pattern.

---

## SetPluginManager / GetPluginManager

Register the `PluginManager` with the runtime so it participates in health checks and graceful shutdown:

```csharp
// After InitializeAsync
CodeLogic.SetPluginManager(pluginManager);

// The runtime will now:
// - Include plugin health in GetHealthAsync()
// - Call UnloadAllAsync() during StopAsync()

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
        // Load all discovered plugins
        await _plugins!.LoadAllAsync();

        // Or load a specific plugin
        var pluginPath = Path.Combine(
            CodeLogic.GetOptions().GetPluginsPath(),
            "MyApp.Dashboard.Plugin",
            "MyApp.Dashboard.Plugin.dll");

        await _plugins.LoadPluginAsync(pluginPath);
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

```csharp
var dashboard = CodeLogic.GetPluginManager()?.GetPlugin<DashboardPlugin>("myapp.dashboard");
dashboard?.SetTheme("light");
```

### Reloading a plugin at runtime

```csharp
await CodeLogic.GetPluginManager()!.ReloadPluginAsync("myapp.dashboard");
```
