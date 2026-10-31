namespace CodeLogic.Core.Events;

/// <summary>
/// Thread-safe in-process event bus.
/// One instance is shared across all library, application, and plugin contexts.
/// </summary>
/// <remarks>
/// Publishing takes no lock and allocates nothing: subscribers live in an immutable snapshot
/// (event type → handler array) that subscribe/unsubscribe replace under a lock (copy-on-write).
/// A publish sees the subscribers that existed when it started.
/// </remarks>
public sealed class EventBus : IEventBus
{
    private static readonly SubscriptionEntry[] None = [];

    private readonly Lock _writeLock = new();
    private volatile Dictionary<Type, SubscriptionEntry[]> _subscriptions = [];

    /// <inheritdoc />
    public void Publish<T>(T @event) where T : IEvent
    {
        var (exact, framework) = Resolve(typeof(T), @event);
        foreach (var entry in exact) Invoke(entry, @event, typeof(T));
        foreach (var entry in framework) Invoke(entry, @event, typeof(T));
    }

    /// <inheritdoc />
    public async Task PublishAsync<T>(T @event) where T : IEvent
    {
        var (exact, framework) = Resolve(typeof(T), @event);
        foreach (var entry in exact) await InvokeAsync(entry, @event, typeof(T)).ConfigureAwait(false);
        foreach (var entry in framework) await InvokeAsync(entry, @event, typeof(T)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEventSubscription Subscribe<T>(Action<T> handler) where T : IEvent
    {
        var entry = new SubscriptionEntry
        {
            EventType = typeof(T),
            SyncHandler = e => handler((T)e)
        };
        AddEntry(typeof(T), entry);
        return new EventSubscription(this, typeof(T), entry);
    }

    /// <inheritdoc />
    public IEventSubscription SubscribeAsync<T>(Func<T, Task> handler) where T : IEvent
    {
        var entry = new SubscriptionEntry
        {
            EventType = typeof(T),
            AsyncHandler = e => handler((T)e)
        };
        AddEntry(typeof(T), entry);
        return new EventSubscription(this, typeof(T), entry);
    }

    internal void Unsubscribe(Type eventType, SubscriptionEntry entry)
    {
        lock (_writeLock)
        {
            var current = _subscriptions;
            if (!current.TryGetValue(eventType, out var handlers))
                return;
            var index = Array.IndexOf(handlers, entry);
            if (index < 0)
                return;

            var next = new Dictionary<Type, SubscriptionEntry[]>(current);
            if (handlers.Length == 1)
                next.Remove(eventType);
            else
                next[eventType] = [.. handlers.AsSpan(0, index), .. handlers.AsSpan(index + 1)];
            _subscriptions = next;
        }
    }

    /// <summary>Removes every subscription. Disposing a subscription afterwards is a no-op.</summary>
    internal void Clear()
    {
        lock (_writeLock)
            _subscriptions = [];
    }

    private void AddEntry(Type eventType, SubscriptionEntry entry)
    {
        lock (_writeLock)
        {
            var current = _subscriptions;
            var next = new Dictionary<Type, SubscriptionEntry[]>(current);
            next[eventType] = current.TryGetValue(eventType, out var handlers) ? [.. handlers, entry] : [entry];
            _subscriptions = next;
        }
    }

    // Subscribers are keyed by the static event type. Framework events are additionally delivered to
    // subscribers of the closed FrameworkEvent base, so one handler can switch over all of them.
    private (SubscriptionEntry[] Exact, SubscriptionEntry[] Framework) Resolve(Type eventType, IEvent @event)
    {
        var snapshot = _subscriptions;
        var exact = snapshot.TryGetValue(eventType, out var handlers) ? handlers : None;
        var framework = @event is FrameworkEvent && eventType != typeof(FrameworkEvent)
                        && snapshot.TryGetValue(typeof(FrameworkEvent), out var frameworkHandlers)
            ? frameworkHandlers
            : None;
        return (exact, framework);
    }

    private static void Invoke(SubscriptionEntry entry, object @event, Type eventType)
    {
        try
        {
            if (entry.SyncHandler != null)
                entry.SyncHandler(@event);
            else if (entry.AsyncHandler != null)
                // Fire-and-forget async handlers
                _ = InvokeSafeAsync(entry.AsyncHandler, @event);
        }
        catch (Exception ex)
        {
            // Log to stderr — never crash on event handler errors
            Console.Error.WriteLine($"[EventBus] Handler error for {eventType.Name}: {ex.Message}");
        }
    }

    private static async Task InvokeAsync(SubscriptionEntry entry, object @event, Type eventType)
    {
        try
        {
            if (entry.SyncHandler != null)
                entry.SyncHandler(@event);
            else if (entry.AsyncHandler != null)
                await entry.AsyncHandler(@event).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventBus] Handler error for {eventType.Name}: {ex.Message}");
        }
    }

    private static async Task InvokeSafeAsync(Func<object, Task> handler, object @event)
    {
        try { await handler(@event); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventBus] Async handler error: {ex.Message}");
        }
    }
}

internal sealed class SubscriptionEntry
{
    public required Type EventType { get; init; }
    public Action<object>? SyncHandler { get; init; }
    public Func<object, Task>? AsyncHandler { get; init; }
}

internal sealed class EventSubscription : IEventSubscription
{
    private readonly EventBus _bus;
    private readonly Type _eventType;
    private readonly SubscriptionEntry _entry;
    private bool _disposed;

    public EventSubscription(EventBus bus, Type eventType, SubscriptionEntry entry)
    {
        _bus = bus;
        _eventType = eventType;
        _entry = entry;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bus.Unsubscribe(_eventType, _entry);
    }
}
