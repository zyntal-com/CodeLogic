namespace CodeLogic.Framework.Application.Plugins;

/// <summary>
/// Describes a plugin's identity and framework requirements.
/// Returned by <see cref="IPlugin.Manifest"/> and used by <see cref="PluginManager"/>
/// for tracking, display, and version compatibility checks.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>
    /// Unique identifier for this plugin (e.g., "myapp.dashboard", "homepoint.z-wave").
    /// Used as the key in the PluginManager's internal registry and as the plugin's log file name, so it
    /// must be a single file-name segment (no path separators, <c>..</c> or characters invalid in file names).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable display name (e.g., "Dashboard Plugin").
    /// Shown in console output and health reports.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Semantic version string (e.g., "1.0.0").
    /// Shown in console output when the plugin is loaded.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Optional description of what this plugin provides.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional author or team name.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Minimum CodeLogic framework version required to run this plugin (e.g., "5.0.0").
    /// When set, <see cref="PluginManager"/> refuses to load or register the plugin if the value is not a
    /// semantic version or is newer than <see cref="CodeLogicEnvironment.FrameworkVersion"/> (a pre-release
    /// build such as 5.0.42-preview counts as 5.0.42).
    /// </summary>
    public string? MinFrameworkVersion { get; init; }
}
