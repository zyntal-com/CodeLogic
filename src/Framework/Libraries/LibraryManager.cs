using System.Collections.Concurrent;
using System.Reflection;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Core.Localization;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Utilities;

namespace CodeLogic.Framework.Libraries;

/// <summary>
/// Manages the full lifecycle of all registered CL.* libraries.
/// Thread-safe: lifecycle methods are serialized via SemaphoreSlim.
/// </summary>
public sealed class LibraryManager : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<LoadedLibrary> _libraries = [];
    private readonly Dictionary<string, ILibrary> _librariesById = [];
    private readonly IEventBus _eventBus;
    private readonly object _configOverrideLock = new();
    private readonly List<ConfigOverrideRegistration> _configOverrides = [];
    private bool _configurationStarted;
    private FileSystemWatcher? _configWatcher;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _configReloadDebounce =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Logging options applied to library loggers.</summary>
    public LoggingOptions LoggingOptions { get; set; } = new();
    /// <summary>Root path of the CodeLogic framework directory.</summary>
    public string FrameworkRootPath { get; set; } = "CodeLogic";
    /// <summary>Default culture used for localization.</summary>
    public string DefaultCulture { get; set; } = "en-US";
    /// <summary>List of cultures supported for localization.</summary>
    public IReadOnlyList<string> SupportedCultures { get; set; } = ["en-US"];
    /// <summary>Whether to resolve and validate library dependencies.</summary>
    public bool EnableDependencyResolution { get; set; } = true;
    /// <summary>Glob pattern used to discover library directories.</summary>
    public string LibraryDiscoveryPattern { get; set; } = "CL.*";
    /// <summary>When true, <see cref="StartConfigWatcher"/> watches library config files and reloads edited sections.</summary>
    public bool EnableConfigHotReload { get; set; }
    /// <summary>Quiet period after the last file event before a watched config file is reloaded.</summary>
    public TimeSpan ConfigHotReloadDebounce { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Initializes a new library manager with the specified event bus.</summary>
    public LibraryManager(IEventBus eventBus)
    {
        _eventBus = eventBus;
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    /// <summary>
    /// Queues a typed, runtime-only configuration mutation. It must be registered before
    /// library configuration begins; application <c>OnConfigureAsync</c> is the intended hook.
    /// </summary>
    public void OverrideConfig<TConfig>(
        string libraryId,
        string sectionName,
        Action<TConfig> apply,
        ConfigOverrideOptions? options = null)
        where TConfig : ConfigModelBase
    {
        if (string.IsNullOrWhiteSpace(libraryId))
            throw new ArgumentException("Library ID cannot be empty.", nameof(libraryId));
        ArgumentNullException.ThrowIfNull(sectionName);
        ArgumentNullException.ThrowIfNull(apply);

        lock (_configOverrideLock)
        {
            if (_configurationStarted)
                throw new InvalidOperationException(
                    "Runtime config overrides must be registered before ConfigureAsync begins. " +
                    "Changing a library after it has started requires a restart.");

            _configOverrides.Add(new ConfigOverrideRegistration(
                libraryId,
                sectionName,
                typeof(TConfig),
                config => apply((TConfig)config),
                options?.FailureMode ?? ConfigOverrideFailureMode.Strict));
        }
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    /// <summary>Discovers library DLLs matching the discovery pattern in the Libraries directory.</summary>
    public List<string> Discover()
    {
        var librariesRoot = Path.Combine(FrameworkRootPath, "Libraries");
        var paths = new List<string>();

        if (!Directory.Exists(librariesRoot)) return paths;

        var dirs = Directory.GetDirectories(librariesRoot, LibraryDiscoveryPattern, SearchOption.TopDirectoryOnly);
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            var dll  = Path.Combine(dir, $"{name}.dll");
            if (File.Exists(dll)) paths.Add(dll);
        }
        return paths;
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Manually registers a library by type.
    /// Dependency validation is deferred to ConfigureAllAsync which runs
    /// ValidateDependencies after all libraries are registered.
    /// </summary>
    public Task<bool> LoadLibraryAsync<T>() where T : class, ILibrary, new() =>
        LoadLibraryAsync<T>(options: null);

    /// <summary>
    /// Manually registers a library by type with load-time options.
    /// Pass <see cref="LibraryLoadOptions.OptionalAtBoot"/> = <see langword="true"/>
    /// to mark the library "nice to have" — a Configure / Initialize / Start
    /// failure will be logged + the library marked <see cref="LibraryState.Failed"/>,
    /// but the application boots without it (other libraries continue
    /// configuring/initializing as normal).
    /// </summary>
    public async Task<bool> LoadLibraryAsync<T>(LibraryLoadOptions? options) where T : class, ILibrary, new()
    {
        await _lock.WaitAsync();
        try
        {
            var library  = new T();
            var manifest = library.Manifest;

            if (_librariesById.ContainsKey(manifest.Id))
            {
                Console.WriteLine($"  ⚠ Library '{manifest.Name}' already loaded");
                return false;
            }

            _libraries.Add(new LoadedLibrary
            {
                Instance     = library,
                Manifest     = manifest,
                AssemblyPath = typeof(T).Assembly.Location,
                IsOptional   = options?.OptionalAtBoot ?? false
            });
            _librariesById[manifest.Id] = library;

            var optionalTag = options?.OptionalAtBoot == true ? " (optional)" : "";
            Console.WriteLine($"  ✓ Manually loaded: {manifest.Name} v{manifest.Version}{optionalTag}");
            return true;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Loads libraries from the specified assembly paths.</summary>
    public async Task LoadLibrariesAsync(IReadOnlyList<string> libraryPaths)
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var path in libraryPaths)
            {
                try
                {
                    var assembly = Assembly.LoadFrom(path);
                    var type     = FindLibraryType(assembly);
                    if (type == null)
                    {
                        Console.WriteLine($"  ⚠ No ILibrary found in {Path.GetFileName(path)}");
                        continue;
                    }

                    var library  = (ILibrary)Activator.CreateInstance(type)!;
                    var manifest = library.Manifest;

                    if (_librariesById.ContainsKey(manifest.Id))
                        throw new InvalidOperationException($"Duplicate library ID '{manifest.Id}'");

                    _libraries.Add(new LoadedLibrary
                    {
                        Instance = library, Manifest = manifest, AssemblyPath = path
                    });
                    _librariesById[manifest.Id] = library;
                    Console.WriteLine($"  ✓ Discovered: {manifest.Name} v{manifest.Version}");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Failed to load {Path.GetFileName(path)}: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }
        finally { _lock.Release(); }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures all loaded libraries in dependency order, optionally generating missing config files.
    /// </summary>
    /// <param name="generateMissingConfigs">When true, generates default config files for libraries that don't have one.</param>
    /// <param name="forceGenerateConfigs">When true, overwrites existing config files with defaults.</param>
    /// <param name="configScopeToIds">When set, limits config generation to the specified library IDs.</param>
    /// <param name="dryRun">When true, validates configuration without applying changes.</param>
    public async Task ConfigureAllAsync(
        bool generateMissingConfigs = true,
        bool forceGenerateConfigs = false,
        IReadOnlyCollection<string>? configScopeToIds = null,
        bool dryRun = false)
    {
        await _lock.WaitAsync();
        try
        {
            lock (_configOverrideLock)
                _configurationStarted = true;

            ValidateDependencies(GetOrderedLibraries());

            foreach (var loaded in GetOrderedLibraries())
            {
                if (loaded.State != LibraryState.Loaded) continue;

                try
                {
                    var ctx = CreateContext(loaded.Manifest.Id);
                    await loaded.Instance.OnConfigureAsync(ctx);

                    var shouldApplyConfigGeneration = configScopeToIds == null
                        || configScopeToIds.Count == 0
                        || configScopeToIds.Contains(loaded.Manifest.Id, StringComparer.OrdinalIgnoreCase);
                    var allowMissingConfigs = shouldApplyConfigGeneration && (generateMissingConfigs || forceGenerateConfigs);
                    var configuration = GetConfigurationManager(ctx);

                    if (dryRun)
                    {
                        if (shouldApplyConfigGeneration)
                            ReportDryRunConfigActions(configuration, forceGenerateConfigs);

                        if (!(forceGenerateConfigs && shouldApplyConfigGeneration))
                            await configuration.ValidateAllAsync(allowMissingFiles: allowMissingConfigs);

                        await ctx.Localization.LoadAllAsync(SupportedCultures, generateIfMissing: false);
                    }
                    else
                    {
                        if (forceGenerateConfigs && shouldApplyConfigGeneration)
                            await ctx.Configuration.GenerateAllDefaultsAsync(force: true);

                        var owner = loaded;
                        configuration.ChangedAsync = (change, ct) => OnLibraryConfigChangedAsync(owner, change, ct);

                        await configuration.LoadAllWithRuntimeOverridesAsync(
                            allowMissingConfigs,
                            (type, section, config) => ApplyConfigOverrides(
                                loaded.Manifest.Id, section, type, config, configuration, ctx.Logger));
                        await ctx.Localization.GenerateAllTemplatesAsync(SupportedCultures);
                        await ctx.Localization.LoadAllAsync(SupportedCultures);
                    }

                    loaded.Context = ctx;
                    loaded.State   = LibraryState.Configured;
                    Console.WriteLine($"  ✓ Configured: {loaded.Manifest.Name}");
                }
                catch (Exception ex)
                {
                    loaded.State            = LibraryState.Failed;
                    loaded.FailureException = ex;
                    _eventBus.Publish(new LibraryFailedEvent(loaded.Manifest.Id, loaded.Manifest.Name, ex));
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Failed to configure {loaded.Manifest.Name}: {ex.Message}");
                    if (loaded.IsOptional)
                        Console.WriteLine($"     (optional — boot continues without this library)");
                    Console.ResetColor();
                    if (!loaded.IsOptional)
                        throw;
                }
            }

            if (!dryRun)
                ValidateUnresolvedConfigOverrides();
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Initializes all configured libraries in dependency order.
    /// </summary>
    public async Task InitializeAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var loaded in GetOrderedLibraries())
            {
                if (loaded.State == LibraryState.Failed) continue;
                if (loaded.State != LibraryState.Configured)
                    throw new InvalidOperationException(
                        $"Library '{loaded.Manifest.Name}' must be Configured before Initialize. State: {loaded.State}");

                try
                {
                    await loaded.Instance.OnInitializeAsync(loaded.Context!);
                    loaded.State = LibraryState.Initialized;
                    Console.WriteLine($"  ✓ Initialized: {loaded.Manifest.Name}");
                }
                catch (Exception ex)
                {
                    loaded.State = LibraryState.Failed;
                    loaded.FailureException = ex;
                    _eventBus.Publish(new LibraryFailedEvent(loaded.Manifest.Id, loaded.Manifest.Name, ex));
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Failed to initialize {loaded.Manifest.Name}: {ex.Message}");
                    if (loaded.IsOptional)
                        Console.WriteLine($"     (optional — boot continues without this library)");
                    Console.ResetColor();
                    if (!loaded.IsOptional)
                        throw;
                }
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Starts all initialized libraries in dependency order.
    /// </summary>
    public async Task StartAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var loaded in GetOrderedLibraries())
            {
                if (loaded.State == LibraryState.Failed) continue;
                if (loaded.State != LibraryState.Initialized)
                    throw new InvalidOperationException(
                        $"Library '{loaded.Manifest.Name}' must be Initialized before Start. State: {loaded.State}");

                try
                {
                    await loaded.Instance.OnStartAsync(loaded.Context!);
                    loaded.State = LibraryState.Started;
                    _eventBus.Publish(new LibraryStartedEvent(loaded.Manifest.Id, loaded.Manifest.Name));
                    Console.WriteLine($"  ✓ Started: {loaded.Manifest.Name}");
                }
                catch (Exception ex)
                {
                    loaded.State = LibraryState.Failed;
                    loaded.FailureException = ex;
                    _eventBus.Publish(new LibraryFailedEvent(loaded.Manifest.Id, loaded.Manifest.Name, ex));
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Failed to start {loaded.Manifest.Name}: {ex.Message}");
                    if (loaded.IsOptional)
                        Console.WriteLine($"     (optional — boot continues without this library)");
                    Console.ResetColor();
                    if (!loaded.IsOptional)
                        throw;
                }
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Stops all started libraries in reverse dependency order.
    /// </summary>
    public async Task StopAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var loaded in GetOrderedLibraries(reverse: true))
            {
                if (loaded.State != LibraryState.Started) continue;
                try
                {
                    await loaded.Instance.OnStopAsync();
                    loaded.State = LibraryState.Stopped;
                    _eventBus.Publish(new LibraryStoppedEvent(loaded.Manifest.Id, loaded.Manifest.Name));
                    Console.WriteLine($"  ✓ Stopped: {loaded.Manifest.Name}");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗ Error stopping {loaded.Manifest.Name}: {ex.Message}");
                    Console.ResetColor();
                    // Don't rethrow on stop — try to stop everything
                }
            }
        }
        finally { _lock.Release(); }
    }

    // ── Health checks ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs health checks on all started libraries and returns their status.
    /// </summary>
    public async Task<Dictionary<string, HealthStatus>> GetHealthAsync()
    {
        var results = new Dictionary<string, HealthStatus>();
        foreach (var loaded in _libraries.Where(l => l.State == LibraryState.Started))
        {
            try
            {
                var status = await loaded.Instance.HealthCheckAsync();
                if (loaded.ConfigApplyError is { } applyError && status.IsHealthy)
                    status = HealthStatus.Degraded($"Config change not applied: {applyError.Message}");
                results[loaded.Manifest.Id] = status;
            }
            catch (Exception ex)
            {
                results[loaded.Manifest.Id] = HealthStatus.FromException(ex);
            }
        }
        return results;
    }

    /// <summary>
    /// Force-regenerates config defaults for all configured libraries, overwriting existing files.
    /// Only runs for libraries that have already completed their Configure phase (Context is set).
    /// Pass an empty array to include all libraries; pass specific IDs to scope generation.
    /// </summary>
    public async Task ForceRegenerateAllConfigsAsync(string[] scopeToIds)
    {
        foreach (var loaded in _libraries.Where(l => l.Context != null))
        {
            if (scopeToIds.Length > 0 &&
                !scopeToIds.Any(id => string.Equals(id, loaded.Manifest.Id, StringComparison.OrdinalIgnoreCase)))
                continue;

            await loaded.Context!.Configuration.GenerateAllDefaultsAsync(force: true);
        }
    }

    // ── Accessors ────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets a loaded library instance by type.
    /// </summary>
    public T? GetLibrary<T>() where T : class, ILibrary =>
        _libraries.Select(l => l.Instance as T).FirstOrDefault(l => l != null);

    /// <summary>
    /// Gets a loaded library instance by its manifest ID.
    /// </summary>
    public ILibrary? GetLibrary(string id) =>
        _librariesById.GetValueOrDefault(id);

    /// <summary>
    /// Returns all registered library instances.
    /// </summary>
    public IEnumerable<ILibrary> GetAllLibraries() =>
        _libraries.Select(l => l.Instance);

    /// <summary>
    /// Returns all loaded library records including state and context.
    /// </summary>
    public IEnumerable<LoadedLibrary> GetLoadedLibraries() => _libraries.AsReadOnly();

    // ── Config discovery (3.4.1+) ────────────────────────────────────────────

    /// <summary>
    /// Enumerate every registered configuration section across every loaded
    /// library. Used by admin UIs and CLI tools to list editable settings.
    /// </summary>
    public IReadOnlyList<ConfigSectionOverview> GetAllConfigSections()
    {
        var result = new List<ConfigSectionOverview>();
        foreach (var lib in _libraries)
        {
            if (lib.Context is null) continue; // Pre-configure phase
            IReadOnlyList<ConfigSectionInfo> sections;
            try { sections = lib.Context.Configuration.GetSections(); }
            catch { continue; }

            foreach (var s in sections)
            {
                result.Add(new ConfigSectionOverview
                {
                    LibraryId   = lib.Manifest.Id,
                    LibraryName = lib.Manifest.Name,
                    SectionName = s.SectionName,
                    Title       = s.Title,
                    Description = s.Description,
                    FilePath    = s.FilePath,
                    FileExists  = s.FileExists,
                    IsLoaded    = s.IsLoaded
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Build the <see cref="ConfigSchema"/> for a specific library's section.
    /// Returns null if the library or section isn't registered.
    /// </summary>
    /// <param name="libraryId">The <see cref="LibraryManifest.Id"/>.</param>
    /// <param name="sectionName">The section sub-name (empty string for the root <c>config.json</c>).</param>
    /// <param name="includeSecrets">When true, return raw values; default masks fields marked <see cref="ConfigFieldAttribute.Secret"/>.</param>
    public ConfigSchema? GetConfigSchema(string libraryId, string sectionName, bool includeSecrets = false)
    {
        var lib = _libraries.FirstOrDefault(l =>
            string.Equals(l.Manifest.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        if (lib?.Context is null) return null;
        try { return lib.Context.Configuration.GetSchema(sectionName, includeSecrets); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Update a library's config section from a raw JSON payload.
    /// File is untouched when validation fails. When the values are valid they are persisted and,
    /// if the library implements <see cref="IConfigReloadable"/>, applied live —
    /// <see cref="ConfigApplyResult.ApplyError"/> reports a failed apply (the file is kept).
    /// </summary>
    public async Task<ConfigApplyResult> UpdateConfigAsync(
        string libraryId,
        string sectionName,
        string json,
        string? changedBy = null,
        CancellationToken ct = default)
    {
        var lib = FindLoadedLibrary(libraryId);
        if (lib?.Context is null)
            return ConfigApplyResult.Invalid(ConfigValidationResult.Invalid($"Library '{libraryId}' is not loaded."));

        try
        {
            var validation = await lib.Context.Configuration
                .UpdateSectionAsync(sectionName, json, changedBy, ct)
                .ConfigureAwait(false);
            return validation.IsValid ? ConfigApplyResult.Success() : ConfigApplyResult.Invalid(validation);
        }
        catch (ConfigApplyException ex)
        {
            return ConfigApplyResult.Failed(ex.InnerException ?? ex);
        }
    }

    /// <summary>Reset a library's config section to defaults and apply it like <see cref="UpdateConfigAsync"/>.</summary>
    public async Task<ConfigApplyResult> ResetConfigAsync(
        string libraryId,
        string sectionName,
        string? changedBy = null,
        CancellationToken ct = default)
    {
        var lib = FindLoadedLibrary(libraryId);
        if (lib?.Context is null)
            throw new InvalidOperationException($"Library '{libraryId}' is not loaded.");

        try
        {
            await lib.Context.Configuration
                .ResetSectionAsync(sectionName, changedBy, ct)
                .ConfigureAwait(false);
            return ConfigApplyResult.Success();
        }
        catch (ConfigApplyException ex)
        {
            return ConfigApplyResult.Failed(ex.InnerException ?? ex);
        }
    }

    /// <summary>
    /// Re-read a library's config from disk (one section, or all when <paramref name="sectionName"/>
    /// is null), re-apply runtime overrides, and apply it like <see cref="UpdateConfigAsync"/>.
    /// An invalid or missing file keeps the current values and is reported as invalid.
    /// </summary>
    public async Task<ConfigApplyResult> ReloadConfigAsync(
        string libraryId,
        string? sectionName = null,
        string? changedBy = null,
        CancellationToken ct = default)
    {
        var lib = FindLoadedLibrary(libraryId);
        if (lib?.Context is null)
            return ConfigApplyResult.Invalid(ConfigValidationResult.Invalid($"Library '{libraryId}' is not loaded."));

        var configuration = GetConfigurationManager(lib.Context);
        try
        {
            var validation = sectionName is null
                ? await configuration.ReloadAllSectionsAsync(changedBy, ct).ConfigureAwait(false)
                : await configuration.ReloadSectionAsync(
                    configuration.ResolveTypeForSection(sectionName), changedBy, ct).ConfigureAwait(false);
            return validation.IsValid ? ConfigApplyResult.Success() : ConfigApplyResult.Invalid(validation);
        }
        catch (ConfigApplyException ex)
        {
            return ConfigApplyResult.Failed(ex.InnerException ?? ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            return ConfigApplyResult.Invalid(ConfigValidationResult.Invalid(ex.Message));
        }
    }

    // ── Config hot reload (4.9.0+) ───────────────────────────────────────────

    /// <summary>
    /// Starts watching <c>Libraries/*/config*.json</c> when <see cref="EnableConfigHotReload"/> is true.
    /// Edited sections are reloaded via <see cref="ReloadConfigAsync"/> after <see cref="ConfigHotReloadDebounce"/>.
    /// Invalid edits are logged and the current values kept. Safe to call more than once.
    /// </summary>
    public void StartConfigWatcher()
    {
        if (!EnableConfigHotReload || _configWatcher is not null) return;

        var root = Path.Combine(FrameworkRootPath, "Libraries");
        Directory.CreateDirectory(root);
        _configWatcher = new FileSystemWatcher(root, "config*.json")
        {
            NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = true
        };
        _configWatcher.Changed += (_, e) => ScheduleConfigReload(e.FullPath);
        _configWatcher.Created += (_, e) => ScheduleConfigReload(e.FullPath);
        _configWatcher.Renamed += (_, e) => ScheduleConfigReload(e.FullPath);
        _configWatcher.EnableRaisingEvents = true;
    }

    private void ScheduleConfigReload(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;

        // Trailing debounce: every event restarts the quiet period for that file.
        var cts = new CancellationTokenSource();
        _configReloadDebounce.AddOrUpdate(path, cts, (_, previous) => { previous.Cancel(); return cts; });

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ConfigHotReloadDebounce, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            _configReloadDebounce.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, cts));
            try { await ReloadChangedConfigFileAsync(path).ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[LibraryManager] Config watcher error: {ex.Message}"); }
        });
    }

    private async Task ReloadChangedConfigFileAsync(string path)
    {
        foreach (var lib in _libraries.ToList())
        {
            if (lib.Context?.Configuration is not ConfigurationManager configuration) continue;
            var type = configuration.FindTypeForFile(path);
            if (type is null) continue;

            var content = await ReadFileWithRetryAsync(path).ConfigureAwait(false);
            if (content is null || configuration.IsKnownContent(path, content))
                return; // deleted, or our own write / an unchanged file

            var section = configuration.GetSectionName(type);
            var result = await ReloadConfigAsync(lib.Manifest.Id, section, changedBy: "file-watcher").ConfigureAwait(false);
            if (!result.IsValid)
                lib.Context.Logger.Warning(
                    $"Config file '{Path.GetFileName(path)}' changed but was not reloaded (current values kept): {result}");
            else if (result.Applied)
                lib.Context.Logger.Info($"Config file '{Path.GetFileName(path)}' reloaded");
            return;
        }
    }

    private static async Task<string?> ReadFileWithRetryAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(path).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(100).ConfigureAwait(false); // editor still holds the file
            }
        }
    }

    private async Task OnLibraryConfigChangedAsync(LoadedLibrary loaded, ConfigChangedEventArgs change, CancellationToken ct)
    {
        var ctx = loaded.Context;
        if (ctx is null || change.ConfigType is null) return;

        try
        {
            // Before Initialize the library reads config itself; nothing to apply yet.
            if (loaded.State is not (LibraryState.Initialized or LibraryState.Started)) return;

            if (loaded.Instance is not IConfigReloadable reloadable)
            {
                var changed = ConfigDiff.ChangedProperties(change.OldValue, change.NewValue);
                if (ConfigDiff.RequiresRestart(change.ConfigType, changed))
                    ctx.Logger.Warning(
                        $"Config section '{DisplaySection(change.SectionName)}' changed settings that require a restart: " +
                        string.Join(", ", changed));
                return;
            }

            try
            {
                await reloadable.OnConfigChangedAsync(ctx, change, ct).ConfigureAwait(false);
                if (loaded.ConfigApplyError is not null)
                {
                    loaded.ConfigApplyError = null;
                    ctx.Logger.Info($"Config section '{DisplaySection(change.SectionName)}' applied; previous apply error cleared");
                }
            }
            catch (Exception ex)
            {
                loaded.ConfigApplyError = ex;
                ctx.Logger.Error($"Config section '{DisplaySection(change.SectionName)}' was saved but could not be applied", ex);
                _eventBus.Publish(new ConfigApplyFailedEvent(loaded.Manifest.Id, change.SectionName, ex));
                throw;
            }
        }
        finally
        {
            _eventBus.Publish(new ConfigChangedEvent(
                loaded.Manifest.Id, change.SectionName, change.ConfigType, change.Kind, change.ChangedBy));
            if (change.Kind == ConfigChangeKind.Reloaded)
                _eventBus.Publish(new ConfigReloadedEvent(loaded.Manifest.Id, change.ConfigType));
        }
    }

    private static string DisplaySection(string sectionName) =>
        string.IsNullOrEmpty(sectionName) ? "(main)" : sectionName;

    private LoadedLibrary? FindLoadedLibrary(string libraryId) =>
        _libraries.FirstOrDefault(l =>
            string.Equals(l.Manifest.Id, libraryId, StringComparison.OrdinalIgnoreCase));

    // ── Private helpers ──────────────────────────────────────────────────────

    private LibraryContext CreateContext(string libraryId)
    {
        var root    = Path.Combine(FrameworkRootPath, "Libraries");
        var libDir  = Path.Combine(root, NormalizeId(libraryId));
        var logsDir = Path.Combine(libDir, "logs");
        var dataDir = Path.Combine(libDir, "data");
        var locDir  = Path.Combine(libDir, "localization");

        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(locDir);

        var logger = new Logger(
            libraryId.ToUpper(), logsDir, LoggingOptions.GlobalLevel, LoggingOptions);

        return new LibraryContext
        {
            LibraryId             = libraryId,
            LibraryDirectory      = libDir,
            ConfigDirectory       = libDir,
            LocalizationDirectory = locDir,
            LogsDirectory         = logsDir,
            DataDirectory         = dataDir,
            Logger                = logger,
            Configuration         = new ConfigurationManager(libDir),
            Localization          = new LocalizationManager(locDir, DefaultCulture),
            Events                = _eventBus
        };
    }

    private static ConfigurationManager GetConfigurationManager(LibraryContext context) =>
        context.Configuration as ConfigurationManager
        ?? throw new InvalidOperationException(
            "Library configuration manager must be CodeLogic.Core.Configuration.ConfigurationManager.");

    private ConfigModelBase ApplyConfigOverrides(
        string libraryId,
        string sectionName,
        Type configType,
        ConfigModelBase jsonConfig,
        ConfigurationManager configuration,
        ILogger logger)
    {
        var current = jsonConfig;
        List<ConfigOverrideRegistration> matching;
        lock (_configOverrideLock)
        {
            matching = _configOverrides
                .Where(o => string.Equals(o.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(o.SectionName, sectionName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var registration in matching)
                registration.TargetWasObserved = true;
        }

        foreach (var registration in matching)
        {
            if (registration.ConfigType != configType)
            {
                HandleConfigOverrideFailure(
                    registration,
                    $"section '{sectionName}' is registered as '{configType.Name}', not '{registration.ConfigType.Name}'.",
                    logger);
                continue;
            }

            try
            {
                // The callback receives a clone. Failed callbacks and validation therefore
                // cannot alter the JSON-derived instance or a previously accepted override.
                var working = configuration.CloneForRuntimeOverride(current, configType);
                registration.Apply(working);
                var validation = working.Validate();
                if (!validation.IsValid)
                    throw new InvalidOperationException($"result is invalid: {validation}");

                current = working;
            }
            catch (Exception ex)
            {
                HandleConfigOverrideFailure(registration, "callback or validation failed", logger, ex);
            }
        }

        return current;
    }

    private void ValidateUnresolvedConfigOverrides()
    {
        List<ConfigOverrideRegistration> unresolved;
        lock (_configOverrideLock)
            unresolved = _configOverrides.Where(o => !o.TargetWasObserved).ToList();

        foreach (var registration in unresolved)
        {
            var libraryKnown = _libraries.Any(l => string.Equals(
                l.Manifest.Id, registration.LibraryId, StringComparison.OrdinalIgnoreCase));
            var reason = libraryKnown
                ? $"section '{registration.SectionName}' was not registered by library '{registration.LibraryId}'."
                : $"library '{registration.LibraryId}' was not loaded.";
            HandleConfigOverrideFailure(registration, reason, logger: null);
        }
    }

    private static void HandleConfigOverrideFailure(
        ConfigOverrideRegistration registration,
        string reason,
        ILogger? logger,
        Exception? exception = null)
    {
        var message = $"Runtime config override for library '{registration.LibraryId}', " +
            $"section '{registration.SectionName}' failed: {reason}";
        if (registration.FailureMode == ConfigOverrideFailureMode.Ignore)
        {
            if (logger is not null)
                logger.Warning(message);
            else
                Console.Error.WriteLine($"  ⚠ {message}");
            return;
        }

        throw new InvalidOperationException(message, exception);
    }

    private sealed class ConfigOverrideRegistration(
        string libraryId,
        string sectionName,
        Type configType,
        Action<ConfigModelBase> apply,
        ConfigOverrideFailureMode failureMode)
    {
        public string LibraryId { get; } = libraryId;
        public string SectionName { get; } = sectionName;
        public Type ConfigType { get; } = configType;
        public Action<ConfigModelBase> Apply { get; } = apply;
        public ConfigOverrideFailureMode FailureMode { get; } = failureMode;
        public bool TargetWasObserved { get; set; }
    }

    private static void ReportDryRunConfigActions(ConfigurationManager configuration, bool force)
    {
        foreach (var path in configuration.GetRegisteredFilePaths())
        {
            if (force)
            {
                var action = File.Exists(path) ? "overwrite" : "generate";
                Console.WriteLine($"[dry-run] Would {action}: {GetDisplayPath(path)}");
            }
            else if (!File.Exists(path))
            {
                Console.WriteLine($"[dry-run] Would generate: {GetDisplayPath(path)}");
            }
        }
    }

    private static string GetDisplayPath(string path) =>
        Path.GetRelativePath(AppContext.BaseDirectory, path);

    private List<LoadedLibrary> GetOrderedLibraries(bool reverse = false)
    {
        var ordered = EnableDependencyResolution
            ? TopologicalSort()
            : _libraries.ToList();
        if (reverse) ordered.Reverse();
        return ordered;
    }

    private List<LoadedLibrary> TopologicalSort()
    {
        var sorted   = new List<LoadedLibrary>();
        var visited  = new HashSet<string>();
        var visiting = new HashSet<string>();

        foreach (var lib in _libraries)
            Visit(lib, sorted, visited, visiting);

        return sorted;
    }

    private void Visit(LoadedLibrary lib, List<LoadedLibrary> sorted,
        HashSet<string> visited, HashSet<string> visiting)
    {
        if (visited.Contains(lib.Manifest.Id)) return;
        if (visiting.Contains(lib.Manifest.Id))
            throw new InvalidOperationException($"Circular dependency: {lib.Manifest.Id}");

        visiting.Add(lib.Manifest.Id);

        foreach (var dep in lib.Manifest.Dependencies)
        {
            var depLib = _libraries.FirstOrDefault(l => l.Manifest.Id == dep.Id);
            if (depLib == null && dep.IsOptional) continue;
            if (depLib != null) Visit(depLib, sorted, visited, visiting);
        }

        visiting.Remove(lib.Manifest.Id);
        visited.Add(lib.Manifest.Id);
        sorted.Add(lib);
    }

    private void ValidateDependencies(IEnumerable<LoadedLibrary> ordered)
    {
        var errors = new List<string>();
        foreach (var lib in ordered)
        {
            foreach (var dep in lib.Manifest.Dependencies)
            {
                var found = _libraries.FirstOrDefault(l => l.Manifest.Id == dep.Id);
                if (found == null)
                {
                    if (!dep.IsOptional)
                        errors.Add($"'{lib.Manifest.Name}' requires missing dependency '{dep.Id}'" +
                            (dep.MinVersion != null ? $" >= {dep.MinVersion}" : ""));
                    continue;
                }

                if (dep.MinVersion != null &&
                    SemanticVersion.TryParse(found.Manifest.Version, out var installed) &&
                    SemanticVersion.TryParse(dep.MinVersion, out var required) &&
                    installed != null && required != null &&
                    installed < required)
                {
                    errors.Add($"'{lib.Manifest.Name}' requires '{dep.Id}' >= {dep.MinVersion}, " +
                        $"but {installed} is loaded");
                }
            }
        }
        if (errors.Count > 0)
            throw new InvalidOperationException("Dependency validation failed:\n  " + string.Join("\n  ", errors));
    }

    private static string NormalizeId(string id) =>
        id.StartsWith("CL.", StringComparison.OrdinalIgnoreCase) ? $"CL.{id[3..]}" : $"CL.{id}";

    private static Type? FindLibraryType(Assembly assembly) =>
        assembly.GetTypes().FirstOrDefault(t =>
            typeof(ILibrary).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

    private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name);
        foreach (var lib in _libraries)
        {
            var dir  = Path.GetDirectoryName(lib.AssemblyPath);
            if (dir == null) continue;
            var path = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
        _configWatcher?.Dispose();
        _configWatcher = null;
        foreach (var cts in _configReloadDebounce.Values)
            cts.Cancel();
        _configReloadDebounce.Clear();
    }
}
