using CodeLogic.Core.Configuration;
using CodeLogic.Framework.Libraries;

namespace CodeLogic;

/// <summary>
/// Static accessor for loaded libraries.
/// Provides a convenient shorthand over <see cref="CodeLogic.GetLibraryManager()"/>.
/// </summary>
public static class Libraries
{
    /// <summary>
    /// Queues an in-memory mutation for a library configuration section. The mutation runs
    /// after the library's JSON is read and before final validation, so it is never written
    /// back to disk and is visible when the library initializes.
    /// Register overrides after <see cref="CodeLogic.InitializeAsync"/> and no later than
    /// the application's <c>OnConfigureAsync</c> callback.
    /// </summary>
    public static void OverrideConfig<TConfig>(
        string libraryId,
        string sectionName,
        Action<TConfig> apply,
        ConfigOverrideOptions? options = null)
        where TConfig : ConfigModelBase
    {
        var mgr = CodeLogic.GetLibraryManager()
            ?? throw new InvalidOperationException(
                "Library manager not available. Call InitializeAsync() before registering config overrides.");
        mgr.OverrideConfig(libraryId, sectionName, apply, options);
    }

    /// <summary>
    /// Retrieves a loaded library by its concrete type.
    /// Returns null if no library of type <typeparamref name="T"/> is loaded.
    /// </summary>
    /// <typeparam name="T">The library type, which must implement <see cref="ILibrary"/>.</typeparam>
    /// <returns>The library instance, or null if not loaded.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="CodeLogic.ConfigureAsync"/> has not been called yet.
    /// </exception>
    public static T? Get<T>() where T : class, ILibrary
    {
        var mgr = CodeLogic.GetLibraryManager()
            ?? throw new InvalidOperationException("No libraries loaded. Call ConfigureAsync() first.");
        return mgr.GetLibrary<T>();
    }

    /// <summary>
    /// Dynamically loads a library by type and registers it with the LibraryManager.
    /// Call this between <see cref="CodeLogic.InitializeAsync"/> and <see cref="CodeLogic.ConfigureAsync"/>
    /// to register libraries before the configure/start sequence runs.
    /// </summary>
    /// <typeparam name="T">
    /// The library type. Must implement <see cref="ILibrary"/> and have a public parameterless constructor.
    /// </typeparam>
    /// <returns>True if the library was loaded successfully; false if it was already registered.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="CodeLogic.InitializeAsync"/> has not been called yet.
    /// </exception>
    public static async Task<bool> LoadAsync<T>() where T : class, ILibrary, new()
    {
        var mgr = CodeLogic.GetLibraryManager()
            ?? throw new InvalidOperationException(
                "Library manager not available. Call InitializeAsync() first, " +
                "then register libraries before ConfigureAsync().");
        return await mgr.LoadLibraryAsync<T>();
    }

    /// <summary>
    /// Dynamically loads a library with per-library options. The most
    /// common use is to pass <see cref="LibraryLoadOptions.OptionalAtBoot"/> = <see langword="true"/>
    /// for "nice to have" dependencies (mail, social-connect, GeoIP) so a
    /// bad config in one of them doesn't kill the whole application boot.
    /// </summary>
    /// <example>
    /// <code>
    /// // Critical — failure aborts boot:
    /// await Libraries.LoadAsync&lt;MySQL2Library&gt;();
    ///
    /// // Optional — failure is logged but boot continues:
    /// await Libraries.LoadAsync&lt;MailLibrary&gt;(new LibraryLoadOptions { OptionalAtBoot = true });
    /// </code>
    /// </example>
    public static async Task<bool> LoadAsync<T>(LibraryLoadOptions options) where T : class, ILibrary, new()
    {
        var mgr = CodeLogic.GetLibraryManager()
            ?? throw new InvalidOperationException(
                "Library manager not available. Call InitializeAsync() first, " +
                "then register libraries before ConfigureAsync().");
        return await mgr.LoadLibraryAsync<T>(options);
    }

    /// <summary>
    /// Retrieves a loaded library by its string ID (e.g., <c>"CL.SQLite"</c>).
    /// Returns null if no library with that ID is loaded.
    /// </summary>
    /// <param name="libraryId">The library ID as declared in its <see cref="LibraryManifest.Id"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="CodeLogic.ConfigureAsync"/> has not been called yet.
    /// </exception>
    public static ILibrary? Get(string libraryId)
    {
        var mgr = CodeLogic.GetLibraryManager()
            ?? throw new InvalidOperationException("No libraries loaded. Call ConfigureAsync() first.");
        return mgr.GetLibrary(libraryId);
    }

    /// <summary>
    /// Returns all currently loaded libraries.
    /// Safe to call before <see cref="CodeLogic.ConfigureAsync"/> — returns an empty sequence rather than throwing.
    /// Useful for diagnostics or iterating all libraries without knowing their types.
    /// </summary>
    public static IEnumerable<ILibrary> GetAll()
    {
        // Returns empty (not throws) when called before ConfigureAsync —
        // querying all is a safe read, not an indication of programmer error.
        var mgr = CodeLogic.GetLibraryManager();
        return mgr?.GetAllLibraries() ?? [];
    }

    // ── Config discovery shortcuts (3.4.1+) ──────────────────────────────────

    /// <summary>
    /// Every registered configuration section across every loaded library.
    /// Empty list before <see cref="CodeLogic.ConfigureAsync"/> runs.
    /// </summary>
    public static IReadOnlyList<ConfigSectionOverview> AllConfigSections()
    {
        var mgr = CodeLogic.GetLibraryManager();
        return mgr?.GetAllConfigSections() ?? [];
    }

    /// <summary>
    /// Schema + current values for a specific library's section. Returns null
    /// if the library or section isn't registered.
    /// </summary>
    /// <param name="libraryId">The <see cref="LibraryManifest.Id"/> (e.g., <c>"CL.MySQL2"</c>).</param>
    /// <param name="sectionName">The section sub-name (empty string for the root <c>config.json</c>).</param>
    /// <param name="includeSecrets">When true, returns raw values; default masks fields marked <see cref="ConfigFieldAttribute.Secret"/>.</param>
    public static ConfigSchema? GetConfigSchema(string libraryId, string sectionName, bool includeSecrets = false)
    {
        var mgr = CodeLogic.GetLibraryManager();
        return mgr?.GetConfigSchema(libraryId, sectionName, includeSecrets);
    }

    /// <summary>
    /// Update a library's config section from a raw JSON payload. File is untouched when
    /// validation fails; valid changes are persisted and applied live by libraries that
    /// implement <see cref="IConfigReloadable"/>.
    /// </summary>
    public static Task<ConfigApplyResult> UpdateConfigAsync(
        string libraryId,
        string sectionName,
        string json,
        string? changedBy = null,
        CancellationToken ct = default)
    {
        var mgr = CodeLogic.GetLibraryManager()
            ?? throw new InvalidOperationException("Libraries not configured yet. Call ConfigureAsync() first.");
        return mgr.UpdateConfigAsync(libraryId, sectionName, json, changedBy, ct);
    }

    /// <summary>Reset a library's config section to defaults and apply it.</summary>
    public static Task<ConfigApplyResult> ResetConfigAsync(
        string libraryId,
        string sectionName,
        string? changedBy = null,
        CancellationToken ct = default)
    {
        var mgr = CodeLogic.GetLibraryManager()
            ?? throw new InvalidOperationException("Libraries not configured yet. Call ConfigureAsync() first.");
        return mgr.ResetConfigAsync(libraryId, sectionName, changedBy, ct);
    }

    /// <summary>
    /// Re-read a library's config from disk (one section, or all when <paramref name="sectionName"/>
    /// is null) and apply it. Invalid files keep the current values.
    /// </summary>
    public static Task<ConfigApplyResult> ReloadConfigAsync(
        string libraryId,
        string? sectionName = null,
        string? changedBy = null,
        CancellationToken ct = default)
    {
        var mgr = CodeLogic.GetLibraryManager()
            ?? throw new InvalidOperationException("Libraries not configured yet. Call ConfigureAsync() first.");
        return mgr.ReloadConfigAsync(libraryId, sectionName, changedBy, ct);
    }
}
