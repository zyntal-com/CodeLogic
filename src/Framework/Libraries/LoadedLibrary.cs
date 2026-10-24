namespace CodeLogic.Framework.Libraries;

/// <summary>
/// Internal record of a library registered with LibraryManager.
/// </summary>
public sealed class LoadedLibrary
{
    /// <summary>The library instance.</summary>
    public required ILibrary Instance { get; init; }
    /// <summary>The library manifest describing its identity and dependencies.</summary>
    public required LibraryManifest Manifest { get; init; }
    /// <summary>Filesystem path the library assembly was loaded from.</summary>
    public required string AssemblyPath { get; init; }
    /// <summary>Timestamp when the library was loaded.</summary>
    public DateTime LoadedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Context assigned after OnConfigureAsync completes.</summary>
    public LibraryContext? Context { get; set; }

    /// <summary>Current lifecycle state.</summary>
    public LibraryState State { get; set; } = LibraryState.Loaded;

    /// <summary>Exception if State == Failed.</summary>
    public Exception? FailureException { get; set; }

    /// <summary>
    /// When <see langword="true"/>, Configure / Initialize / Start
    /// failures are tolerated — the library is marked
    /// <see cref="LibraryState.Failed"/> and the boot continues without
    /// it. Set by <see cref="LibraryLoadOptions.OptionalAtBoot"/>.
    /// </summary>
    public bool IsOptional { get; init; }

    /// <summary>
    /// Set when the last config change was persisted but the library failed to apply it
    /// (see <see cref="IConfigReloadable"/>). Cleared by the next successful apply.
    /// While set, the library's health is reported as at most Degraded.
    /// </summary>
    public Exception? ConfigApplyError { get; internal set; }
}
