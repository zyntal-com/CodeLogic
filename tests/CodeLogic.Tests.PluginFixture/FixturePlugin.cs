using CodeLogic.Core.Events;
using CodeLogic.Framework.Application.Plugins;
using CodeLogic.Framework.Libraries;
using CodeLogic.Tests.PluginFixture.Dependency;

namespace CodeLogic.Tests.PluginFixture;

/// <summary>
/// Plugin whose behavior is driven by <c>behavior.txt</c> next to its assembly:
/// <c>id=...</c>, <c>fail=configure|initialize|start|unload|dispose</c>, <c>startDelayMs=...</c>,
/// <c>reenter=configure|initialize|start|unload</c>.
/// Every lifecycle call is appended to <c>calls.log</c> in the same folder. In the <c>reenter</c> step
/// the plugin awaits <see cref="IEventBus.PublishAsync{T}"/> of a <see cref="ComponentAlertEvent"/>
/// (AlertType "reenter"), so a test handler can call back into the manager from inside the callback.
/// 
/// </summary>
public sealed class FixturePlugin : IPlugin
{
    private readonly string _folder;
    private readonly Dictionary<string, string> _behavior;
    private IEventBus? _events;

    public FixturePlugin()
    {
        _folder = Path.GetDirectoryName(typeof(FixturePlugin).Assembly.Location)!;
        var file = Path.Combine(_folder, "behavior.txt");
        _behavior = File.Exists(file)
            ? File.ReadAllLines(file)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase)
            : [];

        Manifest = new PluginManifest
        {
            Id = _behavior.GetValueOrDefault("id", "fixture"),
            Name = "Fixture",
            Version = "1.0.0"
        };
    }

    public PluginManifest Manifest { get; }
    public PluginState State { get; private set; } = PluginState.Loaded;

    public Task OnConfigureAsync(PluginContext context)
    {
        _events = context.Events;
        return Step("configure", PluginState.Configured);
    }

    public Task OnInitializeAsync(PluginContext context) => Step("initialize", PluginState.Initialized);

    public async Task OnStartAsync(PluginContext context)
    {
        if (int.TryParse(_behavior.GetValueOrDefault("startDelayMs"), out var delay) && delay > 0)
            await Task.Delay(delay);
        await Step("start", PluginState.Started);
    }

    public Task OnUnloadAsync() => Step("unload", PluginState.Stopped);

    public Task<HealthStatus> HealthCheckAsync() =>
        Task.FromResult(HealthStatus.Healthy(DependencyMarker.LoadContextName));

    public void Dispose()
    {
        Record("dispose");
        if (Fails("dispose"))
            throw new InvalidOperationException("fixture: dispose failed");
    }

    private async Task Step(string name, PluginState next)
    {
        Record(name);
        if (_events is not null &&
            string.Equals(_behavior.GetValueOrDefault("reenter"), name, StringComparison.OrdinalIgnoreCase))
            await _events.PublishAsync(new ComponentAlertEvent(Manifest.Id, "reenter", name));
        if (Fails(name))
            throw new InvalidOperationException($"fixture: {name} failed");
        State = next;
    }

    private bool Fails(string step) =>
        string.Equals(_behavior.GetValueOrDefault("fail"), step, StringComparison.OrdinalIgnoreCase);

    private void Record(string step)
    {
        lock (_behavior)
            File.AppendAllText(Path.Combine(_folder, "calls.log"), step + Environment.NewLine);
    }
}
