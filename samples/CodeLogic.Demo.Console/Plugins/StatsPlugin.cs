using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Demo.Console.Events;
using CodeLogic.Framework.Application.Plugins;
using CodeLogic.Framework.Libraries;

namespace CodeLogic.Demo.Console.Plugins;

// ─────────────────────────────────────────────────────────────────────────────
// StatsPlugin — tracks work statistics and exposes them via health check.
//
//   - Counts total jobs, successes, and failures
//   - Tracks min/max/avg duration
//   - Health check returns Degraded if failure rate exceeds threshold
//   - Demonstrates: config, health check reporting, running stats
// ─────────────────────────────────────────────────────────────────────────────

public class StatsPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id          = "demo.stats",
        Name        = "Stats Plugin",
        Version     = "1.0.0",
        Description = "Tracks work completion statistics and reports via health check",
        Author      = "CodeLogic Demo"
    };

    public PluginState State { get; private set; } = PluginState.Loaded;

    // Running stats — guarded by _statsLock
    private int _total;
    private int _succeeded;
    private int _failed;
    private double _minMs = double.MaxValue;
    private double _maxMs;
    private double _totalMs;
    private readonly Lock _statsLock = new();

    private StatsConfig _config = new();
    private IEventSubscription? _workSub;

    public async Task OnConfigureAsync(PluginContext context)
    {
        context.Configuration.Register<StatsConfig>();
        context.Logger.Info($"{Manifest.Name} configured");
        State = PluginState.Configured;
        await Task.CompletedTask;
    }

    public async Task OnInitializeAsync(PluginContext context)
    {
        _config = context.Configuration.Get<StatsConfig>();
        context.Logger.Debug($"StatsPlugin: failureThreshold={_config.FailureThresholdPercent}%");
        context.Logger.Info($"{Manifest.Name} initialized");
        State = PluginState.Initialized;
        await Task.CompletedTask;
    }

    public async Task OnStartAsync(PluginContext context)
    {
        _workSub = context.Events.Subscribe<WorkCompletedEvent>(e =>
        {
            var ms = e.Duration.TotalMilliseconds;
            lock (_statsLock)
            {
                _total++;
                if (e.Success) _succeeded++;
                else _failed++;
                if (ms < _minMs) _minMs = ms;
                if (ms > _maxMs) _maxMs = ms;
                _totalMs += ms;
            }
            context.Logger.Trace(
                $"StatsPlugin: total={_total} ok={_succeeded} fail={_failed} " +
                $"avg={(_total > 0 ? _totalMs / _total : 0):0}ms");
        });

        context.Logger.Info($"{Manifest.Name} started — tracking WorkCompletedEvents");
        State = PluginState.Started;
        await Task.CompletedTask;
    }

    public async Task OnUnloadAsync()
    {
        _workSub?.Dispose();

        System.Console.WriteLine(
            $"  [StatsPlugin] Final stats: total={_total} ok={_succeeded} fail={_failed}");
        State = PluginState.Stopped;
        await Task.CompletedTask;
    }

    public Task<HealthStatus> HealthCheckAsync()
    {
        if (_total == 0)
            return Task.FromResult(HealthStatus.Healthy("No jobs recorded yet"));

        double failureRate = _total > 0 ? (_failed / (double)_total) * 100 : 0;
        double avgMs       = _totalMs / _total;

        var summary = $"total={_total} ok={_succeeded} fail={_failed} " +
                      $"avg={avgMs:0}ms min={(_minMs == double.MaxValue ? 0 : _minMs):0}ms max={_maxMs:0}ms";

        if (failureRate >= _config.FailureThresholdPercent)
            return Task.FromResult(HealthStatus.Degraded(
                $"High failure rate {failureRate:0.0}% (threshold {_config.FailureThresholdPercent}%) — {summary}"));

        return Task.FromResult(HealthStatus.Healthy(summary));
    }

    public void Dispose() => _workSub?.Dispose();
}

// ── Plugin config ─────────────────────────────────────────────────────────────

public class StatsConfig : ConfigModelBase
{
    /// <summary>Failure rate % above which health check returns Degraded.</summary>
    public double FailureThresholdPercent { get; set; } = 50.0;
}
