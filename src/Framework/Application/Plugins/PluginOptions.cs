namespace CodeLogic.Framework.Application.Plugins;

/// <summary>
/// Configuration options for the plugin manager.
/// </summary>
public sealed class PluginOptions
{
    /// <summary>Base directory where plugin folders live. Default: {FrameworkRoot}/Plugins</summary>
    public string PluginsDirectory { get; set; } = "CodeLogic/Plugins";

    /// <summary>Enable auto-reload when plugin DLL changes on disk.</summary>
    public bool EnableHotReload { get; set; } = true;

    /// <summary>Enable filesystem watching for DLL changes.</summary>
    public bool WatchForChanges { get; set; } = false;

    /// <summary>Debounce interval to avoid double-reloads on rapid file changes.</summary>
    public TimeSpan ReloadDebounce { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Simple names of assemblies that every plugin shares with the host, in addition to
    /// <c>CodeLogic</c> itself (which is always shared so <see cref="IPlugin"/> has one identity).
    /// Add the contract assemblies whose types cross the host/plugin boundary, for example a
    /// library whose interfaces both sides use. A shared assembly always resolves to the host's copy,
    /// whatever version the plugin ships.
    /// </summary>
    /// <remarks>
    /// Every other dependency resolves from the plugin's own folder (its <c>.deps.json</c>) first,
    /// and falls back to the host only when the plugin does not ship it. Plugins never receive
    /// assemblies from another plugin's load context.
    /// </remarks>
    public ISet<string> SharedAssemblies { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
