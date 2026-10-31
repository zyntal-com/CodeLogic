# Application

The `IApplication` interface allows the consuming application to participate in the CodeLogic lifecycle. There is one application per runtime instance.

---

## IApplication Interface

```csharp
public interface IApplication
{
    ApplicationManifest Manifest { get; }

    Task OnConfigureAsync(ApplicationContext context);
    Task OnInitializeAsync(ApplicationContext context);
    Task OnStartAsync(ApplicationContext context);
    Task OnStopAsync();

    Task<HealthStatus> HealthCheckAsync();  // default: Healthy
}
```

---

## Lifecycle Ordering

The application lifecycle interleaves with the library lifecycle:

```
InitializeAsync()        ← framework init, config loaded

ConfigureAsync()
  ├── App.OnConfigureAsync()      ← register app config/localization
  └── (config files generated and loaded)

StartAsync()
  ├── Library1.OnConfigureAsync() ← libraries configure
  ├── Library1.OnInitializeAsync()
  ├── Library1.OnStartAsync()     ← libraries fully running
  ├── App.OnInitializeAsync()     ← app starts AFTER all libraries
  └── App.OnStartAsync()

StopAsync()
  ├── App.OnStopAsync()           ← app stops FIRST
  ├── Library1.OnStopAsync()      ← then libraries (reverse order)
  └── (done)
```

**Key principle:** By the time `OnInitializeAsync` is called on the application, all library services are fully operational. The application can safely call into any library from `OnInitializeAsync` and `OnStartAsync`.

---

## ApplicationManifest

```csharp
public sealed class ApplicationManifest
{
    string Id { get; init; }           // "homepoint" — used for context scoping
    string Name { get; init; }         // "HomePoint" — shown in console
    string Version { get; init; }      // "1.0.0"
    string? Description { get; init; }
    string? Author { get; init; }
}
```

---

## ApplicationContext

Provided to the application during all lifecycle phases:

```csharp
public sealed class ApplicationContext
{
    string ApplicationId          { get; }  // from Manifest.Id
    string ApplicationDirectory   { get; }  // {FrameworkRoot}/Application/
    string ConfigDirectory        { get; }  // same as ApplicationDirectory
    string LocalizationDirectory  { get; }  // {ApplicationDirectory}/localization/
    string LogsDirectory          { get; }  // {ApplicationDirectory}/logs/
    string DataDirectory          { get; }  // {ApplicationDirectory}/data/

    ILogger Logger               { get; }  // tagged "APPLICATION"
    IConfigurationManager Configuration { get; }
    ILocalizationManager Localization   { get; }
    IEventBus Events             { get; }  // shared instance
}
```

---

## HealthCheckAsync Default Implementation

The interface provides a default implementation that always returns Healthy:

```csharp
Task<HealthStatus> HealthCheckAsync() =>
    Task.FromResult(HealthStatus.Healthy($"{Manifest.Name} is running"));
```

Override it to add meaningful checks:

```csharp
public async Task<HealthStatus> HealthCheckAsync()
{
    // Check that a required library service is responsive
    var data = Libraries.Get<DataLibrary>();
    if (data == null)
        return HealthStatus.Unhealthy("Data library not available");

    try
    {
        await data.Db().PingAsync();
        return HealthStatus.Healthy("Application healthy");
    }
    catch (Exception ex)
    {
        return HealthStatus.Degraded($"Database check failed: {ex.Message}");
    }
}
```

---

## Full Example Implementation

```csharp
using CodeLogic.Framework.Application;
using CodeLogic.Framework.Libraries;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Localization;
using System.ComponentModel.DataAnnotations;

// Config model
public class HomePointConfig : ConfigModelBase
{
    [Required]
    public string DeviceName { get; set; } = "HomePoint Hub";

    [Range(1024, 65535)]
    public int ApiPort { get; set; } = 8080;

    public bool EnableTelemetry { get; set; } = false;
}

// Localization model
[LocalizationSection("ui")]
public class UiStrings : LocalizationModelBase
{
    public string AppTitle { get; set; } = "HomePoint";
    public string StatusRunning { get; set; } = "System is running";
}

// Application implementation
public class HomePointApp : IApplication
{
    public ApplicationManifest Manifest => new()
    {
        Id          = "homepoint",
        Name        = "HomePoint",
        Version     = "1.0.0",
        Description = "Z-Wave home automation hub"
    };

    private ApplicationContext _context = null!;
    private HomePointConfig _config = null!;
    private ApiServer? _server;

    // Phase 1: Register models (config not yet loaded)
    public Task OnConfigureAsync(ApplicationContext context)
    {
        context.Configuration.Register<HomePointConfig>();
        context.Localization.Register<UiStrings>();
        return Task.CompletedTask;
    }

    // Phase 2: Config loaded, libraries running — initialize services
    public Task OnInitializeAsync(ApplicationContext context)
    {
        _context = context;
        _config = context.Configuration.Get<HomePointConfig>();

        context.Logger.Info($"Initializing on port {_config.ApiPort}");

        // Libraries are fully running here — safe to use
        var zwave = Libraries.Get<ZWaveLibrary>()
            ?? throw new InvalidOperationException("Z-Wave library required");

        _server = new ApiServer(_config.ApiPort, zwave);

        return Task.CompletedTask;
    }

    // Phase 3: Start services
    public async Task OnStartAsync(ApplicationContext context)
    {
        await _server!.StartAsync();
        context.Logger.Info($"API server started on port {_config.ApiPort}");

        // Subscribe to library events
        context.Events.Subscribe<ComponentAlertEvent>(OnComponentAlert);
    }

    // Phase 4: Stop services before libraries stop
    public async Task OnStopAsync()
    {
        _context.Logger.Info("Stopping API server");
        if (_server != null)
            await _server.StopAsync();
    }

    // Health check
    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (_server == null || !_server.IsRunning)
            return HealthStatus.Unhealthy("API server is not running");

        return HealthStatus.Healthy($"Running on port {_config.ApiPort}");
    }

    private void OnComponentAlert(ComponentAlertEvent e)
    {
        _context.Logger.Info($"Alert from {e.ComponentId}: {e.Message}");
    }
}
```

### Registration in Program.cs

```csharp
var result = await CodeLogic.InitializeAsync(o => o.AppVersion = "1.0.0");
if (result.ShouldExit) return;

// Register libraries first
await Libraries.LoadAsync<ZWaveLibrary>();
await Libraries.LoadAsync<DataLibrary>();

// Register the application
CodeLogic.RegisterApplication(new HomePointApp());

// Configure (OnConfigureAsync runs, configs generated and loaded)
await CodeLogic.ConfigureAsync();

// Start (all libraries start, then the application)
await CodeLogic.StartAsync();

// Health check if requested
if (result.RunHealthCheck)
{
    var report = await CodeLogic.GetHealthAsync();
    Console.WriteLine(report.ToConsoleString());
    return;
}

Console.WriteLine("HomePoint running. Press Ctrl+C to stop.");
await Task.Delay(Timeout.Infinite);
```
