using CodeLogic.Core.Events;
using Xunit;

namespace CodeLogic.Tests;

public sealed class EventBusTests
{
    private sealed record Ping(int Value) : IEvent;

    [Fact]
    public void Subscribing_during_a_publish_takes_effect_from_the_next_publish()
    {
        var bus = new EventBus();
        var late = 0;
        var subscribed = false;
        bus.Subscribe<Ping>(_ =>
        {
            if (subscribed) return;
            subscribed = true;
            bus.Subscribe<Ping>(_ => late++);
        });

        bus.Publish(new Ping(1));
        Assert.Equal(0, late); // the publish in progress used the snapshot from before

        bus.Publish(new Ping(2));
        Assert.Equal(1, late);
    }

    [Fact]
    public void Unsubscribing_during_a_publish_takes_effect_from_the_next_publish()
    {
        var bus = new EventBus();
        var second = 0;
        IEventSubscription? secondSubscription = null;
        bus.Subscribe<Ping>(_ => secondSubscription!.Dispose());
        secondSubscription = bus.Subscribe<Ping>(_ => second++);

        bus.Publish(new Ping(1));
        Assert.Equal(1, second);

        bus.Publish(new Ping(2));
        Assert.Equal(1, second);
    }

    [Fact]
    public void Disposed_subscriptions_stop_receiving_and_others_keep_their_order()
    {
        var bus = new EventBus();
        var calls = new List<string>();
        bus.Subscribe<Ping>(_ => calls.Add("a"));
        var b = bus.Subscribe<Ping>(_ => calls.Add("b"));
        bus.Subscribe<Ping>(_ => calls.Add("c"));

        b.Dispose();
        b.Dispose(); // idempotent
        bus.Publish(new Ping(1));

        Assert.Equal(["a", "c"], calls);
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_the_others()
    {
        var bus = new EventBus();
        var reached = false;
        bus.Subscribe<Ping>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<Ping>(_ => reached = true);

        bus.Publish(new Ping(1));

        Assert.True(reached);
    }

    [Fact]
    public async Task PublishAsync_awaits_async_handlers_in_order()
    {
        var bus = new EventBus();
        var calls = new List<int>();
        bus.SubscribeAsync<Ping>(async p => { await Task.Delay(20); calls.Add(p.Value * 10 + 1); });
        bus.SubscribeAsync<Ping>(async p => { await Task.Yield(); calls.Add(p.Value * 10 + 2); });

        await bus.PublishAsync(new Ping(1));

        Assert.Equal([11, 12], calls);
    }

    [Fact]
    public async Task Concurrent_subscribe_and_publish_is_safe()
    {
        var bus = new EventBus();
        var received = 0;
        var publishers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++) bus.Publish(new Ping(i));
        }));
        var subscribers = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var s = bus.Subscribe<Ping>(_ => Interlocked.Increment(ref received));
                if (i % 2 == 0) s.Dispose();
            }
        });

        await Task.WhenAll([.. publishers, subscribers]);
        bus.Publish(new Ping(-1));

        Assert.True(received >= 100); // the 100 remaining subscribers saw at least the final publish
    }
}
