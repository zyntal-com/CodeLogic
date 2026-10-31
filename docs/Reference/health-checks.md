# Health Checks

CodeLogic aggregates health from libraries, plugins, and the application into a single `HealthReport`.

---

## `HealthStatus`

Every component returns `HealthStatus` from `HealthCheckAsync()`:

```csharp
public sealed class HealthStatus
{
    HealthStatusLevel Status { get; init; }
    string Message { get; init; }
    Dictionary<string, object>? Data { get; init; }
    DateTime CheckedAt { get; init; }

    bool IsHealthy => Status == HealthStatusLevel.Healthy;
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
    bool IsHealthy { get; init; }
    DateTime CheckedAt { get; init; }
    string MachineName { get; init; }
    string AppVersion { get; init; }

    Dictionary<string, HealthStatus> Libraries { get; init; }
    Dictionary<string, HealthStatus> Plugins { get; init; }
    HealthStatus? Application { get; init; }
}
```

`IsHealthy` is only `true` when every reported component is healthy.

---

## Scheduled Checks

When `healthChecks.enabled = true`, `CodeLogicRuntime` starts a timer during `StartAsync()` after libraries, plugins, and the application are running.

Each scheduled round:

- calls `CodeLogic.GetHealthAsync()`
- publishes one aggregate `HealthCheckCompletedEvent` for the runtime
- publishes one `HealthCheckCompletedEvent` per library, plugin, and application component

Configuration:

```json
{
  "healthChecks": {
    "enabled": true,
    "intervalSeconds": 30
  }
}
```

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

Application example:

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

var libraryHealth = await CodeLogic.GetLibraryManager()?.GetHealthAsync()
    ?? new Dictionary<string, HealthStatus>();

var pluginHealth = await CodeLogic.GetPluginManager()?.GetHealthAsync()
    ?? new Dictionary<string, HealthStatus>();
```
