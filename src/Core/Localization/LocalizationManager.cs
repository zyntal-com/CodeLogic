using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeLogic.Core.Utilities;

namespace CodeLogic.Core.Localization;

/// <summary>
/// File-based localization manager for a single component.
/// Files live in the component's localization/ directory.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Culture names become part of a file name, so they are validated: only ASCII letters, digits,
/// <c>-</c> and <c>_</c> are accepted, and the resolved path must stay inside the localization
/// directory. Anything else throws <see cref="ArgumentException"/>.</item>
/// <item><see cref="Get{T}"/> and <see cref="GetLoadedCultures{T}"/> never block and read an immutable
/// snapshot. Loads and reloads build a new snapshot and publish it in one step, so readers keep
/// seeing the previous translations until the new ones are complete.</item>
/// </list>
/// </remarks>
public sealed class LocalizationManager : ILocalizationManager
{
    private const int MaxCultureLength = 85;

    private readonly string _localizationDirectory;
    private readonly string _localizationRoot;
    private readonly string _defaultCulture;

    // Type → Culture → Localization instance. Never mutated after it is published: writers build a
    // copy while holding _loadGate and swap the reference.
    private volatile Dictionary<Type, Dictionary<string, object>> _localizations = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Lock _registeredLock = new();
    private readonly List<Type> _registered = [];

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Initializes the localization manager with the specified directory and default culture.</summary>
    /// <exception cref="ArgumentException"><paramref name="defaultCulture"/> is not a valid culture name.</exception>
    public LocalizationManager(string localizationDirectory, string defaultCulture = "en-US")
    {
        ArgumentException.ThrowIfNullOrEmpty(localizationDirectory);
        ValidateCulture(defaultCulture, nameof(defaultCulture));
        _localizationDirectory = localizationDirectory;
        _localizationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localizationDirectory));
        _defaultCulture = defaultCulture;
        Directory.CreateDirectory(localizationDirectory);
    }

    /// <inheritdoc />
    public void Register<T>() where T : LocalizationModelBase, new()
    {
        lock (_registeredLock)
        {
            if (!_registered.Contains(typeof(T)))
                _registered.Add(typeof(T));
        }
    }

    /// <inheritdoc />
    public T Get<T>(string? culture = null) where T : LocalizationModelBase, new()
    {
        culture ??= _defaultCulture;
        var type = typeof(T);

        if (_localizations.TryGetValue(type, out var cultures))
        {
            if (cultures.TryGetValue(culture, out var localization))
                return (T)localization;

            // Fallback to default culture
            if (culture != _defaultCulture && cultures.TryGetValue(_defaultCulture, out var defaultLoc))
                return (T)defaultLoc;
        }

        throw new InvalidOperationException(
            $"Localization '{type.Name}' for culture '{culture}' is not loaded. " +
            $"Call LoadAllAsync() first.");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetLoadedCultures<T>() where T : LocalizationModelBase, new()
    {
        if (_localizations.TryGetValue(typeof(T), out var cultures))
            return [.. cultures.Keys];
        return [];
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A culture name is not valid.</exception>
    public async Task GenerateTemplatesAsync<T>(IReadOnlyList<string> cultures)
        where T : LocalizationModelBase, new()
    {
        ValidateCultures(cultures);
        foreach (var culture in cultures)
        {
            var filePath = GetFilePath<T>(culture);
            if (File.Exists(filePath)) continue; // never overwrite existing translations

            var template = new T { Culture = culture };
            var json = JsonSerializer.Serialize(template, _jsonOptions);
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A culture name is not valid.</exception>
    public async Task LoadAsync<T>(IReadOnlyList<string> cultures, bool generateIfMissing = true)
        where T : LocalizationModelBase, new()
    {
        ValidateCultures(cultures);
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = _localizations;
            var type = typeof(T);
            Dictionary<string, object> loaded = current.TryGetValue(type, out var existing) ? new(existing) : [];

            await LoadIntoAsync<T>(cultures, generateIfMissing, loaded).ConfigureAwait(false);

            _localizations = new Dictionary<Type, Dictionary<string, object>>(current) { [type] = loaded };
        }
        finally { _loadGate.Release(); }
    }

    /// <inheritdoc />
    public async Task GenerateAllTemplatesAsync(IReadOnlyList<string> cultures)
    {
        foreach (var type in RegisteredTypes())
            await InvokeGenericAsync(nameof(GenerateTemplatesAsync), BindingFlags.Public, type, cultures).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task LoadAllAsync(IReadOnlyList<string> cultures, bool generateIfMissing = true) =>
        LoadRegisteredAsync(cultures, generateIfMissing, replaceAll: false);

    /// <inheritdoc />
    /// <remarks>
    /// The registered types are read from disk into a new snapshot that replaces the current one only
    /// once every file has loaded: <see cref="Get{T}"/> keeps returning the previous translations during
    /// the reload, and still does if the reload throws.
    /// </remarks>
    public Task ReloadAllAsync(IReadOnlyList<string> cultures) =>
        LoadRegisteredAsync(cultures, generateIfMissing: true, replaceAll: true);

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task LoadRegisteredAsync(IReadOnlyList<string> cultures, bool generateIfMissing, bool replaceAll)
    {
        ValidateCultures(cultures);
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = _localizations;
            var next = replaceAll ? [] : new Dictionary<Type, Dictionary<string, object>>(current);
            foreach (var type in RegisteredTypes())
            {
                Dictionary<string, object> loaded = !replaceAll && current.TryGetValue(type, out var existing)
                    ? new(existing)
                    : [];
                await InvokeGenericAsync(nameof(LoadIntoAsync), BindingFlags.NonPublic, type,
                    cultures, generateIfMissing, loaded).ConfigureAwait(false);
                next[type] = loaded;
            }
            _localizations = next;
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>Loads <paramref name="cultures"/> of <typeparamref name="T"/> into <paramref name="target"/> (culture → instance).</summary>
    private async Task LoadIntoAsync<T>(IReadOnlyList<string> cultures, bool generateIfMissing, Dictionary<string, object> target)
        where T : LocalizationModelBase, new()
    {
        var type = typeof(T);
        T? defaultLocalization = null;

        foreach (var culture in cultures)
        {
            var filePath = GetFilePath<T>(culture);
            if (!File.Exists(filePath))
            {
                if (!generateIfMissing)
                    throw new FileNotFoundException(
                        $"Localization file for '{type.Name}' and culture '{culture}' not found at: {filePath}");

                await GenerateTemplatesAsync<T>([culture]).ConfigureAwait(false);
            }

            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            T localization;

            if (culture == _defaultCulture)
            {
                localization = JsonSerializer.Deserialize<T>(json, _jsonOptions)
                    ?? throw new InvalidOperationException(
                        $"Failed to deserialize '{type.Name}' localization from {filePath}");
            }
            else
            {
                defaultLocalization ??= await EnsureDefaultLoadedAsync<T>(generateIfMissing, target).ConfigureAwait(false);
                localization = MergeWithDefault(defaultLocalization, json);
            }

            localization.Culture = culture;
            target[culture] = localization;

            if (culture == _defaultCulture)
                defaultLocalization = localization;
        }
    }

    private Type[] RegisteredTypes()
    {
        lock (_registeredLock)
            return [.. _registered];
    }

    private string GetFilePath<T>(string culture) where T : LocalizationModelBase, new()
    {
        ValidateCulture(culture, nameof(culture));
        var section = GetSectionName<T>();
        var path = Path.Combine(_localizationDirectory, $"{section}.{culture}.json");

        // Defence in depth: the section name comes from an attribute, so check the final path as well.
        if (!PathComparison.Comparer.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _localizationRoot))
            throw new ArgumentException(
                $"Localization file for section '{section}' and culture '{culture}' resolves outside '{_localizationDirectory}'.",
                nameof(culture));
        return path;
    }

    private static void ValidateCultures(IReadOnlyList<string> cultures)
    {
        ArgumentNullException.ThrowIfNull(cultures);
        foreach (var culture in cultures)
            ValidateCulture(culture, nameof(cultures));
    }

    /// <summary>
    /// A culture name becomes part of a file name: allow only ASCII letters, digits, <c>-</c> and <c>_</c>
    /// (BCP 47 tags such as <c>en-US</c> or <c>zh-Hans-CN</c>, plus .NET sort suffixes like <c>de-DE_phoneb</c>).
    /// </summary>
    private static void ValidateCulture(string? culture, string paramName)
    {
        if (string.IsNullOrEmpty(culture) || culture.Length > MaxCultureLength)
            throw new ArgumentException($"Culture name must be 1-{MaxCultureLength} characters.", paramName);

        foreach (var c in culture)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
                throw new ArgumentException(
                    $"Culture name '{culture}' is not valid: only ASCII letters, digits, '-' and '_' are allowed.", paramName);
        }
    }

    private static string GetSectionName<T>() where T : LocalizationModelBase, new()
    {
        var attr = typeof(T)
            .GetCustomAttribute<LocalizationSectionAttribute>();
        return !string.IsNullOrWhiteSpace(attr?.SectionName) ? attr.SectionName : typeof(T).Name;
    }

    private async Task<T> EnsureDefaultLoadedAsync<T>(bool generateIfMissing, Dictionary<string, object> target)
        where T : LocalizationModelBase, new()
    {
        var type = typeof(T);

        if (target.TryGetValue(_defaultCulture, out var existing))
            return (T)existing;

        var filePath = GetFilePath<T>(_defaultCulture);
        if (!File.Exists(filePath))
        {
            if (!generateIfMissing)
                throw new FileNotFoundException(
                    $"Localization file for '{type.Name}' and culture '{_defaultCulture}' not found at: {filePath}");

            await GenerateTemplatesAsync<T>([_defaultCulture]).ConfigureAwait(false);
        }

        var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        var localization = JsonSerializer.Deserialize<T>(json, _jsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize default '{type.Name}' localization.");

        localization.Culture = _defaultCulture;
        target[_defaultCulture] = localization;
        return localization;
    }

    private T MergeWithDefault<T>(T defaultLocalization, string overrideJson)
        where T : LocalizationModelBase, new()
    {
        var defaultNode = JsonNode.Parse(
            JsonSerializer.Serialize(defaultLocalization, _jsonOptions))?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse default localization for merge.");

        var overrideNode = JsonNode.Parse(overrideJson)?.AsObject();
        if (overrideNode != null)
        {
            foreach (var prop in overrideNode)
            {
                // Only override if the value is non-null and non-empty string
                if (prop.Value != null)
                    defaultNode[prop.Key] = prop.Value.DeepClone();
            }
        }

        return defaultNode.Deserialize<T>(_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize merged localization.");
    }

    private async Task InvokeGenericAsync(string methodName, BindingFlags visibility, Type typeArg, params object[] args)
    {
        var method = typeof(LocalizationManager)
            .GetMethod(methodName, visibility | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Reflection failed: method '{methodName}' not found on LocalizationManager.");

        var task = method.MakeGenericMethod(typeArg).Invoke(this, args) as Task
            ?? throw new InvalidOperationException(
                $"Reflection failed: '{methodName}<{typeArg.Name}>' returned null.");

        await task.ConfigureAwait(false);
    }
}
