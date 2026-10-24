using System.ComponentModel.DataAnnotations;
using CodeLogic.Core.Configuration;
using CodeLogic.Framework.Libraries;

namespace CodeLogic.Demo.Shared;

// ─────────────────────────────────────────────────────────────────────────────
// Demo library with several named instances in ONE config section — the same
// shape as CL.MySQL2's "Databases". It implements IConfigReloadable so a change
// to one endpoint reconnects only that endpoint; the others stay connected.
//
// Try it:
//   - Console demo: press [C] (changes "Billing" via Libraries.UpdateConfigAsync),
//   - Web demo: PUT /config/CL.DemoEndpoints/endpoints (see ConfigEndpoints.cs), or
//   - set "libraries.configHotReload.enabled": true in CodeLogic.json and edit
//     data/codelogic/Libraries/CL.DemoEndpoints/config.endpoints.json while running.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EndpointsConfig : ConfigModelBase
{
    public Dictionary<string, EndpointConfig> Endpoints { get; set; } = new()
    {
        ["Orders"]  = new() { Url = "https://orders.local" },
        ["Billing"] = new() { Url = "https://billing.local" },
        ["Search"]  = new() { Url = "https://search.local" }
    };

    public override ConfigValidationResult Validate()
    {
        var errors = Endpoints
            .Where(e => string.IsNullOrWhiteSpace(e.Value.Url))
            .Select(e => $"Endpoint '{e.Key}': url is required")
            .ToList();
        return errors.Count > 0 ? ConfigValidationResult.Invalid(errors) : ConfigValidationResult.Valid();
    }
}

public sealed class EndpointConfig
{
    [Required]
    [ConfigField(Label = "URL", RequiresRestart = true)]
    public string Url { get; set; } = string.Empty;

    [ConfigField(Label = "Timeout (s)", Min = 1, Max = 300)]
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class EndpointsLibrary : ILibrary, IConfigReloadable
{
    private readonly Dictionary<string, string> _connections = new();

    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.DemoEndpoints",
        Name = "Demo Endpoints",
        Version = "1.0.0"
    };

    public Task OnConfigureAsync(LibraryContext context)
    {
        context.Configuration.Register<EndpointsConfig>("endpoints");
        return Task.CompletedTask;
    }

    public Task OnInitializeAsync(LibraryContext context)
    {
        foreach (var (id, endpoint) in context.Configuration.Get<EndpointsConfig>().Endpoints)
            Connect(id, endpoint);
        return Task.CompletedTask;
    }

    public Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
    {
        var diff = ConfigDiff.Keyed(
            change.GetOld<EndpointsConfig>()?.Endpoints,
            change.GetNew<EndpointsConfig>()!.Endpoints);

        foreach (var (id, _) in diff.Removed)
            Disconnect(id);

        foreach (var (id, endpoint) in diff.Changed)
        {
            // Only a URL change needs a reconnect; a timeout change is just a knob.
            var paths = ConfigDiff.ChangedProperties(endpoint.Old, endpoint.New);
            if (ConfigDiff.RequiresRestart(typeof(EndpointConfig), paths))
            {
                Disconnect(id);
                Connect(id, endpoint.New);
            }
            else
            {
                System.Console.WriteLine($"  ~ [{id}] updated in place ({string.Join(", ", paths)})");
            }
        }

        foreach (var (id, endpoint) in diff.Added)
            Connect(id, endpoint);

        if (diff.Unchanged.Count > 0)
            System.Console.WriteLine($"  = untouched: {string.Join(", ", diff.Unchanged.Keys)}");

        return Task.CompletedTask;
    }

    public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;

    public Task OnStopAsync()
    {
        foreach (var id in _connections.Keys.ToList())
            Disconnect(id);
        return Task.CompletedTask;
    }

    public Task<HealthStatus> HealthCheckAsync() =>
        Task.FromResult(HealthStatus.Healthy($"{_connections.Count} endpoint(s) connected"));

    public void Dispose() { }

    private void Connect(string id, EndpointConfig endpoint)
    {
        _connections[id] = endpoint.Url;
        System.Console.WriteLine($"  + [{id}] connected to {endpoint.Url}");
    }

    private void Disconnect(string id)
    {
        if (_connections.Remove(id, out var url))
            System.Console.WriteLine($"  - [{id}] disconnected from {url}");
    }
}
