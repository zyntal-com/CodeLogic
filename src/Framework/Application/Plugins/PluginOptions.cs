namespace CodeLogic.Framework.Application.Plugins;

/// <summary>
/// Configuration options for the plugin manager.
/// </summary>
public sealed class PluginOptions
{
    /// <summary>The default value of <see cref="PluginsDirectory"/>, meaning <c>{FrameworkRoot}/Plugins</c>.</summary>
    public const string DefaultPluginsDirectory = "CodeLogic/Plugins";

    /// <summary>
    /// Base directory where plugin folders live. Default: <c>{FrameworkRoot}/Plugins</c>.
    /// <para>
    /// The default value follows the initialized runtime's framework root
    /// (<see cref="CodeLogicOptions.GetPluginsPath"/>); before initialization it is
    /// <c>{AppContext.BaseDirectory}/CodeLogic/Plugins</c>. Any other relative path is resolved against
    /// <see cref="AppContext.BaseDirectory"/>, never the current working directory. Plugins run with the host's
    /// permissions, so this directory must be writable only by the account that deploys them (more so with
    /// <see cref="WatchForChanges"/>). <see cref="PluginManager.PluginsDirectory"/> reports the resolved path.
    /// </para>
    /// </summary>
    public string PluginsDirectory { get; set; } = DefaultPluginsDirectory;

    /// <summary>
    /// Whether loading a plugin writes missing localization template files. <see langword="null"/> (default)
    /// follows <c>localization.autoGenerateTemplates</c> of the initialized runtime (true when there is none).
    /// When false, missing cultures use the models' built-in strings in memory.
    /// </summary>
    public bool? GenerateLocalizationTemplates { get; set; }

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
