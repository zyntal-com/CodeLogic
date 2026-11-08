using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Demo.Web.Events;
using CodeLogic.Framework.Application.Plugins;
using CodeLogic.Framework.Libraries;

namespace CodeLogic.Demo.Web.Plugins;

// ─────────────────────────────────────────────────────────────────────────────
// RequestLoggerPlugin — intercepts RequestReceivedEvent and tracks:
//   - Total request count per HTTP method
//   - Total request count per path
//   - Most requested path
//   - Exposes stats via health check and /plugins/request-stats endpoint
// ─────────────────────────────────────────────────────────────────────────────

public class RequestLoggerPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id          = "demo.request-logger",
        Name        = "Request Logger Plugin",
        Version     = "1.0.0",
        Description = "Tracks HTTP request counts by method and path",
        Author      = "CodeLogic Demo"
    };

    public PluginState State { get; private set; } = PluginState.Loaded;

    // Thread-safe stats
    private readonly Dictionary<string, int> _byMethod = [];
    private readonly Dictionary<string, int> _byPath   = [];
    private readonly Lock _lock = new();
    private int _total;

    private RequestLoggerConfig _config = new();
    private IEventSubscription? _requestSub;

    public async Task OnConfigureAsync(PluginContext context)
    {
        context.Configuration.Register<RequestLoggerConfig>();
        context.Logger.Info($"{Manifest.Name} configured");
        State = PluginState.Configured;
        await Task.CompletedTask;
    }

    public async Task OnInitializeAsync(PluginContext context)
    {
        _config = context.Configuration.Get<RequestLoggerConfig>();
        context.Logger.Debug($"RequestLoggerPlugin: trackPaths={_config.TrackPaths}");
        context.Logger.Info($"{Manifest.Name} initialized");
        State = PluginState.Initialized;
        await Task.CompletedTask;
    }

    public async Task OnStartAsync(PluginContext context)
    {
        _requestSub = context.Events.Subscribe<RequestReceivedEvent>(e =>
        {
            lock (_lock)
            {
                _total++;
                _byMethod[e.Method] = _byMethod.GetValueOrDefault(e.Method) + 1;
                if (_config.TrackPaths)
                    _byPath[e.Path] = _byPath.GetValueOrDefault(e.Path) + 1;
            }
            context.Logger.Trace($"RequestLogger: {e.Method} {e.Path} (total={_total})");
        });

        context.Logger.Info($"{Manifest.Name} started — tracking RequestReceivedEvents");
        State = PluginState.Started;
        await Task.CompletedTask;
    }

    public async Task OnUnloadAsync()
    {
        _requestSub?.Dispose();
        context_logger_info($"Unloaded — tracked {_total} request(s)");
        State = PluginState.Stopped;
        await Task.CompletedTask;
    }

    private static void context_logger_info(string msg) =>
        System.Console.WriteLine($"  [RequestLoggerPlugin] {msg}");

    public Task<HealthStatus> HealthCheckAsync()
    {
        var topPath = _byPath.OrderByDescending(kv => kv.Value).FirstOrDefault();
        var msg = _total == 0
            ? "No requests tracked yet"
            : $"Total={_total} methods=[{string.Join(",", _byMethod.Select(kv => $"{kv.Key}:{kv.Value}"))}]" +
              (topPath.Key != null ? $" top='{topPath.Key}'({topPath.Value})" : "");

        return Task.FromResult(HealthStatus.Healthy(msg));
    }

    // ── Public stats (read by the /plugins/request-stats endpoint) ────────────
    public (int Total, IReadOnlyDictionary<string, int> ByMethod, IReadOnlyDictionary<string, int> ByPath) GetStats()
    {
        lock (_lock)
            return (_total, new Dictionary<string, int>(_byMethod), new Dictionary<string, int>(_byPath));
    }

    public void Dispose() => _requestSub?.Dispose();
}

public class RequestLoggerConfig : ConfigModelBase
{
    /// <summary>Track per-path counts in addition to per-method counts.</summary>
    public bool TrackPaths { get; set; } = true;
}
