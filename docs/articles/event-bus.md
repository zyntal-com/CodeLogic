# Event Bus

CodeLogic provides a shared publish-subscribe event bus (`IEventBus`) available on every context object. Libraries, the application, and plugins all share the same bus instance.

---

## Defining Events

Events are plain C# records implementing `IEvent`:

```csharp
public interface IEvent { }

public record UserCreatedEvent(string UserId, string Email) : IEvent;
public record DeviceStateChangedEvent(string DeviceId, string NewState, string OldState) : IEvent;
public record QueueDepthEvent(string QueueName, int Depth) : IEvent;
```

Events should be immutable — use `record` types.

---

## IEventBus

```csharp
public interface IEventBus
{
    // Synchronous publish — sync handlers run immediately, async handlers are fire-and-forget
    void Publish<T>(T @event) where T : IEvent;

    // Asynchronous publish — awaits all handlers (sync and async), one after another
    Task PublishAsync<T>(T @event) where T : IEvent;

    // Subscribe a synchronous handler
    IEventSubscription Subscribe<T>(Action<T> handler) where T : IEvent;

    // Subscribe an asynchronous handler
    IEventSubscription SubscribeAsync<T>(Func<T, Task> handler) where T : IEvent;
}
```

---

## Publishing Events

From `OnStartAsync` onwards, through the context's `Events` property:

```csharp
// fire-and-forget sync publish
context.Events.Publish(new UserCreatedEvent("user-123", "alice@example.com"));

// awaited async publish — waits for all handlers
await context.Events.PublishAsync(new DeviceStateChangedEvent("switch-1", "On", "Off"));
```

---

## Subscribing to Events

Subscribe in `OnStartAsync`. Store the subscription to dispose it in `OnStopAsync`:

```csharp
private IEventSubscription _userCreatedSub = null!;

public Task OnStartAsync(LibraryContext context)
{
    // synchronous handler
    _userCreatedSub = context.Events.Subscribe<UserCreatedEvent>(e =>
    {
        context.Logger.Info($"User created: {e.Email}");
    });

    return Task.CompletedTask;
}

public Task OnStopAsync()
{
    _userCreatedSub.Dispose();   // unsubscribes
    return Task.CompletedTask;
}
```

### Async handler

```csharp
_sub = context.Events.SubscribeAsync<UserCreatedEvent>(async e =>
{
    await _emailService.SendWelcomeAsync(e.Email);
});
```

---

## IEventSubscription

`Subscribe` and `SubscribeAsync` return `IEventSubscription`, which implements `IDisposable`. Disposing it unsubscribes the handler:

```csharp
public interface IEventSubscription : IDisposable { }
```

Disposing twice is safe.

A common pattern is to collect subscriptions and dispose them all on stop:

```csharp
private readonly List<IEventSubscription> _subscriptions = [];

public Task OnStartAsync(LibraryContext context)
{
    _subscriptions.Add(context.Events.Subscribe<UserCreatedEvent>(OnUserCreated));
    _subscriptions.Add(context.Events.Subscribe<QueueDepthEvent>(OnQueueDepth));
    return Task.CompletedTask;
}

public Task OnStopAsync()
{
    foreach (var sub in _subscriptions) sub.Dispose();
    _subscriptions.Clear();
    return Task.CompletedTask;
}
```

---

## Thread Safety

`IEventBus` is thread-safe. Sync handlers run on the thread that calls `Publish`. For `PublishAsync`, handlers are awaited one after another.

If a handler throws, the exception is caught and written to stderr. It never reaches the publisher, and the remaining handlers still run.

`Publish` takes no lock and allocates nothing. Subscribing and unsubscribing replace a snapshot, so a publish sees the subscribers that existed when it started.

---

## Framework Events

Framework events derive from `FrameworkEvent`, a C# 15 `closed record`:

| Event | Published when |
|-------|---------------|
| `LibraryStartedEvent` | A library completes `OnStartAsync` |
| `LibraryStoppedEvent` | A library completes `OnStopAsync` |
| `LibraryFailedEvent` | A library throws during a lifecycle phase |
| `PluginLoadedEvent` | A plugin is loaded by `PluginManager` |
| `PluginUnloadedEvent` | A plugin is unloaded |
| `PluginFailedEvent` | A plugin throws during load or unload |
| `ConfigChangedEvent` | A library config section is updated, reloaded or reset |
| `ConfigReloadedEvent` | A config section is reloaded from disk |
| `ConfigApplyFailedEvent` | A saved config change could not be applied by the library |
| `LocalizationReloadedEvent` | A component's localizations are reloaded |
| `HealthCheckCompletedEvent` | A scheduled health check finishes |
| `ShutdownRequestedEvent` | Shutdown begins (CTRL+C, process exit or `StopAsync`) |
| `ComponentAlertEvent` | A library or plugin raises a cross-component alert (see below) |

Subscribe to a single event type, or to `FrameworkEvent` to receive all of them in one handler:

```csharp
_sub = context.Events.Subscribe<FrameworkEvent>(e =>
{
    switch (e)
    {
        case LibraryFailedEvent f:
            context.Logger.Error($"Library {f.LibraryName} failed", f.Error);
            break;
        case PluginFailedEvent f:
            context.Logger.Error($"Plugin {f.PluginName} failed", f.Error);
            break;
        case ShutdownRequestedEvent r:
            context.Logger.Info($"Shutting down: {r.Reason}");
            break;
    }
});
```

Your own events implement `IEvent` directly and are not delivered to `FrameworkEvent` subscribers.

---

## ComponentAlertEvent Pattern

`ComponentAlertEvent` is a built-in framework event for cross-component alerts. Components can signal each other without referencing each other's assemblies:

```csharp
public record ComponentAlertEvent(
    string ComponentId,
    string AlertType,
    string Message,
    object? Payload = null) : FrameworkEvent;

// In any library or plugin:
context.Events.Publish(new ComponentAlertEvent(
    ComponentId: Manifest.Id,
    AlertType:   "queue.depth_critical",
    Message:     "Queue depth critical",
    Payload:     depth));

// In the application — aggregate and forward to monitoring:
_sub = context.Events.Subscribe<ComponentAlertEvent>(alert =>
{
    _monitor.SendAlert(alert.ComponentId, alert.AlertType, alert.Message);
});
```
