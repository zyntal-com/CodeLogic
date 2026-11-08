# Health Checks

CodeLogic provides a built-in health check system. Every library, plugin, and the application implements `HealthCheckAsync()`. The framework aggregates results into a `HealthReport`.

---

## HealthStatus

Returned by `HealthCheckAsync()`:

```csharp
public sealed class HealthStatus
{
    public HealthStatusLevel Status  { get; init; }   // Healthy, Degraded, Unhealthy
    public string Message            { get; init; }   // human-readable description
    public Dictionary<string, object>? Data { get; init; }   // optional structured data
    public DateTime CheckedAt        { get; init; }   // UTC timestamp

    public bool IsHealthy   => Status == HealthStatusLevel.Healthy;
    public bool IsDegraded  => Status == HealthStatusLevel.Degraded;
    public bool IsUnhealthy => Status == HealthStatusLevel.Unhealthy;
}
```

### HealthStatusLevel

| Level | Meaning |
|-------|---------|
| `Healthy` | Component is fully operational |
| `Degraded` | Running but with reduced capability (high latency, partial failure) |
| `Unhealthy` | Not functioning — requires attention |

---

## Factory Methods

Use the static factory methods to construct `HealthStatus`:

```csharp
// All good
return HealthStatus.Healthy("All 10 connections active");

// Partial failure — still running
return HealthStatus.Degraded("Queue depth is 8000 (threshold: 5000)");

// Full failure
return HealthStatus.Unhealthy("Cannot connect to database");

// From exception
try
{
    await _db.PingAsync();
    return HealthStatus.Healthy("Ping OK");
}
catch (Exception ex)
{
    return HealthStatus.FromException(ex);
    // → Unhealthy with ex.Message
}
```

---

## Adding Structured Data

Attach key-value data to the status for dashboards and monitoring:

```csharp
return new HealthStatus
{
    Status  = HealthStatusLevel.Degraded,
    Message = "High queue depth",
    Data    = new Dictionary<string, object>
    {
        ["QueueDepth"]      = 8421,
        ["QueueThreshold"]  = 5000,
        ["ActiveWorkers"]   = 3,
        ["PendingMessages"] = 8421
    }
};
```

---

## Implementing HealthCheckAsync

Implement `HealthCheckAsync()` on your library:

```csharp
public async Task<HealthStatus> HealthCheckAsync()
{
    if (!_isStarted)
        return HealthStatus.Unhealthy("Library not started");

    try
    {
        var latency = await _db.PingAsync();

        if (latency > TimeSpan.FromSeconds(1))
            return HealthStatus.Degraded($"High latency: {latency.TotalMilliseconds:F0}ms");

        var poolStats = _db.GetPoolStats();
        return new HealthStatus
        {
            Status  = HealthStatusLevel.Healthy,
            Message = $"Connected ({poolStats.Active}/{poolStats.Max} connections)",
            Data    = new Dictionary<string, object>
            {
                ["LatencyMs"]          = latency.TotalMilliseconds,
                ["ActiveConnections"]  = poolStats.Active,
                ["MaxConnections"]     = poolStats.Max
            }
        };
    }
    catch (Exception ex)
    {
        return HealthStatus.FromException(ex);
    }
}
```

---

## HealthReport

`CodeLogic.GetHealthAsync()` returns a `HealthReport` aggregating all component statuses:

```csharp
public sealed class HealthReport
{
    public bool IsHealthy { get; }                              // true only when every component is Healthy
    public DateTime CheckedAt { get; }                          // UTC
    public string MachineName { get; }
    public string AppVersion { get; }
    public Dictionary<string, HealthStatus> Libraries { get; }  // started libraries, keyed by ID
    public Dictionary<string, HealthStatus> Plugins { get; }    // started plugins, keyed by ID
    public HealthStatus? Application { get; }                   // null when no application is registered

    public string ToConsoleString();           // plain-text table
    public string ToJson();                    // JSON with messages, machine name and app version
    public string ToJson(bool includeDetails); // false: overall flag and status levels only
}
```

A component that is `Degraded` makes `IsHealthy` false. An exception thrown by the application's `HealthCheckAsync` is reported as `Unhealthy`.

---

## Querying Health

### Via CLI

```bash
./MyApp --health
```

`--health` sets `InitializationResult.RunHealthCheck`. The host runs the normal startup, prints the report and exits:

```csharp
var init = await CodeLogic.InitializeAsync();
if (init.ShouldExit) return;
// ... load libraries, register the application ...
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

if (init.RunHealthCheck)
{
    var report = await CodeLogic.GetHealthAsync();
    Console.WriteLine(report.ToConsoleString());
    await CodeLogic.StopAsync();
    return;
}
```

### Programmatically

```csharp
var report = await CodeLogic.GetHealthAsync();

Console.WriteLine(report.ToConsoleString());
// or
var json = report.ToJson();
await File.WriteAllTextAsync("health.json", json);

if (!report.IsHealthy)
    Environment.ExitCode = 1;
```

### In a web endpoint

```csharp
app.MapGet("/health", async () =>
{
    var report = await CodeLogic.GetHealthAsync();
    // Public endpoint: status levels only. Health messages can contain exception text and connection
    // details; serve report.ToJson() only to authenticated operators.
    return Results.Content(report.ToJson(includeDetails: false), "application/json",
        statusCode: report.IsHealthy ? 200 : 503);
});
```

---

## Scheduled Health Checks

Configure periodic health checks in `CodeLogic.json`:

```json
{
  "healthChecks": {
    "enabled": true,
    "intervalSeconds": 30
  }
}
```

Both values shown are the defaults. The timer starts after `StartAsync`. After each round the framework publishes a `HealthCheckCompletedEvent(ComponentId, IsHealthy, Message)` per library, plugin and the application, plus one with `ComponentId = "runtime"` for the overall result. Subscribe to it to forward results to a monitoring service:

```csharp
CodeLogic.GetEventBus().Subscribe<HealthCheckCompletedEvent>(e =>
{
    if (!e.IsHealthy)
        alerts.Send($"{e.ComponentId}: {e.Message}");
});
```
