using System.Collections.Concurrent;
using System.Reflection;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Core.Localization;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Utilities;
using CodeLogic.Framework.Libraries;

namespace CodeLogic.Framework.Application.Plugins;

/// <summary>
/// Manages the full lifecycle of dynamically loaded plugins.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Lifecycle operations (load, register, unload, reload, dispose and hot reload) are serialized:
/// one runs at a time, so they never overlap for the same or different plugins.</item>
/// <item>Readers (<see cref="GetPlugin{T}"/>, <see cref="GetAllPlugins"/>, <see cref="GetLoadedPlugins"/>,
/// <see cref="GetHealthAsync"/>) never block and work on a snapshot.</item>
/// <item>Plugin IDs are unique. Loading a plugin whose ID is already loaded fails without starting it;
/// use <see cref="ReloadPluginAsync"/> or unload the running one first.</item>
/// <item>Lifecycle methods (load, unload, reload, unload-all, register, dispose) called from code that runs
/// inside an operation in progress — a plugin's <c>OnConfigureAsync</c>/<c>OnInitializeAsync</c>/
/// <c>OnStartAsync</c>/<c>OnUnloadAsync</c>/<c>Dispose</c>, or an <see cref="OnPluginError"/> handler — throw
/// <see cref="InvalidOperationException"/> immediately instead of deadlocking. The same applies to work those
/// callbacks start, if it calls back while that operation is still running. <see cref="OnPluginLoaded"/> and
/// <see cref="OnPluginUnloaded"/> handlers run after the lock is released and may call lifecycle methods.</item>
/// </list>
/// </remarks>
public sealed class PluginManager : IAsyncDisposable
{
    private readonly IEventBus _eventBus;
    private readonly PluginOptions _options;
    private readonly LoggingOptions _loggingOptions;
    private readonly string _defaultCulture;
    private readonly IReadOnlyList<string> _supportedCultures;
    private readonly ConcurrentDictionary<string, LoadedPlugin> _plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _reloadDebounce = new(PathComparison.Comparer);
    private readonly Lock _reloadLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private volatile bool _disposed;

    // Identifies the lifecycle operation that currently holds _lifecycleLock. A plugin callback that
    // calls back into the manager runs in the same async flow, carries the same token, and would
    // otherwise wait for itself forever. Per instance: with a shared marker, a callback of manager A that
    // calls manager B would overwrite A's marker, and a callback of B calling back into A would then wait
    // on A's lock (held by A's operation, which is waiting on B) instead of failing fast.
    private readonly AsyncLocal<object?> _flowToken = new();
    private object? _activeToken;

    /// <summary>Raised when a plugin is successfully loaded.</summary>
    public event Action<string>? OnPluginLoaded;
    /// <summary>Raised when a plugin is unloaded.</summary>
    public event Action<string>? OnPluginUnloaded;
    /// <summary>Raised when a plugin encounters an error during load, unload, or reload.</summary>
    public event Action<string, Exception>? OnPluginError;

    /// <summary>Initializes a new plugin manager with the specified dependencies.</summary>
    public PluginManager(
        IEventBus eventBus,
        PluginOptions? options = null,
        LoggingOptions? loggingOptions = null,
        string defaultCulture = "en-US",
        IReadOnlyList<string>? supportedCultures = null)
    {
        _eventBus          = eventBus;
        _options           = options ?? new PluginOptions();
        _loggingOptions    = loggingOptions ?? new LoggingOptions();
        _defaultCulture    = defaultCulture;
        _supportedCultures = supportedCultures ?? ["en-US"];

        Directory.CreateDirectory(_options.PluginsDirectory);

        if (_options.WatchForChanges)
            StartWatcher();
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    /// <summary>Discovers plugin DLLs in the configured plugins directory.</summary>
    public Task<List<string>> DiscoverAsync()
    {
        var paths = new List<string>();
        if (!Directory.Exists(_options.PluginsDirectory)) return Task.FromResult(paths);

        foreach (var dir in Directory.GetDirectories(_options.PluginsDirectory, "*.Plugin"))
        {
            var name = Path.GetFileName(dir);
            var dll  = Path.Combine(dir, $"{name}.dll");
            if (File.Exists(dll)) paths.Add(dll);
        }
        return Task.FromResult(paths);
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    /// <summary>Loads and starts a single plugin from the specified assembly path.</summary>
    /// <exception cref="FileNotFoundException">The assembly does not exist.</exception>
    /// <exception cref="InvalidOperationException">
    /// The assembly has no <see cref="IPlugin"/>, a plugin with the same ID is already loaded, or the
    /// call was made from inside a plugin lifecycle callback or <see cref="OnPluginError"/> handler of an
    /// operation in progress. The other lifecycle methods throw it in that case too.
    /// </exception>
    /// <remarks>
    /// When any phase fails, the plugin is cleaned up before the exception is rethrown: a plugin whose
    /// <see cref="IPlugin.OnStartAsync"/> was entered gets <see cref="IPlugin.OnUnloadAsync"/>, every
    /// created instance is disposed, and its load context is unloaded.
    /// </remarks>
    public async Task LoadPluginAsync(string pluginPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginPath);
        if (!File.Exists(pluginPath))
            throw new FileNotFoundException($"Plugin not found: {pluginPath}");

        string? loadedId;
        var previousFlow = MarkFlow(await AcquireLifecycleAsync().ConfigureAwait(false));
        try
        {
            loadedId = await LoadCoreAsync(pluginPath).ConfigureAwait(false);
        }
        finally { ExitLifecycle(previousFlow); }

        NotifyLoaded(loadedId);
    }

    /// <summary>Discovers and loads all plugins from the plugins directory.</summary>
    public async Task LoadAllAsync()
    {
        var paths = await DiscoverAsync().ConfigureAwait(false);
        foreach (var path in paths)
        {
            try { await LoadPluginAsync(path).ConfigureAwait(false); }
            catch { /* already reported through PluginFailedEvent / OnPluginError */ }
        }
    }

    /// <summary>
    /// Registers an in-process plugin (already instantiated and running) with the PluginManager
    /// so that it participates in health checks, <see cref="GetLoadedPlugins"/>, and graceful
    /// shutdown via <see cref="UnloadAllAsync"/>.
    /// <para>
    /// Use this when hosting a plugin in the same process/assembly (e.g. demos, tests) rather
    /// than loading it from a separate DLL via <see cref="LoadPluginAsync"/>. The caller is
    /// responsible for running the full 4-phase lifecycle before calling this method.
    /// A plugin whose ID is already registered is ignored.
    /// </para>
    /// </summary>
    /// <param name="plugin">The already-started plugin instance to register.</param>
    /// <param name="context">The <see cref="PluginContext"/> the plugin was started with.</param>
    public async Task RegisterInProcessPluginAsync(IPlugin plugin, PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);
        var manifest = plugin.Manifest;

        var previousFlow = MarkFlow(await AcquireLifecycleAsync().ConfigureAwait(false));
        try
        {
            if (_plugins.ContainsKey(manifest.Id))
            {
                Console.WriteLine($"  ! Plugin '{manifest.Name}' already registered");
                return;
            }

            // In-process plugins don't have their own AssemblyLoadContext — use a no-op sentinel.
            var noOpLoadCtx = new PluginLoadContext(typeof(PluginManager).Assembly.Location);

            _plugins[manifest.Id] = new LoadedPlugin
            {
                Instance      = plugin,
                Manifest      = manifest,
                AssemblyPath  = typeof(PluginManager).Assembly.Location,
                LoadContext   = noOpLoadCtx,
                WeakReference = new WeakReference(noOpLoadCtx),
                Context       = context,
                State         = PluginState.Started
            };
        }
        finally { ExitLifecycle(previousFlow); }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  + Plugin registered (in-process): {manifest.Name} v{manifest.Version}");
        Console.ResetColor();
        NotifyLoaded(manifest.Id);
    }

    // ── Unload ───────────────────────────────────────────────────────────────

    /// <summary>Unloads a plugin by its manifest ID and releases its assembly.</summary>
    /// <remarks>
    /// The plugin is always removed, disposed and its load context unloaded, even when
    /// <see cref="IPlugin.OnUnloadAsync"/> throws. Such a failure is reported through
    /// <see cref="PluginFailedEvent"/> and <see cref="OnPluginError"/>; it is not rethrown.
    /// </remarks>
    public async Task UnloadPluginAsync(string pluginId)
    {
        LoadedPlugin? unloaded;
        var previousFlow = MarkFlow(await AcquireLifecycleAsync().ConfigureAwait(false));
        try
        {
            unloaded = await UnloadCoreAsync(pluginId).ConfigureAwait(false);
        }
        finally { ExitLifecycle(previousFlow); }

        NotifyUnloaded(unloaded);
    }

    /// <summary>Unloads all currently loaded plugins.</summary>
    public async Task UnloadAllAsync()
    {
        List<LoadedPlugin> unloaded;
        var previousFlow = MarkFlow(await AcquireLifecycleAsync().ConfigureAwait(false));
        try
        {
            unloaded = await UnloadAllCoreAsync().ConfigureAwait(false);
        }
        finally { ExitLifecycle(previousFlow); }

        foreach (var plugin in unloaded)
            NotifyUnloaded(plugin);
    }

    /// <summary>Unloads and reloads a plugin from disk.</summary>
    /// <exception cref="InvalidOperationException">The plugin is not loaded.</exception>
    public async Task ReloadPluginAsync(string pluginId)
    {
        LoadedPlugin? unloaded = null;
        string? loadedId;
        var previousFlow = MarkFlow(await AcquireLifecycleAsync().ConfigureAwait(false));
        try
        {
            if (!_plugins.TryGetValue(pluginId, out var loaded))
                throw new InvalidOperationException($"Plugin '{pluginId}' not loaded");

            var path = loaded.AssemblyPath;
            unloaded = await UnloadCoreAsync(pluginId).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false); // brief pause for file system
            loadedId = await LoadCoreAsync(path).ConfigureAwait(false);
        }
        finally
        {
            ExitLifecycle(previousFlow);
            NotifyUnloaded(unloaded);
        }

        NotifyLoaded(loadedId);
    }

    // ── Accessors ────────────────────────────────────────────────────────────

    /// <summary>Gets a loaded plugin instance cast to the specified type.</summary>
    public T? GetPlugin<T>(string pluginId) where T : class, IPlugin =>
        _plugins.TryGetValue(pluginId, out var p) ? p.Instance as T : null;

    /// <summary>Returns a snapshot of all loaded plugin instances.</summary>
    public IEnumerable<IPlugin> GetAllPlugins() => [.. _plugins.Values.Select(p => p.Instance)];
    /// <summary>Returns a snapshot of all loaded plugin records including state and metadata.</summary>
    public IEnumerable<LoadedPlugin> GetLoadedPlugins() => [.. _plugins.Values];

    /// <summary>Runs health checks on all started plugins and returns their status.</summary>
    public async Task<Dictionary<string, HealthStatus>> GetHealthAsync()
    {
        var results = new Dictionary<string, HealthStatus>();
        foreach (var (id, loaded) in _plugins.ToArray())
        {
            if (loaded.State != PluginState.Started) continue;
            try { results[id] = await loaded.Instance.HealthCheckAsync().ConfigureAwait(false); }
            catch (Exception ex) { results[id] = HealthStatus.FromException(ex); }
        }
        return results;
    }

    // ── Lifecycle core (caller holds _lifecycleLock) ─────────────────────────

    private async Task<string?> LoadCoreAsync(string pluginPath)
    {
        PluginLoadContext? loadCtx = null;
        IPlugin? plugin = null;
        var startEntered = false;
        string? pluginId = null;

        try
        {
            loadCtx = new PluginLoadContext(pluginPath, _options.SharedAssemblies);
            var assembly   = loadCtx.LoadFromAssemblyPath(Path.GetFullPath(pluginPath));
            var pluginType = FindPluginType(assembly)
                ?? throw new InvalidOperationException($"No IPlugin in {Path.GetFileName(pluginPath)}");

            plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
            var manifest = plugin.Manifest;
            pluginId = manifest.Id;

            // Reject before anything is started: the running instance stays untouched.
            if (_plugins.TryGetValue(manifest.Id, out var existing))
                throw new InvalidOperationException(
                    $"Plugin '{manifest.Id}' is already loaded from '{existing.AssemblyPath}'. " +
                    "Unload it first or use ReloadPluginAsync.");

            var ctx = CreateContext(manifest.Id, pluginPath);

            // Full 4-phase lifecycle
            await plugin.OnConfigureAsync(ctx).ConfigureAwait(false);
            await ctx.Configuration.GenerateAllDefaultsAsync().ConfigureAwait(false);
            await ctx.Configuration.LoadAllAsync().ConfigureAwait(false);
            await ctx.Localization.GenerateAllTemplatesAsync(_supportedCultures).ConfigureAwait(false);
            await ctx.Localization.LoadAllAsync(_supportedCultures).ConfigureAwait(false);

            await plugin.OnInitializeAsync(ctx).ConfigureAwait(false);
            startEntered = true;
            await plugin.OnStartAsync(ctx).ConfigureAwait(false);

            _plugins[manifest.Id] = new LoadedPlugin
            {
                Instance      = plugin,
                Manifest      = manifest,
                AssemblyPath  = pluginPath,
                LoadContext   = loadCtx,
                WeakReference = new WeakReference(loadCtx),
                Context       = ctx,
                State         = PluginState.Started
            };

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  + Plugin loaded: {manifest.Name} v{manifest.Version}");
            Console.ResetColor();
            return manifest.Id;
        }
        catch (Exception ex)
        {
            await CleanupFailedLoadAsync(plugin, loadCtx, startEntered, pluginId ?? pluginPath).ConfigureAwait(false);

            _eventBus.Publish(new PluginFailedEvent(pluginId ?? Path.GetFileName(pluginPath), pluginPath, ex));
            RaiseError(pluginPath, ex);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  x Plugin load failed {Path.GetFileName(pluginPath)}: {ex.Message}");
            Console.ResetColor();
            throw;
        }
    }

    private static async Task CleanupFailedLoadAsync(IPlugin? plugin, PluginLoadContext? loadCtx, bool startEntered, string label)
    {
        if (plugin is not null)
        {
            // OnStartAsync may have subscribed to events or started work before it failed.
            if (startEntered)
            {
                try { await plugin.OnUnloadAsync().ConfigureAwait(false); }
                catch (Exception ex) { ReportCleanupError(label, "OnUnloadAsync", ex); }
            }

            try { plugin.Dispose(); }
            catch (Exception ex) { ReportCleanupError(label, "Dispose", ex); }
        }

        try { loadCtx?.Unload(); }
        catch (Exception ex) { ReportCleanupError(label, "load context unload", ex); }
    }

    private async Task<List<LoadedPlugin>> UnloadAllCoreAsync()
    {
        List<LoadedPlugin> unloaded = [];
        foreach (var id in _plugins.Keys.ToList())
            if (await UnloadCoreAsync(id).ConfigureAwait(false) is { } plugin)
                unloaded.Add(plugin);
        return unloaded;
    }

    private async Task<LoadedPlugin?> UnloadCoreAsync(string pluginId)
    {
        if (!_plugins.TryRemove(pluginId, out var loaded))
        {
            Console.WriteLine($"  ! Plugin '{pluginId}' not loaded");
            return null;
        }

        Exception? failure = null;
        try { await loaded.Instance.OnUnloadAsync().ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }

        // Cleanup runs whatever the unload hook did.
        try { loaded.Instance.Dispose(); }
        catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }

        try { loaded.LoadContext.Unload(); }
        catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }

        if (failure is not null)
        {
            loaded.State            = PluginState.Failed;
            loaded.FailureException = failure;
            _eventBus.Publish(new PluginFailedEvent(pluginId, loaded.Manifest.Name, failure));
            RaiseError(pluginId, failure);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  x Plugin unload error '{pluginId}': {failure.Message}");
            Console.ResetColor();
        }
        else
        {
            loaded.State = PluginState.Stopped;
        }

        // No forced GC here. The unload completes once nothing references the plugin's types any more;
        // the returned LoadedPlugin record itself still references the context, so blocking collections
        // under the lifecycle lock could never finish it and only stalled the process.
        Console.WriteLine($"  + Plugin unloaded: {loaded.Manifest.Name}");
        return loaded;
    }

    // ── Lifecycle serialization ──────────────────────────────────────────────

    // Every public lifecycle method follows the same pattern:
    //
    //     var previousFlow = MarkFlow(await AcquireLifecycleAsync().ConfigureAwait(false));
    //     try { ... } finally { ExitLifecycle(previousFlow); }
    //
    // The AsyncLocal marker must be set by the synchronous MarkFlow in the public method's own frame:
    // a value set inside an awaited async helper is discarded when that helper returns, so plugin
    // callbacks and error handlers invoked afterwards would not inherit it, and a callback that awaited
    // another lifecycle call would wait on the semaphore it is itself holding.

    private void ThrowIfReentrant()
    {
        var flow = _flowToken.Value;
        if (flow is not null && ReferenceEquals(flow, Volatile.Read(ref _activeToken)))
            throw new InvalidOperationException(
                "PluginManager lifecycle methods cannot be called from inside a plugin lifecycle callback " +
                "or a PluginManager event handler raised by the operation in progress.");
    }

    /// <summary>Waits for the lifecycle lock and returns the token identifying this operation.</summary>
    private async Task<object> AcquireLifecycleAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfReentrant();

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        if (_disposed)
        {
            _lifecycleLock.Release();
            throw new ObjectDisposedException(nameof(PluginManager));
        }

        var token = new object();
        Volatile.Write(ref _activeToken, token);
        return token;
    }

    /// <summary>
    /// Marks the caller's execution context as running lifecycle operation <paramref name="token"/>.
    /// Deliberately not async, so the change stays in the caller's context. Returns the previous marker.
    /// </summary>
    private object? MarkFlow(object token)
    {
        var previous = _flowToken.Value;
        _flowToken.Value = token;
        return previous;
    }

    /// <summary>Restores the caller's previous marker and releases the lifecycle lock.</summary>
    private void ExitLifecycle(object? previousFlow)
    {
        Volatile.Write(ref _activeToken, null);
        _flowToken.Value = previousFlow;
        _lifecycleLock.Release();
    }

    // ── Notifications (never treated as plugin failures) ─────────────────────

    private void NotifyLoaded(string? pluginId)
    {
        if (pluginId is null) return;
        if (_plugins.TryGetValue(pluginId, out var loaded))
            _eventBus.Publish(new PluginLoadedEvent(pluginId, loaded.Manifest.Name));
        InvokeHandler(OnPluginLoaded, pluginId, nameof(OnPluginLoaded));
    }

    private void NotifyUnloaded(LoadedPlugin? plugin)
    {
        if (plugin is null) return;
        _eventBus.Publish(new PluginUnloadedEvent(plugin.Manifest.Id, plugin.Manifest.Name));
        InvokeHandler(OnPluginUnloaded, plugin.Manifest.Id, nameof(OnPluginUnloaded));
    }

    private static void InvokeHandler(Action<string>? handlers, string pluginId, string name)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try { handler(pluginId); }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[PluginManager] {name} handler failed for '{pluginId}': {ex.Message}"); }
                catch { /* ignore */ }
            }
        }
    }

    private void RaiseError(string key, Exception error)
    {
        if (OnPluginError is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string, Exception>>())
        {
            try { handler(key, error); }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[PluginManager] OnPluginError handler failed for '{key}': {ex.Message}"); }
                catch { /* ignore */ }
            }
        }
    }

    private static void ReportCleanupError(string label, string step, Exception ex)
    {
        try { Console.Error.WriteLine($"[PluginManager] Cleanup of '{label}' failed in {step}: {ex.Message}"); }
        catch { /* ignore */ }
    }

    // ── File watcher (hot reload) ─────────────────────────────────────────────

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_options.PluginsDirectory, "*.dll")
        {
            NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName,
            IncludeSubdirectories = true,
            EnableRaisingEvents   = true
        };
        _watcher.Changed += (_, e) => _ = SafeOnFileChangedAsync(e.FullPath);
        _watcher.Created += (_, e) => _ = SafeOnFileChangedAsync(e.FullPath);
        _watcher.Renamed += (_, e) => _ = SafeOnFileChangedAsync(e.FullPath);
    }

    private async Task SafeOnFileChangedAsync(string filePath)
    {
        try { await OnFileChangedAsync(filePath).ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* manager disposed while the change was pending */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PluginManager] File watcher error: {ex.Message}");
            RaiseError(filePath, ex);
        }
    }

    private async Task OnFileChangedAsync(string filePath)
    {
        if (!_options.EnableHotReload || _disposed) return;
        if (!filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return;

        var pluginDir = Path.GetDirectoryName(filePath);
        if (pluginDir == null || !pluginDir.EndsWith(".Plugin", StringComparison.OrdinalIgnoreCase)) return;

        // Debounce
        lock (_reloadLock)
        {
            var now = DateTime.UtcNow;
            if (_reloadDebounce.TryGetValue(filePath, out var last) && now - last < _options.ReloadDebounce)
                return;
            _reloadDebounce[filePath] = now;
        }

        await Task.Delay(250).ConfigureAwait(false); // wait for file write to complete

        var fullPath = Path.GetFullPath(filePath);
        var plugin = _plugins.Values.FirstOrDefault(p =>
            PathComparison.Comparer.Equals(Path.GetFullPath(p.AssemblyPath), fullPath));
        if (plugin == null) return;

        Console.WriteLine($"  ~ Hot-reload: {Path.GetFileName(filePath)}");
        try { await ReloadPluginAsync(plugin.Manifest.Id).ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* shutting down */ }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  x Hot-reload failed: {ex.Message}");
            Console.ResetColor();
        }
    }

    // ── Context creation ─────────────────────────────────────────────────────

    private PluginContext CreateContext(string pluginId, string pluginPath)
    {
        var pluginDir = Path.GetDirectoryName(Path.GetFullPath(pluginPath))!;
        var logsDir   = Path.Combine(pluginDir, "logs");
        var dataDir   = Path.Combine(pluginDir, "data");
        var locDir    = Path.Combine(pluginDir, "localization");

        Directory.CreateDirectory(logsDir);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(locDir);

        var logger = new Logger(pluginId.ToUpperInvariant(), logsDir, _loggingOptions.GlobalLevel, _loggingOptions);

        return new PluginContext
        {
            PluginId              = pluginId,
            PluginDirectory       = pluginDir,
            ConfigDirectory       = pluginDir,
            LocalizationDirectory = locDir,
            LogsDirectory         = logsDir,
            DataDirectory         = dataDir,
            Logger                = logger,
            Configuration         = new ConfigurationManager(pluginDir),
            Localization          = new LocalizationManager(locDir, _defaultCulture),
            Events                = _eventBus
        };
    }

    private static Type? FindPluginType(Assembly assembly) =>
        assembly.GetTypes().FirstOrDefault(t =>
            typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

    // ── Disposal ─────────────────────────────────────────────────────────────

    /// <summary>Stops watching for changes and unloads every plugin. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        ThrowIfReentrant(); // before touching the watcher
        _watcher?.Dispose();
        _watcher = null;

        List<LoadedPlugin> unloaded;
        object token;
        try { token = await AcquireLifecycleAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; } // a concurrent DisposeAsync finished first
        var previousFlow = MarkFlow(token);
        try
        {
            unloaded = await UnloadAllCoreAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally { ExitLifecycle(previousFlow); }

        foreach (var plugin in unloaded)
            NotifyUnloaded(plugin);
    }
}
