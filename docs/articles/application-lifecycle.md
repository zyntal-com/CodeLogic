# Application Lifecycle

The **application** is the centrepiece of a CodeLogic host. It implements `IApplication`, participates in the same 4-phase lifecycle as libraries, and is the place where your business logic lives. The key difference from a library is *when* it runs: libraries are always fully started before the application's Initialize and Start phases execute.

---

## IApplication

```csharp
public interface IApplication
{
    ApplicationManifest Manifest { get; }

    Task OnConfigureAsync(ApplicationContext context);
    Task OnInitializeAsync(ApplicationContext context);
    Task OnStartAsync(ApplicationContext context);
    Task OnStopAsync();

    Task<HealthStatus> HealthCheckAsync();  // default: always Healthy
}
```

Register your application before calling `ConfigureAsync`:

```csharp
CodeLogic.RegisterApplication(new MyApp());
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();
```

---

## ApplicationManifest

```csharp
public ApplicationManifest Manifest => new()
{
    Id          = "homepoint",           // used to scope logs and directories
    Name        = "HomePoint",           // shown in console output and health reports
    Version     = "1.0.0",
    Description = "Self-hosted home automation platform",
    Author      = "Zyntal.com"
};
```

---

## ApplicationContext

The framework creates a single `ApplicationContext` and passes it to all lifecycle phases. It is scoped to your application's own directory (`CodeLogic/Application/`) and provides the same services as `LibraryContext`:

```csharp
public sealed class ApplicationContext
{
    // Directories (all created automatically on first run)
    public string ApplicationDirectory   { get; }  // CodeLogic/Application/
    public string ConfigDirectory        { get; }  // same as ApplicationDirectory
    public string LocalizationDirectory  { get; }  // CodeLogic/Application/localization/
    public string LogsDirectory          { get; }  // CodeLogic/Application/logs/
    public string DataDirectory          { get; }  // CodeLogic/Application/data/

    // Services
    public ILogger Logger                { get; }  // tagged "APPLICATION"
    public IConfigurationManager Configuration { get; }
    public ILocalizationManager Localization   { get; }
    public IEventBus Events              { get; }  // shared with all libraries and plugins
}
```

Store the context in a field — the same instance is passed through all phases, including `OnStopAsync` (which receives no parameter, so you must hold the reference from an earlier phase):

```csharp
private ApplicationContext _ctx = null!;

public Task OnInitializeAsync(ApplicationContext context)
{
    _ctx = context;  // keep it for OnStopAsync
    return Task.CompletedTask;
}

public Task OnStopAsync()
{
    _ctx.Logger.Info("Stopping");
    return Task.CompletedTask;
}
```

---

## Phase 1 — OnConfigureAsync

Called during `CodeLogic.ConfigureAsync()`, **before** libraries run their Configure phase. Use it exclusively to register config and localization models. Config files are generated and loaded *after* this method returns — do not read config values here.

```csharp
public Task OnConfigureAsync(ApplicationContext context)
{
    context.Configuration.Register<MyAppConfig>();
    context.Localization.Register<MyAppStrings>();
    return Task.CompletedTask;
}
```

---

## Phase 2 — OnInitializeAsync

Called during `CodeLogic.StartAsync()`, **after all libraries have fully started**. This is the earliest point at which library services are available. Set up your application's services here.

```csharp
public async Task OnInitializeAsync(ApplicationContext context)
{
    _ctx = context;

    // Read your config — guaranteed to be loaded by now
    var config = context.Configuration.Get<MyAppConfig>();

    // Libraries are fully running — access them freely
    var db   = Libraries.Get<CL.Data.DataLibrary>();
    var mail = Libraries.Get<CL.Mail.MailLibrary>();

    // Set up application services using library services
    _userService = new UserService(db, context.Logger);
    _notifier    = new NotificationService(mail);

    await _userService.InitializeAsync();

    context.Logger.Info($"Application initialized — environment: {config.Environment}");
}
```

---

## Phase 3 — OnStartAsync

Called immediately after `OnInitializeAsync`. Start background workers, subscribe to events, and begin accepting work.

```csharp
private CancellationTokenSource _cts = new();
private Task _backgroundWork = Task.CompletedTask;

public Task OnStartAsync(ApplicationContext context)
{
    // Subscribe to library events
    context.Events.Subscribe<CL.SystemStats.SystemSnapshotTakenEvent>(OnSnapshot);

    // Start background processing
    _backgroundWork = RunMainLoopAsync(_cts.Token);

    context.Logger.Info("Application started");
    return Task.CompletedTask;
}

private async Task RunMainLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await DoPeriodicWorkAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }
}
```

---

## Phase 4 — OnStopAsync

Called during `CodeLogic.StopAsync()` **before** libraries are stopped. This is important: your application must fully stop before the libraries it depends on are torn down.

```csharp
public async Task OnStopAsync()
{
    _ctx.Logger.Info("Stopping application");

    // Signal background work to stop
    _cts.Cancel();

    // Wait for it to finish cleanly
    try { await _backgroundWork; }
    catch (OperationCanceledException) { }

    // Flush anything pending
    await _notifier.FlushAsync();

    _ctx.Logger.Info("Application stopped");
}
```

---

## Health Check

Override `HealthCheckAsync()` to report your application's health. The default implementation always returns `Healthy`.

```csharp
public async Task<HealthStatus> HealthCheckAsync()
{
    if (!_userService.IsReady)
        return HealthStatus.Degraded("User service not ready");

    var dbOk = await _userService.PingAsync();
    return dbOk
        ? HealthStatus.Healthy("All systems operational")
        : HealthStatus.Unhealthy("Database unreachable");
}
```

Health is aggregated across all components by `CodeLogic.GetHealthAsync()`, which returns a `HealthReport` covering libraries, plugins, and the application.

---

## Plugins

If your application uses plugins, it owns a `PluginManager` and registers it with the framework so plugins participate in health checks and graceful shutdown:

```csharp
public Task OnInitializeAsync(ApplicationContext context)
{
    _ctx = context;

    // Create and register the plugin manager
    var plugins = new PluginManager(context.Events, new PluginOptions
    {
        PluginsDirectory = Path.Combine(context.DataDirectory, "plugins"),
        WatchForChanges  = true
    });

    CodeLogic.SetPluginManager(plugins);

    return Task.CompletedTask;
}

public async Task OnStartAsync(ApplicationContext context)
{
    var plugins = CodeLogic.GetPluginManager()!;
    await plugins.LoadAllAsync();
}
```

By registering the `PluginManager` via `CodeLogic.SetPluginManager()`, the framework will:
- Include plugins in `HealthReport` results
- Call `UnloadAllAsync()` on shutdown automatically, **before** libraries stop

See [Plugins](plugins.md) for full plugin authoring details.

---

## Full Example

```csharp
public sealed class MyApp : IApplication
{
    public ApplicationManifest Manifest => new()
    {
        Id      = "myapp",
        Name    = "My Application",
        Version = "1.0.0"
    };

    private ApplicationContext _ctx    = null!;
    private UserService        _users  = null!;
    private CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;

    public Task OnConfigureAsync(ApplicationContext context)
    {
        context.Configuration.Register<MyAppConfig>();
        return Task.CompletedTask;
    }

    public async Task OnInitializeAsync(ApplicationContext context)
    {
        _ctx  = context;
        var db = Libraries.Get<CL.Data.DataLibrary>();
        _users = new UserService(db, context.Logger);
        await _users.InitializeAsync();
    }

    public Task OnStartAsync(ApplicationContext context)
    {
        _loop = RunLoopAsync(_cts.Token);
        context.Logger.Info("Running");
        return Task.CompletedTask;
    }

    public async Task OnStopAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        _ctx.Logger.Info("Stopped");
    }

    public Task<HealthStatus> HealthCheckAsync() =>
        Task.FromResult(_users.IsReady
            ? HealthStatus.Healthy("Ready")
            : HealthStatus.Unhealthy("User service not ready"));

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
            await Task.Delay(5000, ct);
    }
}
```
