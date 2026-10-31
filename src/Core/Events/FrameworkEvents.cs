using CodeLogic.Core.Configuration;

namespace CodeLogic.Core.Events;

/// <summary>
/// Base of every event the framework itself publishes. It is a C# 15 <c>closed</c> record, so a
/// <c>switch</c> over it is exhaustive, and subscribing to <see cref="FrameworkEvent"/> receives all of them:
/// <code>
/// events.Subscribe&lt;FrameworkEvent&gt;(e =&gt; log.Info(e switch
/// {
///     LibraryStartedEvent s =&gt; $"started {s.LibraryName}",
///     LibraryFailedEvent f  =&gt; $"failed {f.LibraryName}: {f.Error.Message}",
///     _                     =&gt; e.ToString(),
/// }));
/// </code>
/// Application and library events implement <see cref="IEvent"/> directly and are unaffected.
/// </summary>
public closed record FrameworkEvent : IEvent;

// ── Lifecycle ─────────────────────────────────────────────────────────────

/// <summary>Published when a library completes OnStartAsync successfully.</summary>
public record LibraryStartedEvent(string LibraryId, string LibraryName) : FrameworkEvent;

/// <summary>Published when a library completes OnStopAsync.</summary>
public record LibraryStoppedEvent(string LibraryId, string LibraryName) : FrameworkEvent;

/// <summary>Published when a library throws during any lifecycle phase.</summary>
public record LibraryFailedEvent(string LibraryId, string LibraryName, Exception Error) : FrameworkEvent;

/// <summary>Published when a plugin is successfully loaded.</summary>
public record PluginLoadedEvent(string PluginId, string PluginName) : FrameworkEvent;

/// <summary>Published when a plugin is unloaded.</summary>
public record PluginUnloadedEvent(string PluginId, string PluginName) : FrameworkEvent;

/// <summary>Published when a plugin throws during load or unload.</summary>
public record PluginFailedEvent(string PluginId, string PluginName, Exception Error) : FrameworkEvent;

// ── Configuration / Localization ──────────────────────────────────────────

/// <summary>Published after a specific config type is reloaded from disk.</summary>
public record ConfigReloadedEvent(string ComponentId, Type ConfigType) : FrameworkEvent;

/// <summary>Published after any committed config change (update, reload, reset) of a library section.</summary>
public record ConfigChangedEvent(
    string ComponentId,
    string SectionName,
    Type ConfigType,
    ConfigChangeKind Kind,
    string? ChangedBy) : FrameworkEvent;

/// <summary>
/// Published when a config change was valid and persisted but the library failed to apply it.
/// The library reports Degraded health until a later change applies successfully.
/// </summary>
public record ConfigApplyFailedEvent(string ComponentId, string SectionName, Exception Error) : FrameworkEvent;

/// <summary>Published after all localizations are reloaded from disk.</summary>
public record LocalizationReloadedEvent(string ComponentId) : FrameworkEvent;

// ── Health ────────────────────────────────────────────────────────────────

/// <summary>Published after a scheduled health check round completes.</summary>
public record HealthCheckCompletedEvent(string ComponentId, bool IsHealthy, string Message) : FrameworkEvent;

// ── Shutdown ──────────────────────────────────────────────────────────────

/// <summary>Published when the framework begins shutting down.</summary>
public record ShutdownRequestedEvent(string Reason) : FrameworkEvent;

// ── Generic bridge — lib-to-lib communication without cross-references ────

/// <summary>
/// Generic bridge event for lib-to-lib signals where a direct type reference
/// would create unwanted coupling. Scope with ComponentId + AlertType.
/// Example: ComponentId="cl.sqlite", AlertType="connection.lost"
/// </summary>
public record ComponentAlertEvent(
    string ComponentId,
    string AlertType,
    string Message,
    object? Payload = null) : FrameworkEvent;
