using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodeLogic.Core.Configuration;

/// <summary>
/// File-based JSON configuration manager for a single component.
/// Thread-safe: reads are lock-free, changes (save / reload / update / reset) are
/// serialized, files are written atomically, and change notifications are delivered
/// in the order the changes were committed.
/// </summary>
public sealed class ConfigurationManager : IConfigurationManager
{
    private readonly string _baseDirectory;
    private readonly ConcurrentDictionary<Type, ConfigModelBase> _loaded = new();   // effective values
    private readonly ConcurrentDictionary<Type, SectionState> _state = new();       // file vs effective snapshot
    private readonly Dictionary<Type, string> _registered = []; // Type → subConfigName
    private readonly ConcurrentDictionary<string, string> _knownContent = new(Utilities.PathComparison.Comparer); // path → hash
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _notifyGate = new();
    private Task _notifyTail = Task.CompletedTask;

    // Set while change handlers run, so a handler that saves config again doesn't wait on itself.
    private static readonly AsyncLocal<ConfigurationManager?> _notifying = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static JsonSerializerOptions JsonOptions => _jsonOptions;

    /// <inheritdoc />
    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

    /// <summary>
    /// Runtime-only overrides applied on top of the file values every time a section is
    /// loaded or changed. Set by the framework (library <c>OverrideConfig</c>); never persisted.
    /// </summary>
    internal Func<Type, string, ConfigModelBase, ConfigModelBase>? RuntimeOverrides { get; set; }

    /// <summary>
    /// Async change handler awaited after <see cref="ConfigChanged"/> for every committed change.
    /// Exceptions surface to the caller as <see cref="ConfigApplyException"/>.
    /// </summary>
    internal Func<ConfigChangedEventArgs, CancellationToken, Task>? ChangedAsync { get; set; }

    /// <summary>Initializes the configuration manager with the specified base directory.</summary>
    public ConfigurationManager(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        Directory.CreateDirectory(baseDirectory);
    }

    /// <inheritdoc />
    public void Register<T>(string? subConfigName = null) where T : ConfigModelBase, new()
    {
        _registered[typeof(T)] = subConfigName ?? string.Empty;
    }

    /// <inheritdoc />
    public T Get<T>() where T : ConfigModelBase, new()
    {
        if (_loaded.TryGetValue(typeof(T), out var config))
            return (T)config;

        throw new InvalidOperationException(
            $"Configuration '{typeof(T).Name}' is not loaded. " +
            $"Call LoadAsync<{typeof(T).Name}>() or LoadAllAsync() first.");
    }

    /// <inheritdoc />
    public Task GenerateDefaultAsync<T>(bool force = false) where T : ConfigModelBase, new() =>
        GenerateDefaultAsync(typeof(T), force);

    /// <inheritdoc />
    public Task LoadAsync<T>(bool generateIfMissing = true) where T : ConfigModelBase, new() =>
        LoadAsync(typeof(T), generateIfMissing);

    /// <inheritdoc />
    public async Task SaveAsync<T>(T config) where T : ConfigModelBase, new()
    {
        ArgumentNullException.ThrowIfNull(config);
        var type = typeof(T);
        ConfigChangedEventArgs args;
        Task notification;

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Validate BEFORE writing — never persist invalid config
            var prepared = Prepare(type, StripRuntimeOverrides(type, config));
            if (!prepared.Validation.IsValid)
                throw new InvalidOperationException(
                    $"Cannot save invalid configuration '{type.Name}': {prepared.Validation}");

            await WriteFileAsync(GetFilePath(type), prepared.State!.PersistedJson, CancellationToken.None).ConfigureAwait(false);
            args = Commit(prepared, ConfigChangeKind.Updated, changedBy: null);
            notification = EnqueueNotification(args, CancellationToken.None);
        }
        finally { _writeLock.Release(); }

        await AwaitNotificationAsync(notification, args).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task GenerateAllDefaultsAsync(bool force = false)
    {
        foreach (var type in _registered.Keys)
            await GenerateDefaultAsync(type, force).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task LoadAllAsync(bool generateIfMissing = true)
    {
        foreach (var type in _registered.Keys)
            await LoadAsync(type, generateIfMissing).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads all registered models with runtime overrides applied on top of the file values.
    /// The overrides stay attached, so later reloads and updates re-apply them.
    /// This is deliberately internal: component contracts continue to expose the normal configuration API.
    /// </summary>
    internal Task LoadAllWithRuntimeOverridesAsync(
        bool generateIfMissing,
        Func<Type, string, ConfigModelBase, ConfigModelBase> applyOverrides)
    {
        ArgumentNullException.ThrowIfNull(applyOverrides);
        RuntimeOverrides = applyOverrides;
        return LoadAllAsync(generateIfMissing);
    }

    /// <summary>Creates an independent working copy for an atomic runtime override.</summary>
    internal ConfigModelBase CloneForRuntimeOverride(ConfigModelBase config, Type type) =>
        Deserialize(JsonSerializer.Serialize(config, type, _jsonOptions), type, "runtime override clone");

    /// <inheritdoc />
    public async Task ValidateAllAsync(bool allowMissingFiles = false)
    {
        foreach (var type in _registered.Keys)
        {
            var path = GetFilePath(type);
            if (!File.Exists(path))
            {
                if (allowMissingFiles)
                    continue;

                throw new FileNotFoundException(
                    $"Config file for '{type.Name}' not found at: {path}");
            }

            await LoadAsync(type, generateIfMissing: false).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ReloadAsync<T>() where T : ConfigModelBase, new()
    {
        var result = await ReloadSectionAsync(typeof(T), changedBy: null).ConfigureAwait(false);
        if (!result.IsValid)
            throw new InvalidOperationException(
                $"Reloaded configuration '{typeof(T).Name}' is invalid: {result}");
    }

    /// <inheritdoc />
    public async Task ReloadAllAsync()
    {
        var result = await ReloadAllSectionsAsync(changedBy: null).ConfigureAwait(false);
        if (!result.IsValid)
            throw new InvalidOperationException($"Reloaded configuration is invalid: {result}");
    }

    /// <summary>
    /// Re-reads one section from disk, re-applies runtime overrides, and notifies handlers.
    /// Returns an invalid result (and keeps the current values) when the file is malformed or invalid.
    /// Throws <see cref="ConfigApplyException"/> when a change handler fails.
    /// </summary>
    internal async Task<ConfigValidationResult> ReloadSectionAsync(Type type, string? changedBy, CancellationToken ct = default)
    {
        ConfigChangedEventArgs args;
        Task notification;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prepared = await TryPrepareFromDiskAsync(type, ct).ConfigureAwait(false);
            if (!prepared.Validation.IsValid)
                return prepared.Validation;

            args = Commit(prepared, ConfigChangeKind.Reloaded, changedBy);
            notification = EnqueueNotification(args, ct);
        }
        finally { _writeLock.Release(); }

        await AwaitNotificationAsync(notification, args).ConfigureAwait(false);
        return ConfigValidationResult.Valid();
    }

    /// <summary>
    /// Re-reads every registered section. All files are validated first; if any is invalid,
    /// nothing is committed and the combined errors are returned.
    /// </summary>
    internal async Task<ConfigValidationResult> ReloadAllSectionsAsync(string? changedBy, CancellationToken ct = default)
    {
        var pending = new List<(ConfigChangedEventArgs Args, Task Notification)>();

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prepared = new List<PreparedSection>();
            var errors = new List<string>();
            foreach (var type in _registered.Keys)
            {
                var section = await TryPrepareFromDiskAsync(type, ct).ConfigureAwait(false);
                if (section.Validation.IsValid)
                    prepared.Add(section);
                else
                    errors.AddRange(section.Validation.Errors.Select(e => $"{type.Name}: {e}"));
            }

            if (errors.Count > 0)
                return ConfigValidationResult.Invalid(errors);

            foreach (var section in prepared)
            {
                var args = Commit(section, ConfigChangeKind.Reloaded, changedBy);
                pending.Add((args, EnqueueNotification(args, ct)));
            }
        }
        finally { _writeLock.Release(); }

        ConfigApplyException? firstFailure = null;
        foreach (var (args, notification) in pending)
        {
            try { await AwaitNotificationAsync(notification, args).ConfigureAwait(false); }
            catch (ConfigApplyException ex) { firstFailure ??= ex; }
        }
        if (firstFailure is not null)
            throw firstFailure;

        return ConfigValidationResult.Valid();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetRegisteredFilePaths() =>
        _registered.Keys.Select(GetFilePath).ToList();

    // ─── 3.3.0 additions ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<ConfigSectionInfo> GetSections()
    {
        List<ConfigSectionInfo> result = [with(capacity: _registered.Count)];
        foreach (var (type, subName) in _registered)
        {
            var path = GetFilePath(type);
            result.Add(new ConfigSectionInfo
            {
                SectionName = subName,
                Title = type.Name,
                FilePath = path,
                FileExists = File.Exists(path),
                IsLoaded = _loaded.ContainsKey(type)
            });
        }
        return result;
    }

    /// <inheritdoc />
    public ConfigSchema GetSchema(string sectionName, bool includeSecrets = false)
    {
        var type = ResolveTypeForSection(sectionName);
        _loaded.TryGetValue(type, out var instance);
        var schema = ConfigSchemaBuilder.Build(type, sectionName, instance, includeSecrets);

        if (_state.TryGetValue(type, out var state) && state.OverriddenPaths.Count > 0)
        {
            var overridden = state.OverriddenPaths
                .Select(p => string.Join('.', p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var field in schema.Fields)
                field.IsOverridden = overridden.Contains(field.Path);
        }

        return schema;
    }

    /// <inheritdoc />
    public async Task<ConfigValidationResult> UpdateSectionAsync(
        string sectionName,
        string json,
        string? changedBy = null,
        CancellationToken ct = default)
    {
        var type = ResolveTypeForSection(sectionName);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return ConfigValidationResult.Invalid($"Invalid JSON: {ex.Message}");
        }

        ConfigChangedEventArgs args;
        Task notification;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _loaded.TryGetValue(type, out var previous);
            List<string> maskErrors = [];

            // Non-string secrets (numbers, Guids, enums...) are masked with a string, which would not
            // deserialize: put the stored value back on the JSON first.
            if (node is JsonObject root)
                RestoreMaskedTypedSecrets(type, previous, root, "", maskErrors);
            if (maskErrors.Count > 0)
                return ConfigValidationResult.Invalid(maskErrors);

            ConfigModelBase deserialized;
            try
            {
                deserialized = node.Deserialize(type, _jsonOptions) as ConfigModelBase
                    ?? throw new InvalidOperationException("Deserialized config was null.");
            }
            catch (JsonException ex)
            {
                return ConfigValidationResult.Invalid($"Invalid JSON: {ex.Message}");
            }

            // Preserve any masked string secrets by copying from the existing loaded instance.
            if (previous is not null)
            {
                PreserveMaskedSecrets(type, previous, deserialized, maskErrors);
                if (maskErrors.Count > 0)
                    return ConfigValidationResult.Invalid(maskErrors);
            }

            var prepared = Prepare(type, StripRuntimeOverrides(type, deserialized));
            if (!prepared.Validation.IsValid)
                return prepared.Validation;

            await WriteFileAsync(GetFilePath(type), prepared.State!.PersistedJson, ct).ConfigureAwait(false);
            args = Commit(prepared, ConfigChangeKind.Updated, changedBy);
            notification = EnqueueNotification(args, ct);
        }
        finally { _writeLock.Release(); }

        await AwaitNotificationAsync(notification, args).ConfigureAwait(false);
        return ConfigValidationResult.Valid();
    }

    /// <inheritdoc />
    public async Task ResetSectionAsync(string sectionName, string? changedBy = null, CancellationToken ct = default)
    {
        var type = ResolveTypeForSection(sectionName);
        var defaults = Activator.CreateInstance(type) as ConfigModelBase
            ?? throw new InvalidOperationException($"Could not instantiate '{type.Name}' for reset.");

        ConfigChangedEventArgs args;
        Task notification;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Defaults may legitimately be incomplete (e.g. no host yet) — reset never validates.
            var prepared = Prepare(type, defaults, validate: false);
            await WriteFileAsync(GetFilePath(type), prepared.State!.PersistedJson, ct).ConfigureAwait(false);
            args = Commit(prepared, ConfigChangeKind.Reset, changedBy);
            notification = EnqueueNotification(args, ct);
        }
        finally { _writeLock.Release(); }

        await AwaitNotificationAsync(notification, args).ConfigureAwait(false);
    }

    // ─── Framework hooks (file watcher) ───────────────────────────────────────

    /// <summary>Returns the registered type whose file is <paramref name="fullPath"/>, or null.</summary>
    internal Type? FindTypeForFile(string fullPath)
    {
        var target = Path.GetFullPath(fullPath);
        return _registered.Keys.FirstOrDefault(t =>
            string.Equals(Path.GetFullPath(GetFilePath(t)), target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the section name a type was registered under.</summary>
    internal string GetSectionName(Type type) =>
        _registered.TryGetValue(type, out var name) ? name : string.Empty;

    /// <summary>
    /// True when <paramref name="content"/> is exactly what this manager last wrote to or read
    /// from <paramref name="path"/> — used to ignore file-watcher events caused by our own writes.
    /// </summary>
    internal bool IsKnownContent(string path, string content) =>
        _knownContent.TryGetValue(Path.GetFullPath(path), out var hash) && hash == Hash(content);

    // ─── Internals ────────────────────────────────────────────────────────────

    /// <summary>File values vs effective values for one section, as JSON snapshots.</summary>
    private sealed record SectionState(string PersistedJson, string EffectiveJson, IReadOnlyList<string[]> OverriddenPaths);

    private sealed record PreparedSection(Type Type, ConfigModelBase? Effective, SectionState? State, ConfigValidationResult Validation);

    /// <summary>
    /// Applies runtime overrides to <paramref name="persisted"/> and validates the result.
    /// Nothing is stored — call <see cref="Commit"/> once the change should go live.
    /// </summary>
    private PreparedSection Prepare(Type type, ConfigModelBase persisted, bool validate = true)
    {
        var persistedJson = JsonSerializer.Serialize(persisted, type, _jsonOptions);
        var effective = ApplyRuntimeOverrides(type, persisted);

        if (validate)
        {
            var validation = effective.Validate();
            if (!validation.IsValid)
                return new PreparedSection(type, null, null, validation);
        }

        string effectiveJson;
        IReadOnlyList<string[]> overridden;
        if (ReferenceEquals(effective, persisted))
        {
            effectiveJson = persistedJson;
            overridden = [];
        }
        else
        {
            effectiveJson = JsonSerializer.Serialize(effective, type, _jsonOptions);
            overridden = ConfigDiff.DiffPaths(JsonNode.Parse(persistedJson), JsonNode.Parse(effectiveJson));
        }

        return new PreparedSection(type, effective,
            new SectionState(persistedJson, effectiveJson, overridden), ConfigValidationResult.Valid());
    }

    /// <summary>Publishes a prepared section and builds the change event (with a detached old snapshot).</summary>
    private ConfigChangedEventArgs Commit(PreparedSection prepared, ConfigChangeKind kind, string? changedBy)
    {
        var type = prepared.Type;
        var old = _state.TryGetValue(type, out var previous)
            ? Deserialize(previous.EffectiveJson, type, "previous snapshot")
            : null;

        CommitSilently(prepared);

        return new ConfigChangedEventArgs
        {
            SectionName = GetSectionName(type),
            Kind = kind,
            ChangedBy = changedBy,
            ConfigType = type,
            OldValue = old,
            NewValue = prepared.Effective
        };
    }

    private void CommitSilently(PreparedSection prepared)
    {
        _loaded[prepared.Type] = prepared.Effective!;
        _state[prepared.Type] = prepared.State!;
    }

    private ConfigModelBase ApplyRuntimeOverrides(Type type, ConfigModelBase persisted)
    {
        var overrides = RuntimeOverrides;
        if (overrides is null) return persisted;

        // The callback receives a clone so the file values are never mutated.
        var working = CloneForRuntimeOverride(persisted, type);
        var result = overrides(type, GetSectionName(type), working)
            ?? throw new InvalidOperationException(
                $"Runtime override for configuration '{type.Name}' returned null.");
        if (result.GetType() != type)
            throw new InvalidOperationException(
                $"Runtime override for configuration '{type.Name}' returned '{result.GetType().Name}'.");
        return result;
    }

    /// <summary>
    /// Turns an effective-looking instance (e.g. a mutated <c>Get&lt;T&gt;()</c> result or an admin UI
    /// payload) back into file values: every overridden field the caller left at its overridden value
    /// is replaced by the value from the file, so runtime-only overrides never reach disk.
    /// </summary>
    private ConfigModelBase StripRuntimeOverrides(Type type, ConfigModelBase incoming)
    {
        if (!_state.TryGetValue(type, out var state) || state.OverriddenPaths.Count == 0)
            return incoming;

        var node = JsonSerializer.SerializeToNode(incoming, type, _jsonOptions)!;
        var persisted = JsonNode.Parse(state.PersistedJson);
        var effective = JsonNode.Parse(state.EffectiveJson);

        foreach (var path in state.OverriddenPaths)
        {
            if (path.Length == 0) continue;
            if (!JsonNode.DeepEquals(GetAt(node, path), GetAt(effective, path)))
                continue; // the caller explicitly changed this value — keep it

            if (GetAt(node, path[..^1]) is not JsonObject parent) continue;
            var fileValue = GetAt(persisted, path)?.DeepClone();
            if (fileValue is null)
                parent.Remove(path[^1]);
            else
                parent[path[^1]] = fileValue;
        }

        return node.Deserialize(type, _jsonOptions) as ConfigModelBase
            ?? throw new InvalidOperationException($"Failed to rebuild '{type.Name}' without runtime overrides.");
    }

    private static JsonNode? GetAt(JsonNode? node, IEnumerable<string> path)
    {
        foreach (var segment in path)
        {
            if (node is not JsonObject obj) return null;
            node = obj[segment];
        }
        return node;
    }

    private async Task LoadAsync(Type type, bool generateIfMissing)
    {
        var path = GetFilePath(type);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Checked under the write lock so a concurrent save/generate can't slip in between.
            if (!File.Exists(path))
            {
                if (!generateIfMissing)
                    throw new FileNotFoundException(
                        $"Config file for '{type.Name}' not found at: {path}");

                await WriteDefaultCoreAsync(type, path, force: false).ConfigureAwait(false);
            }

            var persisted = Deserialize(await ReadFileAsync(path, CancellationToken.None).ConfigureAwait(false), type, path);
            var prepared = Prepare(type, persisted);
            if (!prepared.Validation.IsValid)
                throw new InvalidOperationException(
                    $"Configuration '{type.Name}' is invalid: {prepared.Validation}");

            CommitSilently(prepared);
        }
        finally { _writeLock.Release(); }
    }

    private async Task<PreparedSection> TryPrepareFromDiskAsync(Type type, CancellationToken ct)
    {
        var path = GetFilePath(type);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Config file for '{type.Name}' not found at: {path}. " +
                $"Call GenerateDefaultAsync<{type.Name}>() first.");

        ConfigModelBase persisted;
        try
        {
            persisted = Deserialize(await ReadFileAsync(path, ct).ConfigureAwait(false), type, path);
        }
        catch (JsonException ex)
        {
            return new PreparedSection(type, null, null, ConfigValidationResult.Invalid($"Invalid JSON: {ex.Message}"));
        }

        return Prepare(type, persisted);
    }

    private async Task GenerateDefaultAsync(Type type, bool force)
    {
        var path = GetFilePath(type);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await WriteDefaultCoreAsync(type, path, force).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Writes the defaults file. The caller holds <see cref="_writeLock"/>.</summary>
    private async Task WriteDefaultCoreAsync(Type type, string path, bool force)
    {
        if (!force && File.Exists(path)) return;

        var defaults = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not instantiate '{type.Name}'.");
        await WriteFileAsync(path, JsonSerializer.Serialize(defaults, type, _jsonOptions), CancellationToken.None)
            .ConfigureAwait(false);
    }

    // ─── Notifications ────────────────────────────────────────────────────────

    /// <summary>
    /// Queues handlers for a committed change. Must be called while holding the write lock so
    /// notifications run in commit order. Handlers always run on the thread pool, never inline.
    /// </summary>
    private Task EnqueueNotification(ConfigChangedEventArgs args, CancellationToken ct)
    {
        lock (_notifyGate)
        {
            var task = _notifyTail
                .ContinueWith(_ => RunHandlersAsync(args, ct), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
            _notifyTail = task.ContinueWith(static _ => { }, TaskScheduler.Default);
            return task;
        }
    }

    private async Task RunHandlersAsync(ConfigChangedEventArgs args, CancellationToken ct)
    {
        var outer = _notifying.Value;
        _notifying.Value = this;
        try
        {
            ConfigChanged?.Invoke(this, args);
            if (ChangedAsync is { } handler)
                await handler(args, ct).ConfigureAwait(false);
        }
        finally { _notifying.Value = outer; }
    }

    private async Task AwaitNotificationAsync(Task notification, ConfigChangedEventArgs args)
    {
        if (_notifying.Value == this)
        {
            // Called from inside one of our own change handlers: the new notification is queued
            // behind the running one, so waiting here would deadlock. It runs once the handler returns.
            _ = notification.ContinueWith(static t => _ = t.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return;
        }

        try
        {
            await notification.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ConfigApplyException(args.SectionName, ex);
        }
    }

    // ─── File IO ──────────────────────────────────────────────────────────────

    private async Task WriteFileAsync(string path, string json, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Write-then-rename so readers (and crashes) never see a half-written file. The temp name is
        // unique, so even writers outside this manager (another instance on the same directory)
        // never share or clobber it.
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            RememberContent(path, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    private async Task<string> ReadFileAsync(string path, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        RememberContent(path, json);
        return json;
    }

    private void RememberContent(string path, string content) =>
        _knownContent[Path.GetFullPath(path)] = Hash(content);

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static ConfigModelBase Deserialize(string json, Type type, string source) =>
        JsonSerializer.Deserialize(json, type, _jsonOptions) as ConfigModelBase
            ?? throw new InvalidOperationException($"Failed to deserialize '{type.Name}' from {source}");

    internal Type ResolveTypeForSection(string sectionName)
    {
        var key = sectionName ?? string.Empty;
        foreach (var (type, subName) in _registered)
        {
            if (string.Equals(subName, key, StringComparison.Ordinal))
                return type;
        }
        throw new InvalidOperationException(
            $"No config registered for section '{key}'. Known sections: {string.Join(", ", _registered.Values.Select(s => $"'{s}'"))}");
    }

    /// <summary>
    /// Walk <paramref name="incoming"/> and replace any string property that
    /// equals <see cref="ConfigField.SecretMask"/> with the corresponding value
    /// from <paramref name="previous"/>. Recurses into nested objects,
    /// <c>Dictionary&lt;string,X&gt;</c> entries and <c>List&lt;X&gt;</c> entries
    /// (see <see cref="PairListEntries"/>). Masked entries of a list of strings are restored by
    /// position; when the list length changed they cannot be paired safely and an error is added
    /// to <paramref name="errors"/> instead.
    /// </summary>
    private static void PreserveMaskedSecrets(Type type, object previous, object incoming, List<string> errors)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

        foreach (var prop in props)
        {
            var pt = prop.PropertyType;

            if (pt == typeof(string))
            {
                var incomingValue = prop.GetValue(incoming) as string;
                if (incomingValue == ConfigField.SecretMask)
                {
                    var previousValue = prop.GetValue(previous);
                    prop.SetValue(incoming, previousValue);
                }
                continue;
            }

            if (ConfigSchemaBuilder.IsListOfString(pt))
            {
                if (prop.GetValue(incoming) is System.Collections.IList incomingList)
                    RestoreMaskedListEntries(prop, prop.GetValue(previous) as System.Collections.IList, incomingList, errors);
                continue;
            }

            if (pt.IsClass && pt != typeof(string) && !pt.IsArray)
            {
                var prevNested = prop.GetValue(previous);
                var incomingNested = prop.GetValue(incoming);
                if (prevNested is null || incomingNested is null) continue;

                // Dictionary<string, X>
                if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
                    pt.GetGenericArguments()[0] == typeof(string))
                {
                    var valueType = pt.GetGenericArguments()[1];
                    var prevDict = (System.Collections.IDictionary)prevNested;
                    var incomingDict = (System.Collections.IDictionary)incomingNested;
                    foreach (var key in incomingDict.Keys)
                    {
                        if (prevDict.Contains(key))
                        {
                            var incomingEntry = incomingDict[key];
                            var previousEntry = prevDict[key];
                            if (incomingEntry is not null && previousEntry is not null)
                                PreserveMaskedSecrets(valueType, previousEntry, incomingEntry, errors);
                        }
                    }
                    continue;
                }

                // List<X> of objects
                if (ConfigSchemaBuilder.IsListOfObjects(pt, out var elementType) && elementType is not null)
                {
                    foreach (var (previousEntry, incomingEntry) in PairListEntries(
                                 elementType, (System.Collections.IList)prevNested, (System.Collections.IList)incomingNested))
                        PreserveMaskedSecrets(elementType, previousEntry, incomingEntry, errors);
                    continue;
                }

                PreserveMaskedSecrets(pt, prevNested, incomingNested, errors);
            }
        }
    }

    /// <summary>
    /// Walks the incoming JSON alongside <paramref name="type"/> and replaces every
    /// <see cref="ConfigField.SecretMask"/> found where a non-string secret
    /// (<see cref="ConfigSchemaBuilder.IsNonStringScalar"/>, or a list of them) is expected with the stored
    /// value from <paramref name="previous"/>. Nested objects, dictionaries and lists of objects are paired
    /// the same way as <see cref="PreserveMaskedSecrets"/>. A mask that cannot be paired with a stored
    /// value adds an error instead.
    /// </summary>
    private static void RestoreMaskedTypedSecrets(Type type, object? previous, JsonObject node, string path, List<string> errors)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

        foreach (var prop in props)
        {
            var jsonName = JsonName(prop);
            if (!node.TryGetPropertyValue(jsonName, out var child) || child is null) continue;

            var pt = prop.PropertyType;
            var fieldPath = path.Length == 0 ? jsonName : $"{path}.{jsonName}";
            var previousValue = previous is null ? null : prop.GetValue(previous);
            var secret = prop.GetCustomAttribute<ConfigFieldAttribute>()?.Secret ?? false;

            if (ConfigSchemaBuilder.IsNonStringScalar(pt))
            {
                if (!secret || !IsMask(child)) continue;
                if (previous is null)
                    errors.Add(MaskWithoutValue(fieldPath));
                else
                    node[jsonName] = JsonSerializer.SerializeToNode(previousValue, pt, _jsonOptions);
                continue;
            }

            if (ConfigSchemaBuilder.IsListOfPrimitive(pt))
            {
                var elementType = pt.GetGenericArguments()[0];
                if (!secret || child is not JsonArray array
                    || !ConfigSchemaBuilder.IsNonStringScalar(elementType)
                    || !array.Any(IsMask)) continue;

                if (previousValue is not System.Collections.IList previousList || previousList.Count != array.Count)
                {
                    errors.Add($"'{fieldPath}' still contains masked secret entries but its length changed; " +
                               "send every entry in clear text to change the list.");
                    continue;
                }
                for (var i = 0; i < array.Count; i++)
                    if (IsMask(array[i]))
                        array[i] = JsonSerializer.SerializeToNode(previousList[i], elementType, _jsonOptions);
                continue;
            }

            if (!pt.IsClass || pt == typeof(string) || pt.IsArray) continue;

            // Dictionary<string, X>
            if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
                pt.GetGenericArguments()[0] == typeof(string))
            {
                if (child is not JsonObject entries) continue;
                var valueType = pt.GetGenericArguments()[1];
                var previousDict = previousValue as System.Collections.IDictionary;
                foreach (var entry in entries.ToList())
                {
                    if (entry.Value is JsonObject entryObject)
                        RestoreMaskedTypedSecrets(valueType,
                            previousDict is not null && previousDict.Contains(entry.Key) ? previousDict[entry.Key] : null,
                            entryObject, $"{fieldPath}.{entry.Key}", errors);
                }
                continue;
            }

            // List<X> of objects
            if (ConfigSchemaBuilder.IsListOfObjects(pt, out var objectType) && objectType is not null)
            {
                if (child is not JsonArray items) continue;
                var previousItems = previousValue as System.Collections.IList;
                var keyProp = ConfigSchemaBuilder.FindKeyProperty(objectType);
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is not JsonObject item) continue;
                    RestoreMaskedTypedSecrets(objectType, FindPreviousEntry(previousItems, items, i, item, keyProp),
                        item, $"{fieldPath}.{i}", errors);
                }
                continue;
            }

            if (child is JsonObject nested)
                RestoreMaskedTypedSecrets(pt, previousValue, nested, fieldPath, errors);
        }
    }

    /// <summary>Same pairing rule as <see cref="PairListEntries"/>: by key property, else by position when the length is unchanged.</summary>
    private static object? FindPreviousEntry(
        System.Collections.IList? previous, JsonArray incoming, int index, JsonObject item, PropertyInfo? keyProp)
    {
        if (previous is null) return null;
        if (keyProp is not null)
        {
            if (item[JsonName(keyProp)] is not JsonValue keyNode || !keyNode.TryGetValue<string>(out var key))
                return null;
            foreach (var entry in previous)
                if (entry is not null && keyProp.GetValue(entry) is string existing && existing == key)
                    return entry;
            return null;
        }
        return previous.Count == incoming.Count ? previous[index] : null;
    }

    private static bool IsMask(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) && s == ConfigField.SecretMask;

    private static string MaskWithoutValue(string path) =>
        $"'{path}' still contains the masked secret placeholder but there is no stored value to keep; send the value in clear text.";

    private static string JsonName(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
        ?? _jsonOptions.PropertyNamingPolicy?.ConvertName(prop.Name)
        ?? prop.Name;

    private static void RestoreMaskedListEntries(
        PropertyInfo prop, System.Collections.IList? previous, System.Collections.IList incoming, List<string> errors)
    {
        var masked = false;
        foreach (var entry in incoming)
            if (entry is string s && s == ConfigField.SecretMask) { masked = true; break; }
        if (!masked) return;

        if (previous is null || previous.Count != incoming.Count)
        {
            errors.Add($"'{prop.Name}' still contains masked secret entries but its length changed; " +
                       "send every entry in clear text to change the list.");
            return;
        }

        for (var i = 0; i < incoming.Count; i++)
            if (incoming[i] is string s && s == ConfigField.SecretMask)
                incoming[i] = previous[i];
    }

    /// <summary>
    /// Pairs incoming list entries with the previous entries they replace: by the element's key
    /// property (<c>Id</c>, <c>Name</c>, <c>Key</c> or <c>…Id</c>) when it has one, otherwise by
    /// position, and only when the list length is unchanged — a masked secret must never be
    /// filled in from a different entry.
    /// </summary>
    private static IEnumerable<(object Previous, object Incoming)> PairListEntries(
        Type elementType, System.Collections.IList previous, System.Collections.IList incoming)
    {
        var keyProp = ConfigSchemaBuilder.FindKeyProperty(elementType);
        if (keyProp is not null)
        {
            Dictionary<string, object> byKey = [with(StringComparer.Ordinal)];
            foreach (var entry in previous)
                if (entry is not null && keyProp.GetValue(entry) is string key)
                    byKey.TryAdd(key, entry);

            foreach (var entry in incoming)
                if (entry is not null && keyProp.GetValue(entry) is string key && byKey.TryGetValue(key, out var match))
                    yield return (match, entry);
            yield break;
        }

        if (previous.Count != incoming.Count) yield break;
        for (var i = 0; i < incoming.Count; i++)
            if (previous[i] is { } p && incoming[i] is { } n)
                yield return (p, n);
    }

    private string GetFilePath(Type type)
    {
        if (!_registered.TryGetValue(type, out var subName))
            throw new InvalidOperationException(
                $"Configuration type '{type.Name}' is not registered. " +
                $"Call Register<{type.Name}>() in OnConfigureAsync first.");

        var fileName = string.IsNullOrEmpty(subName) ? "config.json" : $"config.{subName}.json";
        return Path.Combine(_baseDirectory, fileName);
    }
}
