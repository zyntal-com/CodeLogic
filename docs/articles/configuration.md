# Configuration

CodeLogic's configuration system provides per-component JSON files with automatic generation, DataAnnotations validation, environment overrides, and hot-reload support.

---

## ConfigModelBase

All config classes inherit from `ConfigModelBase`:

```csharp
public abstract class ConfigModelBase
{
    public virtual ConfigValidationResult Validate();
}
```

The `Validate()` method runs DataAnnotations validation automatically. Override it to add custom rules:

```csharp
using System.ComponentModel.DataAnnotations;
using CodeLogic.Core.Configuration;

public class DatabaseConfig : ConfigModelBase
{
    [Required]
    public string ConnectionString { get; set; } = "Data Source=myapp.db";

    [Range(1, 100)]
    public int MaxConnections { get; set; } = 10;

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 30;

    public override ConfigValidationResult Validate()
    {
        var result = base.Validate();      // runs DataAnnotations
        if (!result.IsValid) return result;

        // custom rules
        if (ConnectionString.Contains(".."))
            return ConfigValidationResult.Invalid("Path traversal not allowed in connection string");

        return ConfigValidationResult.Valid();
    }
}
```

---

## File names

The name passed to `Register<T>()` sets the file name suffix:

| Registration | File name |
|--------------|-----------|
| `Register<DatabaseConfig>()` | `config.json` |
| `Register<DatabaseConfig>("database")` | `config.database.json` |
| `Register<AdvancedConfig>("advanced")` | `config.advanced.json` |

Files are placed in the library's `ConfigDirectory` (`{FrameworkRoot}/Libraries/{LibraryId}/`).

`[ConfigSection("database", Description = "...")]` only adds a description and schema version for admin UIs. It does not change the file name.

---

## Registering Config Models

Register models in `OnConfigureAsync`:

```csharp
public Task OnConfigureAsync(LibraryContext context)
{
    context.Configuration.Register<DatabaseConfig>();             // config.json
    context.Configuration.Register<AdvancedConfig>("advanced");   // config.advanced.json
    return Task.CompletedTask;
}
```

Reading config in `OnConfigureAsync` will throw — files are generated/loaded after this phase completes.

---

## Reading Config

Read models in `OnInitializeAsync` or later:

```csharp
public Task OnInitializeAsync(LibraryContext context)
{
    var config = context.Configuration.Get<DatabaseConfig>();
    // use config values
    return Task.CompletedTask;
}
```

Configs are validated when they load, so a library never sees an invalid instance. An invalid file fails the library's Configure phase, which stops startup unless the library is optional.

---

## IConfigurationManager API

The members you use most:

```csharp
public interface IConfigurationManager
{
    void Register<T>(string? subConfigName = null) where T : ConfigModelBase, new();
    T Get<T>() where T : ConfigModelBase, new();
    Task SaveAsync<T>(T config) where T : ConfigModelBase, new();
    Task ReloadAsync<T>() where T : ConfigModelBase, new();
    Task ReloadAllAsync();
    event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
    // ... plus generate/load/validate and the admin-UI members (GetSections, GetSchema, UpdateSectionAsync, ResetSectionAsync)
}
```

See [Core › Configuration](https://github.com/zyntal-com/CodeLogic/blob/main/docs/Core/configuration.md#iconfigurationmanager) for the full interface.

---

## Environment Overrides

The framework-level config file (`CodeLogic.json`) has a development variant: `CodeLogic.Development.json`. When running in development mode (the environment is `Development`, or `CodeLogicOptions.UseDevelopmentConfig = true`; an attached debugger alone does not count), the framework loads `CodeLogic.Development.json` **instead of** `CodeLogic.json`. This is a full replacement — not a merge or overlay — so the development file must be complete.

Use this for local log levels and developer settings that should not be committed to source control:

```json
// CodeLogic/Framework/CodeLogic.Development.json  ← add to .gitignore
{
  "logging": {
    "globalLevel": "Debug",
    "enableConsoleOutput": true,
    "consoleMinimumLevel": "Debug"
  }
}
```

> **Important**: because this is a full replacement (not a merge), any settings you omit revert to their defaults — copy all the sections you care about from `CodeLogic.json` and then change only what differs.

Per-component config files (`config.database.json`, etc.) do not have a development variant. For deployment values that must stay out of generated JSON, read a typed startup parameter and register a runtime-only library override from the application's `OnConfigureAsync`:

```csharp
public Task OnConfigureAsync(ApplicationContext context)
{
    var host = CodeLogic.GetRequiredStartupParameter<string>("db-host");

    Libraries.OverrideConfig<DataConfig>(
        "CL.Data",
        "data",
        config => config.Connections["Default"].Host = host);

    return Task.CompletedTask;
}
```

Set `DB_HOST=db.internal` in the environment, or pass `--db-host db.internal` (or `--db-host=db.internal`). Command-line values win. The override is applied after the normal JSON is read but before final validation, is visible to the library during `OnInitializeAsync`, and is never written to the JSON file — not even when the section is later saved, updated through an admin UI, or reloaded (the override is re-applied each time). Register it no later than application `OnConfigureAsync`.

---

## First-Run Generation

When a config file does not exist, CodeLogic writes it with the model's default values during `ConfigureAsync` and carries on starting. Set `CodeLogicOptions.GenerateConfigs = false` for strict mode: a missing file then fails the library's Configure phase.

To generate the files and stop before anything starts:

```bash
./MyApp --generate-configs --exit-after-generate
```

`--generate-configs-force` overwrites existing files with defaults. It is destructive. Both flags accept library IDs to limit the scope (`--generate-configs CL.Mail CL.SQLite`).

---

## Hot Reload

Config can be reloaded at runtime:

```csharp
await context.Configuration.ReloadAsync<DatabaseConfig>();
var updated = context.Configuration.Get<DatabaseConfig>(); // reload replaces the instance
```

Libraries can apply changes live, per instance, by implementing `IConfigReloadable` and using `ConfigDiff.Keyed` on the old/new values — e.g. reconnect only the one database whose settings changed. File watching is opt-in via `libraries.configHotReload` in `CodeLogic.json`. See [Core › Configuration](https://github.com/zyntal-com/CodeLogic/blob/main/docs/Core/configuration.md#live-reload-for-libraries) for the full pattern.

---

## framework config: CodeLogic.json

The framework itself uses `CodeLogic.json` in the `FrameworkRootPath` directory. It controls:

- Logging level and output mode
- Localization cultures
- Library discovery and config file watching
- Scheduled health checks

See [CodeLogic.json reference](https://github.com/zyntal-com/CodeLogic/blob/main/docs/Reference/codelogic-json.md) for every property.
