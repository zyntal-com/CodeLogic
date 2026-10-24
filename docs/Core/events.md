# Events

CodeLogic provides a synchronous/asynchronous publish-subscribe event bus shared across all libraries, the application, and plugins.

---

## IEvent Marker Interface

All events must implement `IEvent`:

```csharp
public interface IEvent { }
```

Define events as records:

```csharp
public record UserCreatedEvent(string UserId, string Email) : IEvent;
public record DeviceStateChangedEvent(string DeviceId, string State) : IEvent;
```

---

## IEventBus

The shared event bus is available on every context object (`context.Events`).

```csharp
public interface IEventBus
{
    void Publish<T>(T @event) where T : IEvent;
    Task PublishAsync<T>(T @event) where T : IEvent;
    IEventSubscription Subscribe<T>(Action<T> handler) where T : IEvent;
    IEventSubscription SubscribeAsync<T>(Func<T, Task> handler) where T : IEvent;
}
```

### Method reference

| Method | Description |
|--------|-------------|
| `Publish<T>` | Publishes synchronously. Sync handlers run immediately on the calling thread. Async handlers are fire-and-forget (not awaited). |
| `PublishAsync<T>` | Publishes and awaits all handlers (sync and async). |
| `Subscribe<T>` | Subscribes a sync handler. Returns a disposable subscription. |
| `SubscribeAsync<T>` | Subscribes an async handler. Returns a disposable subscription. |

---

## IEventSubscription

`Subscribe` and `SubscribeAsync` return an `IEventSubscription` that implements `IDisposable`. Dispose it to unsubscribe:

```csharp
IEventSubscription _sub;

public Task OnStartAsync(LibraryContext context)
{
    _sub = context.Events.Subscribe<UserCreatedEvent>(OnUserCreated);
    return Task.CompletedTask;
}

public Task OnStopAsync()
{
    _sub.Dispose();   // Unsubscribe
    return Task.CompletedTask;
}

private void OnUserCreated(UserCreatedEvent e)
{
    Console.WriteLine($"New user: {e.Email}");
}
```

---

## Event Ownership

Convention for where events are defined:

| Event type | Defined in |
|------------|------------|
| Framework lifecycle events | `CodeLogic.Core.Events` (FrameworkEvents.cs) |
| Library-specific events | The library's assembly |
| Application events | The application's assembly |
| Plugin events | The plugin's assembly |
| Cross-component signals (no type ref) | `ComponentAlertEvent` (see below) |

---

## Framework Events Reference

All 11 built-in framework events:

### Lifecycle Events

```csharp
// Published when a library completes OnStartAsync successfully
record LibraryStartedEvent(string LibraryId, string LibraryName) : IEvent;

// Published when a library completes OnStopAsync
record LibraryStoppedEvent(string LibraryId, string LibraryName) : IEvent;

// Published when a library throws during any lifecycle phase
record LibraryFailedEvent(string LibraryId, string LibraryName, Exception Error) : IEvent;

// Published when a plugin is successfully loaded
record PluginLoadedEvent(string PluginId, string PluginName) : IEvent;

// Published when a plugin is unloaded
record PluginUnloadedEvent(string PluginId, string PluginName) : IEvent;

// Published when a plugin throws during load or unload
record PluginFailedEvent(string PluginId, string PluginName, Exception Error) : IEvent;
```

### Configuration / Localization Events

```csharp
// Published after a specific config type is reloaded from disk
record ConfigReloadedEvent(string ComponentId, Type ConfigType) : IEvent;

// Published after any committed change (update, reload, reset) of a library config section (4.9.0+)
record ConfigChangedEvent(string ComponentId, string SectionName, Type ConfigType,
    ConfigChangeKind Kind, string? ChangedBy) : IEvent;

// Published when a valid, saved change could not be applied by the library (4.9.0+).
// The library reports Degraded health until a later change applies.
record ConfigApplyFailedEvent(string ComponentId, string SectionName, Exception Error) : IEvent;

// Published after all localizations are reloaded from disk
record LocalizationReloadedEvent(string ComponentId) : IEvent;
```

### Health Events

```csharp
// Published after a scheduled health check round completes
record HealthCheckCompletedEvent(string ComponentId, bool IsHealthy, string Message) : IEvent;
```

### Shutdown Events

```csharp
// Published when the framework begins shutting down (CTRL+C, ProcessExit, or StopAsync)
record ShutdownRequestedEvent(string Reason) : IEvent;
```

### Bridge Event

```csharp
// Generic bridge for lib-to-lib signals without cross-references
record ComponentAlertEvent(
    string ComponentId,
    string AlertType,
    string Message,
    object? Payload = null) : IEvent;
```

---

## ComponentAlertEvent Bridge Pattern

When Library A needs to signal Library B without taking a dependency on B's assembly, use `ComponentAlertEvent`:

```csharp
// In Library A (CL.ZWave): publish a connection-lost alert
context.Events.Publish(new ComponentAlertEvent(
    ComponentId: "CL.ZWave",
    AlertType:   "connection.lost",
    Message:     "Z-Wave controller disconnected",
    Payload:     null));
```

```csharp
// In Library B (CL.Dashboard): subscribe without referencing CL.ZWave
context.Events.Subscribe<ComponentAlertEvent>(e =>
{
    if (e.ComponentId == "CL.ZWave" && e.AlertType == "connection.lost")
    {
        UpdateDeviceStatus("disconnected");
    }
});
```

This pattern avoids coupling between library assemblies.

---

## Code Examples

### Synchronous subscription

```csharp
var sub = context.Events.Subscribe<LibraryStartedEvent>(e =>
{
    Console.WriteLine($"Library started: {e.LibraryName}");
});

// Later, to unsubscribe:
sub.Dispose();
```

### Asynchronous subscription

```csharp
var sub = context.Events.SubscribeAsync<UserCreatedEvent>(async e =>
{
    await SendWelcomeEmailAsync(e.Email);
});
```

### Fire-and-forget publish (sync)

```csharp
// Async handlers are not awaited here
context.Events.Publish(new UserCreatedEvent(user.Id, user.Email));
```

### Awaited publish (all handlers finish)

```csharp
// Awaits all handlers including async ones
await context.Events.PublishAsync(new OrderPlacedEvent(order.Id));
```

### Subscribe on startup, dispose on stop

```csharp
public class MyLibrary : ILibrary
{
    private IEventSubscription? _shutdownSub;
    private IEventSubscription? _healthSub;

    public Task OnStartAsync(LibraryContext context)
    {
        _shutdownSub = context.Events.Subscribe<ShutdownRequestedEvent>(e =>
        {
            context.Logger.Info($"Shutdown requested: {e.Reason}");
        });

        _healthSub = context.Events.Subscribe<HealthCheckCompletedEvent>(e =>
        {
            if (!e.IsHealthy)
                context.Logger.Warning($"Component {e.ComponentId} unhealthy: {e.Message}");
        });

        return Task.CompletedTask;
    }

    public Task OnStopAsync()
    {
        _shutdownSub?.Dispose();
        _healthSub?.Dispose();
        return Task.CompletedTask;
    }
}
```

---

## Thread Safety

- `Publish` calls sync handlers on the calling thread — be aware of threading context.
- `PublishAsync` awaits all handlers; exceptions from handlers propagate to the caller.
- `Subscribe` and `SubscribeAsync` are thread-safe — you can subscribe from any thread.
- `Dispose` on a subscription is thread-safe.
- Do not modify shared state from async event handlers without proper synchronization.
