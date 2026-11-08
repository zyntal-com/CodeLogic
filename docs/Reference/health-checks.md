# Health Checks

CodeLogic aggregates health from libraries, plugins, and the application into a single `HealthReport`.

---

## `HealthStatus`

Every component returns `HealthStatus` from `HealthCheckAsync()`:

```csharp
public sealed class HealthStatus
{
    public HealthStatusLevel Status { get; init; }          // Healthy, Degraded, Unhealthy
    public string Message { get; init; }
    public Dictionary<string, object>? Data { get; init; }
    public DateTime CheckedAt { get; init; }                // UTC

    public bool IsHealthy   => Status == HealthStatusLevel.Healthy;
    public bool IsDegraded  => Status == HealthStatusLevel.Degraded;
    public bool IsUnhealthy => Status == HealthStatusLevel.Unhealthy;
}
```

Common factory helpers:

```csharp
HealthStatus.Healthy("All good")
HealthStatus.Degraded("Running with reduced capacity")
HealthStatus.Unhealthy("Database unavailable")
HealthStatus.FromException(ex)
```

---

## `HealthReport`

```csharp
public sealed class HealthReport
{
    public bool IsHealthy { get; init; }
    public DateTime CheckedAt { get; init; }   // UTC
    public string MachineName { get; init; }
    public string AppVersion { get; init; }

    public Dictionary<string, HealthStatus> Libraries { get; init; }   // started libraries, by ID
    public Dictionary<string, HealthStatus> Plugins { get; init; }     // started plugins, by ID
    public HealthStatus? Application { get; init; }

    public string ToJson();                      // full details
    public string ToJson(bool includeDetails);   // false: status levels only
    public string ToConsoleString();
}
```

`IsHealthy` is only `true` when every reported component is healthy. A `Degraded` component makes it `false`.

---

## Scheduled Checks

When `healthChecks.enabled = true`, `CodeLogicRuntime` starts a timer at the end of `StartAsync()`, once libraries and the application are running. The first round runs one interval later.

Each scheduled round:

- calls `CodeLogic.GetHealthAsync()`
- publishes one aggregate `HealthCheckCompletedEvent` with `ComponentId = "runtime"`
- publishes one `HealthCheckCompletedEvent` per library, plugin, and application component (the application uses its manifest ID)

Configuration:

```json
{
  "healthChecks": {
    "enabled": true,
    "intervalSeconds": 30
  }
}
```

`intervalSeconds` must be greater than 0 while health checks are enabled; otherwise `InitializeAsync` fails.

`HealthReport.ToJson()` includes every component's message (which can contain exception text), the
machine name and the app version. For an unauthenticated endpoint use `ToJson(includeDetails: false)`,
which returns only the overall flag, the timestamp and each component's status level.

Example event subscription:

```csharp
context.Events.Subscribe<HealthCheckCompletedEvent>(e =>
{
    if (!e.IsHealthy)
        logger.Warning($"{e.ComponentId} is unhealthy: {e.Message}");
});
```

---

## `--health`

`--health` requests a report after normal startup:

```csharp
var result = await CodeLogic.InitializeAsync(o => o.AppVersion = "1.0.0");
if (result.ShouldExit) return;

await Libraries.LoadAsync<DataLibrary>();
CodeLogic.RegisterApplication(new MyApp());
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

if (result.RunHealthCheck)
{
    var report = await CodeLogic.GetHealthAsync();
    Console.WriteLine(report.ToConsoleString());
    await CodeLogic.StopAsync();
    return;
}
```

---

## Implementing `HealthCheckAsync()`

Library example:

```csharp
public async Task<HealthStatus> HealthCheckAsync()
{
    if (_db == null)
        return HealthStatus.Unhealthy("Database not initialized");

    try
    {
        var latency = await _db.MeasureLatencyAsync();
        return latency > TimeSpan.FromSeconds(5)
            ? HealthStatus.Degraded($"High latency: {latency.TotalMilliseconds:F0}ms")
            : HealthStatus.Healthy($"Database OK ({latency.TotalMilliseconds:F0}ms)");
    }
    catch (Exception ex)
    {
        return HealthStatus.FromException(ex);
    }
}
```

Application example. `IApplication.HealthCheckAsync()` has a default that reports `Healthy`; override it when the application has something to check:

```csharp
public async Task<HealthStatus> HealthCheckAsync()
{
    int pendingJobs = await GetPendingJobCount();
    return pendingJobs > 1000
        ? HealthStatus.Degraded($"{pendingJobs} jobs pending")
        : HealthStatus.Healthy("Application healthy");
}
```

---

## Requesting Reports Programmatically

```csharp
var report = await CodeLogic.GetHealthAsync();

Dictionary<string, HealthStatus> libraryHealth =
    CodeLogic.GetLibraryManager() is { } libraries ? await libraries.GetHealthAsync() : [];

Dictionary<string, HealthStatus> pluginHealth =
    CodeLogic.GetPluginManager() is { } plugins ? await plugins.GetHealthAsync() : [];
```
