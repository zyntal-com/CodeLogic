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
using CodeLogic.Core;

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
            return ConfigValidationResult.Invalid(["Path traversal not allowed in connection string"]);

        return ConfigValidationResult.Valid();
    }
}
```

---

## ConfigSectionAttribute

Controls the file name suffix for a config model:

```csharp
[ConfigSection("database")]
public class DatabaseConfig : ConfigModelBase { ... }
```

| Library ID | Attribute value | File name |
|------------|-----------------|-----------|
| `MyApp.Database` | `[ConfigSection("database")]` | `config.database.json` |
| `MyApp.Database` | `[ConfigSection("advanced")]` | `config.advanced.json` |
| `MyApp.Database` | *(none)* | `config.json` |

Files are placed in the library's `ConfigDirectory` (`{FrameworkRoot}/Libraries/{LibraryId}/`).

---

## Registering Config Models

Register models in `OnConfigureAsync`:

```csharp
public Task OnConfigureAsync(LibraryContext context)
{
    context.Configuration.Register<DatabaseConfig>();
    context.Configuration.Register<AdvancedConfig>();
    return Task.CompletedTask;
}
```

Reading config in `OnConfigureAsync` will throw — files are generated/loaded after this phase completes.

---

## Reading Config

Read models in `OnInitializeAsync` or later:

```csharp
public async Task OnInitializeAsync(LibraryContext context)
{
    var config = context.Configuration.Get<DatabaseConfig>();

    var validation = config.Validate();
    if (!validation.IsValid)
        throw new InvalidOperationException(string.Join("; ", validation.Errors));

    // use config values
}
```

---

## IConfigurationManager API

```csharp
public interface IConfigurationManager
{
    void Register<T>() where T : ConfigModelBase, new();
    T Get<T>() where T : ConfigModelBase;
    Task ReloadAsync<T>() where T : ConfigModelBase, new();
    Task ReloadAllAsync();
    Task SaveAsync<T>(T model) where T : ConfigModelBase, new();
    event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
}
```

---

## Environment Overrides

The framework-level config file (`CodeLogic.json`) has a development variant: `CodeLogic.Development.json`. When running in development mode (DEBUG build or debugger attached), the framework loads `CodeLogic.Development.json` **instead of** `CodeLogic.json`. This is a full replacement — not a merge or overlay — so the development file must be complete.

Use this for local log levels and developer settings that should not be committed to source control:

```json
// Framework/CodeLogic.Development.json  ← add to .gitignore
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
    var host = CodeLogic.GetRequiredStartupParameter<string>("mysql-host");

    Libraries.OverrideConfig<DatabaseConfiguration>(
        "CL.MySQL2",
        "mysql",
        config => config.Databases["Default"].Host = host);

    return Task.CompletedTask;
}
```

Set `MYSQL_HOST=db.internal` in the environment, or pass `--mysql-host db.internal` (or `--mysql-host=db.internal`). Command-line values win. The override is applied after the normal JSON is read but before final validation, is visible to the library during `OnInitializeAsync`, and is never written to the JSON file — not even when the section is later saved, updated through an admin UI, or reloaded (the override is re-applied each time). Register it no later than application `OnConfigureAsync`.

---

## First-Run Generation

When a config file does not exist, CodeLogic:

1. Creates the config file with default property values
2. Exits with a message: `Config files generated. Please review and restart.`

Use the `--generate-configs` flag to generate without starting:

```bash
./MyApp --generate-configs
```

Use `--force` to regenerate even if files already exist:

```bash
./MyApp --generate-configs-force
```

---

## Hot Reload

Config can be reloaded at runtime:

```csharp
await context.Configuration.ReloadAsync<DatabaseConfig>();
var updated = context.Configuration.Get<DatabaseConfig>(); // reload replaces the instance
```

Libraries can apply changes live, per instance, by implementing `IConfigReloadable` and using `ConfigDiff.Keyed` on the old/new values — e.g. reconnect only the one database whose settings changed. File watching is opt-in via `libraries.configHotReload` in `CodeLogic.json`. See [Core › Configuration](../Core/configuration.md#live-reload-for-libraries-490) for the full pattern.

---

## framework config: CodeLogic.json

The framework itself uses `CodeLogic.json` in the `FrameworkRootPath` directory. It controls:

- Logging level and output mode
- Library directories
- Development mode detection

See [API Reference](../api/index.md) for the full `CodeLogic.json` property reference.
