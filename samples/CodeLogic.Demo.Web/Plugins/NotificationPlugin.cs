using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Demo.Web.Events;
using CodeLogic.Framework.Application.Plugins;
using CodeLogic.Framework.Libraries;

namespace CodeLogic.Demo.Web.Plugins;

// ─────────────────────────────────────────────────────────────────────────────
// NotificationPlugin — subscribes to AppNotificationEvent and:
//   - Keeps a rolling in-memory log of recent notifications
//   - Reports as Degraded if too many error-severity notifications arrive
//   - Exposes the log via /plugins/notifications endpoint
// ─────────────────────────────────────────────────────────────────────────────

public class NotificationPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id          = "demo.notifications",
        Name        = "Notification Plugin",
        Version     = "1.0.0",
        Description = "Keeps a rolling log of app notifications with health degradation on errors",
        Author      = "CodeLogic Demo"
    };

    public PluginState State { get; private set; } = PluginState.Loaded;

    private NotificationConfig _config = new();
    private IEventSubscription? _notifySub;

    // Rolling log — thread-safe
    private readonly List<NotificationEntry> _log = [];
    private readonly Lock _lock = new();
    private int _errorCount;

    public async Task OnConfigureAsync(PluginContext context)
    {
        context.Configuration.Register<NotificationConfig>();
        context.Logger.Info($"{Manifest.Name} configured");
        State = PluginState.Configured;
        await Task.CompletedTask;
    }

    public async Task OnInitializeAsync(PluginContext context)
    {
        _config = context.Configuration.Get<NotificationConfig>();
        context.Logger.Debug(
            $"NotificationPlugin: maxLog={_config.MaxLogSize}, " +
            $"errorThreshold={_config.ErrorThreshold}");
        context.Logger.Info($"{Manifest.Name} initialized");
        State = PluginState.Initialized;
        await Task.CompletedTask;
    }

    public async Task OnStartAsync(PluginContext context)
    {
        _notifySub = context.Events.Subscribe<AppNotificationEvent>(e =>
        {
            var entry = new NotificationEntry(DateTime.UtcNow, e.Severity, e.Title, e.Message);

            lock (_lock)
            {
                _log.Add(entry);
                if (e.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
                    _errorCount++;

                // Trim rolling log to max size
                if (_log.Count > _config.MaxLogSize)
                    _log.RemoveAt(0);
            }

            context.Logger.Debug(
                $"Notification [{e.Severity}] {e.Title}: {e.Message} " +
                $"(log size={_log.Count})");
        });

        context.Logger.Info($"{Manifest.Name} started — listening for notifications");
        State = PluginState.Started;
        await Task.CompletedTask;
    }

    public async Task OnUnloadAsync()
    {
        _notifySub?.Dispose();
        System.Console.WriteLine(
            $"  [NotificationPlugin] Unloaded — logged {_log.Count} notification(s), {_errorCount} error(s)");
        State = PluginState.Stopped;
        await Task.CompletedTask;
    }

    public Task<HealthStatus> HealthCheckAsync()
    {
        lock (_lock)
        {
            var msg = $"log={_log.Count}/{_config.MaxLogSize} errors={_errorCount}";
            if (_errorCount >= _config.ErrorThreshold)
                return Task.FromResult(HealthStatus.Degraded(
                    $"Error threshold reached ({_errorCount}/{_config.ErrorThreshold}) — {msg}"));
            return Task.FromResult(HealthStatus.Healthy(msg));
        }
    }

    // ── Public access for the /plugins/notifications endpoint ─────────────────
    public IReadOnlyList<NotificationEntry> GetLog()
    {
        lock (_lock) return [.. _log];
    }

    public void Dispose() => _notifySub?.Dispose();
}

// ── Supporting types ──────────────────────────────────────────────────────────

public record NotificationEntry(DateTime Timestamp, string Severity, string Title, string Message);

public class NotificationConfig : ConfigModelBase
{
    /// <summary>Maximum number of notifications to keep in memory.</summary>
    public int MaxLogSize { get; set; } = 50;

    /// <summary>Number of error-severity notifications that triggers Degraded health.</summary>
    public int ErrorThreshold { get; set; } = 5;
}
