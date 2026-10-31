using BenchmarkDotNet.Attributes;
using CodeLogic.Core.Events;

namespace CodeLogic.Benchmarks;

public sealed record BenchEvent(int Value) : IEvent;

[MemoryDiagnoser]
public class EventBusBenchmarks
{
    private EventBus _one = null!;
    private EventBus _ten = null!;
    private EventBus _framework = null!;
    private readonly BenchEvent _event = new(42);
    private readonly LibraryStartedEvent _started = new("cl.bench", "Bench");
    private long _sink;

    [GlobalSetup]
    public void Setup()
    {
        _one = new EventBus();
        _one.Subscribe<BenchEvent>(e => _sink += e.Value);

        _ten = new EventBus();
        for (var i = 0; i < 10; i++)
            _ten.Subscribe<BenchEvent>(e => _sink += e.Value);

        // One exact-type subscriber plus one FrameworkEvent subscriber (fan-out path).
        _framework = new EventBus();
        _framework.Subscribe<LibraryStartedEvent>(_ => _sink++);
        _framework.Subscribe<FrameworkEvent>(_ => _sink++);
    }

    [Benchmark(Baseline = true)]
    public void Publish_1_subscriber() => _one.Publish(_event);

    [Benchmark]
    public void Publish_10_subscribers() => _ten.Publish(_event);

    [Benchmark]
    public void Publish_framework_event_fan_out() => _framework.Publish(_started);
}
