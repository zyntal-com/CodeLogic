# Configuration

CodeLogic's configuration system provides per-component JSON config files with auto-generation, DataAnnotations validation, and hot-reload support.

---

## ConfigModelBase

All config classes inherit from `ConfigModelBase`:

```csharp
public abstract class ConfigModelBase
{
    public virtual ConfigValidationResult Validate();
}
```

The `Validate()` method runs `DataAnnotations` validation automatically. Override it to add custom rules:

```csharp
public class DatabaseConfig : ConfigModelBase
{
    [Required]
    public string ConnectionString { get; set; } = "Data Source=mydb.sqlite";

    [Range(1, 100)]
    public int MaxConnections { get; set; } = 10;

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 30;

    public override ConfigValidationResult Validate()
    {
        var result = base.Validate();
        if (!result.IsValid) return result;

        // Custom validation
        if (ConnectionString.Contains(".."))
            return ConfigValidationResult.Invalid("Connection string must not contain '..'.");

        return ConfigValidationResult.Valid();
    }
}
```

---

## File names

The name passed to `Register<T>()` decides the file name:

| Registration | File name |
|--------------|-----------|
| `Register<T>()` | `config.json` |
| `Register<T>("db")` | `config.db.json` |

## ConfigSectionAttribute

`[ConfigSection]` adds a description and a schema version to the section. Admin UIs read them from `GetSchema`. It does **not** change the file name; pass the name to `Register<T>(name)` for that.

```csharp
[ConfigSection("database", Description = "Database connection", Version = 2)]
public class DatabaseConfig : ConfigModelBase { ... }
```

---

## IConfigurationManager

Each component gets its own `IConfigurationManager` scoped to its directory.

```csharp
public interface IConfigurationManager
{
    void Register<T>(string? subConfigName = null) where T : ConfigModelBase, new();
    T Get<T>() where T : ConfigModelBase, new();
    Task GenerateDefaultAsync<T>(bool force = false) where T : ConfigModelBase, new();
    Task LoadAsync<T>(bool generateIfMissing = true) where T : ConfigModelBase, new();
    Task SaveAsync<T>(T config) where T : ConfigModelBase, new();
    Task GenerateAllDefaultsAsync(bool force = false);
    Task LoadAllAsync(bool generateIfMissing = true);
    Task ValidateAllAsync(bool allowMissingFiles = false);
    Task ReloadAsync<T>() where T : ConfigModelBase, new();
    Task ReloadAllAsync();
    IReadOnlyList<string> GetRegisteredFilePaths();

    // Discovery, schema and generic save (admin UIs)
    IReadOnlyList<ConfigSectionInfo> GetSections();
    ConfigSchema GetSchema(string sectionName, bool includeSecrets = false);
    Task<ConfigValidationResult> UpdateSectionAsync(string sectionName, string json,
        string? changedBy = null, CancellationToken ct = default);
    Task ResetSectionAsync(string sectionName, string? changedBy = null, CancellationToken ct = default);

    event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
}
```

### Method reference

| Method | Description |
|--------|-------------|
| `Register<T>()` | Registers a config type. Call in `OnConfigureAsync`. |
| `Get<T>()` | Returns the loaded instance. Throws if not loaded. |
| `GenerateDefaultAsync<T>(force)` | Writes the section's file with defaults if it doesn't exist. `force: true` overwrites it. |
| `LoadAsync<T>(generateIfMissing)` | Loads from disk, generates defaults if missing (by default), validates. |
| `SaveAsync<T>(config)` | Validates then writes to disk. Throws if validation fails. |
| `GenerateAllDefaultsAsync(force)` | Generates defaults for all registered types that don't have files. |
| `LoadAllAsync(generateIfMissing)` | Loads all registered configs. Called automatically by the framework. |
| `ValidateAllAsync(allowMissingFiles)` | Validates the registered files on disk without loading them. |
| `ReloadAsync<T>()` | Reloads a specific config from disk and updates the in-memory instance. |
| `ReloadAllAsync()` | Reloads all registered configs from disk. |
| `GetRegisteredFilePaths()` | Returns the paths of all registered config files. |
| `GetSections()` / `GetSchema()` | List sections and describe their fields, for admin UIs. |
| `UpdateSectionAsync()` / `ResetSectionAsync()` | Update a section from JSON, or reset it to defaults. |

---

## Register → Generate → Load → Save → Reload Lifecycle

### The standard pattern (automatic)

The framework handles `GenerateAllDefaultsAsync()` and `LoadAllAsync()` automatically after `OnConfigureAsync`:

```csharp
public Task OnConfigureAsync(LibraryContext context)
{
    // Just register — framework generates and loads automatically
    context.Configuration.Register<DatabaseConfig>();
    context.Configuration.Register<CacheConfig>("cache");   // → config.cache.json
    return Task.CompletedTask;
}

public Task OnInitializeAsync(LibraryContext context)
{
    // Config is loaded — safe to read
    var db = context.Configuration.Get<DatabaseConfig>();
    context.Logger.Info($"Connecting to {db.ConnectionString}");
    return Task.CompletedTask;
}
```

### Saving changes at runtime

```csharp
var config = context.Configuration.Get<DatabaseConfig>();
config.MaxConnections = 20;
await context.Configuration.SaveAsync(config);
```

`SaveAsync` runs validation before writing. If validation fails, an exception is thrown and the file is not written.

### Hot-reloading

```csharp
await context.Configuration.ReloadAsync<DatabaseConfig>();   // one section
await context.Configuration.ReloadAllAsync();                // all sections (all-or-nothing)
```

A reload **replaces** the in-memory instance. Code that stored the result of `Get<T>()` keeps the old values — call `Get<T>()` again, or react to the change (below). Runtime overrides (`Libraries.OverrideConfig`) are re-applied on every reload and update, and are never written to the file.

Every save / update / reload / reset raises `ConfigChanged` with the section, the kind of change, and `OldValue` / `NewValue` snapshots. Notifications are delivered in commit order, and the call that made the change waits for them.

### Live reload for libraries

A library opts in to applying changes while running by implementing `IConfigReloadable` next to `ILibrary`:

```csharp
public sealed class DataLibrary : ILibrary, IConfigReloadable
{
    public async Task OnConfigChangedAsync(LibraryContext ctx, ConfigChangedEventArgs change, CancellationToken ct)
    {
        if (change.SectionName != "data") return;

        var diff = ConfigDiff.Keyed(
            change.GetOld<DataConfig>()?.Connections,
            change.GetNew<DataConfig>()!.Connections);

        foreach (var (id, _) in diff.Removed)        await RemoveConnectionAsync(id, ct);
        foreach (var (id, c) in diff.Changed)        await ReplaceConnectionAsync(id, c.New, ct);
        foreach (var (id, cfg) in diff.Added)        await AddConnectionAsync(id, cfg, ct);
        // diff.Unchanged → untouched; those connections stay open
    }
}
```

- Called for `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, `Libraries.ResetConfigAsync`, the file watcher, and the library's own `SaveAsync` — only once the library is initialized.
- `ConfigDiff.Keyed` compares keyed instances by value; `ConfigDiff.ChangedProperties` and `ConfigDiff.RequiresRestart` tell you whether a change needs a rebuild or only a knob update.
- Throwing from `OnConfigChangedAsync` reports the change as **saved but not applied**: the file is kept, `ConfigApplyResult.ApplyError` is returned (or `ConfigApplyException` is thrown from `SaveAsync`), `ConfigApplyFailedEvent` is published, and the library reports Degraded health until a later change applies.
- Libraries that don't implement the interface still get the change persisted; a warning is logged when fields marked `RequiresRestart` changed.

Admin UIs use `ConfigApplyResult`:

```csharp
var result = await Libraries.UpdateConfigAsync("CL.Data", "data", json, changedBy: user);
if (!result.IsValid)        ShowErrors(result.Errors);        // nothing saved
else if (!result.Applied)   ShowWarning(result.ApplyError!);  // saved, not applied
```

Schema fields set by a runtime override have `ConfigField.IsOverridden = true`.

### Watching config files

Edits to `Libraries/*/config*.json` can be picked up automatically. Opt in via `CodeLogic.json`:

```json
{
  "libraries": {
    "configHotReload": { "enabled": true, "debounceMs": 500 }
  }
}
```

Invalid edits are logged and the current values kept. Writes made by CodeLogic itself do not trigger a reload. Files are written atomically (write-then-rename).

Framework events: `ConfigChangedEvent` (every change), `ConfigReloadedEvent` (reloads), `ConfigApplyFailedEvent`.

---

## Validation with DataAnnotations

```csharp
using System.ComponentModel.DataAnnotations;

public class AppConfig : ConfigModelBase
{
    [Required]
    [MinLength(1)]
    public string AppName { get; set; } = "MyApp";

    [Range(1024, 65535)]
    public int Port { get; set; } = 8080;

    [EmailAddress]
    public string? AdminEmail { get; set; }

    [Url]
    public string ApiBaseUrl { get; set; } = "https://api.example.com";
}
```

When a config fails validation during `LoadAsync`, an exception is thrown with the validation errors listed. This prevents the application from starting with an invalid configuration.

`[ConfigField]`'s `Required`, `Min`, `Max`, `MaxLength` and `AllowedValues` are hints for admin UIs and are
not enforced on save. Use DataAnnotations or override `Validate()` for rules that must hold. DataAnnotations
errors on top-level properties are also reported in `ConfigValidationResult.FieldErrors` with the schema path.

Section names passed to `Register<T>(name)` become part of the file name (`config.{name}.json`), so they must
be a single file-name segment: no `/`, `\`, `..`, control characters, `:`, `*`, `?`, `"`, `<`, `>`, `|`, and
no leading/trailing whitespace or trailing dot. Anything else throws `ArgumentException`.

---

## Secrets and admin UIs

Mark sensitive properties with `[ConfigField(Secret = true)]`:

```csharp
public class SmtpServer
{
    public string Name { get; set; } = "";                         // key: pairs entries across edits
    [ConfigField(Secret = true)] public string Password { get; set; } = "";
}
```

- **Masking.** `GetSchema(section)` returns `ConfigField.SecretMask` (`••••••••`) instead of every set
  secret value, whatever its type: strings, numbers, `Guid`s, enums, lists/arrays of them and
  string-keyed dictionaries of them. `null` and `""` stay visible. `GetSchema(section, includeSecrets: true)`
  returns the real values; never send that to a browser.
- **Restoring.** `UpdateSectionAsync` puts the stored value back wherever the client echoed the mask:
  scalars directly, lists of scalars by position (only while the length is unchanged), dictionaries by key,
  and entries of lists of objects by their `Id`/`Name`/`Key`/`…Id` property, else by position when the
  length is unchanged. This works for `List<T>`, `IList<T>`, `IReadOnlyList<T>`, arrays, `Dictionary<,>`,
  `IDictionary<,>` and `IReadOnlyDictionary<,>` properties.
- **Never persisted.** A mask that cannot be matched to a stored value (a new entry, or a list without a key
  property whose length changed) rejects the whole update with a validation error naming the field
  (`ConfigValidationResult.FieldErrors`); nothing is written. Send that value in clear text. Give list
  entries a key property so an admin UI can add and remove entries without re-entering every secret.
- **Clear text on disk.** Masking only affects the admin view. Secret values are stored **in clear text** in
  `config*.json`. On Linux and macOS, CodeLogic writes config files with mode `0600` (owner read/write
  only, whatever the umask); on Windows the file inherits the directory's ACL, so restrict the
  `CodeLogic` directory to the service account. For stronger protection keep secrets out of the file
  (environment variables, a secret store) and inject them with a runtime override.

---

## Code Examples

### Multiple config files per library

```csharp
public Task OnConfigureAsync(LibraryContext context)
{
    context.Configuration.Register<AppConfig>();            // → config.json
    context.Configuration.Register<DatabaseConfig>("db");   // → config.db.json
    context.Configuration.Register<SmtpConfig>("smtp");    // → config.smtp.json
    return Task.CompletedTask;
}
```

### Using ConfigSection attribute

```csharp
[ConfigSection("smtp", Description = "Outgoing mail server")]
public class SmtpConfig : ConfigModelBase
{
    [Required]
    public string Host { get; set; } = "smtp.example.com";

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    public bool UseSsl { get; set; } = true;
}
```

The attribute only describes the section. The file name still comes from `Register`:

```csharp
context.Configuration.Register<SmtpConfig>("smtp"); // → config.smtp.json
```

### Manual generate-only (CI pattern)

```csharp
// Generate configs, then return from StartAsync without starting anything
var result = await CodeLogic.InitializeAsync(o =>
{
    o.GenerateConfigs = true;
    o.ExitAfterGenerate = true;
});
if (result.ShouldExit) return;

await CodeLogic.ConfigureAsync();   // writes missing config files
await CodeLogic.StartAsync();       // returns right away
```

Or use the CLI: `myapp --generate-configs --exit-after-generate`
