# CodeLogic.json Reference

The framework configuration file lives at `{FrameworkRoot}/Framework/CodeLogic.json`.

---

## Files and Load Order

| File | When loaded |
|------|-------------|
| `CodeLogic.json` | Normal production/default runtime config |
| `CodeLogic.Development.json` | Loaded instead of `CodeLogic.json` when the environment is `Development` (`CODELOGIC_ENVIRONMENT`, `DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT`), or when `CodeLogicOptions.UseDevelopmentConfig = true`. An attached debugger alone does not switch files |

`CodeLogic.Development.json` is a full replacement file, not an overlay. It must contain every section you want the runtime to use.

Add `CodeLogic.Development.json` to `.gitignore`.

---

## Complete Schema

```json
{
  "framework": {
    "name": "CodeLogic",
    "version": "5.0.0"
  },
  "logging": {
    "mode": "singleFile",
    "maxFileSizeMb": 10,
    "maxRolledFiles": 5,
    "fileNamePattern": "{date:yyyy}/{date:MM}/{date:dd}/{level}.log",
    "globalLevel": "Warning",
    "enableConsoleOutput": false,
    "consoleMinimumLevel": "Debug",
    "enableDebugMode": false,
    "centralizedDebugLog": false,
    "centralizedLogsPath": null,
    "includeMachineName": true,
    "timestampFormat": "yyyy-MM-dd HH:mm:ss.fff"
  },
  "localization": {
    "defaultCulture": "en-US",
    "supportedCultures": ["en-US"],
    "autoGenerateTemplates": true
  },
  "libraries": {
    "discoveryPattern": "CL.*",
    "enableDependencyResolution": true,
    "configHotReload": {
      "enabled": false,
      "debounceMs": 500
    }
  },
  "healthChecks": {
    "enabled": true,
    "intervalSeconds": 30
  }
}
```

---

## Sections

### `framework`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `name` | string | `"CodeLogic"` | Informational display name |
| `version` | string | `"5.0.0"` | Informational framework version |

### `logging`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `mode` | string | `"singleFile"` | `"singleFile"` or `"dateFolder"` |
| `maxFileSizeMb` | int | `10` | Max size before rolling in single-file mode |
| `maxRolledFiles` | int | `5` | Number of rolled files to keep |
| `fileNamePattern` | string | `"{date:yyyy}/{date:MM}/{date:dd}/{level}.log"` | Path pattern used in date-folder mode |
| `globalLevel` | string | `"Warning"` | Minimum file log level |
| `enableConsoleOutput` | bool | `false` | Also write logs to console |
| `consoleMinimumLevel` | string | `"Debug"` | Minimum console log level |
| `enableDebugMode` | bool | `false` | Debug flag passed on to `LoggingOptions`; CodeLogic's loggers don't read it |
| `centralizedDebugLog` | bool | `false` | Also write every entry of every component to `{centralizedLogsPath}/yyyy/MM/dd/debug_all.log` |
| `centralizedLogsPath` | string? | `null` | Directory of the centralized log; `null` = `{FrameworkRoot}/Framework/logs/debug`, relative = under the framework root |
| `includeMachineName` | bool | `true` | Prefix entries with machine name |
| `timestampFormat` | string | `"yyyy-MM-dd HH:mm:ss.fff"` | Timestamp output format |

### `localization`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `defaultCulture` | string | `"en-US"` | Fallback culture |
| `supportedCultures` | string[] | `["en-US"]` | Cultures to generate and load |
| `autoGenerateTemplates` | bool | `true` | Generate missing localization templates (application, libraries, plugins). When `false`, nothing is written and missing cultures use the models' built-in strings |

### `libraries`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `discoveryPattern` | string | `"CL.*"` | Directory pattern used under `Libraries/` |
| `enableDependencyResolution` | bool | `true` | Topologically sort libraries before startup |
| `configHotReload.enabled` | bool | `false` | Watch `Libraries/*/config*.json` and reload a section when its file is edited. Invalid edits are logged and ignored. See [Configuration › Watching config files](../Core/configuration.md#watching-config-files) |
| `configHotReload.debounceMs` | int | `500` | Quiet period after the last file event before reloading |

### `healthChecks`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `enabled` | bool | `true` | Enable scheduled health checks |
| `intervalSeconds` | int | `30` | Time between scheduled health checks; must be > 0 when enabled |

### Validation

The runtime checks the file while loading it and fails `InitializeAsync` (with the reasons in
`InitializationResult.Message`) when `healthChecks.intervalSeconds <= 0` with health checks enabled,
`logging.timestampFormat` is empty or not a valid .NET date/time format, `logging.maxFileSizeMb <= 0`
or `logging.maxRolledFiles < 0`. `CodeLogicConfiguration.Validate()` runs the same checks.

---

## Development File Example

```json
{
  "framework": {
    "name": "CodeLogic",
    "version": "5.0.0"
  },
  "logging": {
    "mode": "singleFile",
    "maxFileSizeMb": 10,
    "maxRolledFiles": 5,
    "fileNamePattern": "{date:yyyy}/{date:MM}/{date:dd}/{level}.log",
    "globalLevel": "Debug",
    "enableConsoleOutput": true,
    "consoleMinimumLevel": "Debug",
    "enableDebugMode": true,
    "centralizedDebugLog": false,
    "centralizedLogsPath": null,
    "includeMachineName": true,
    "timestampFormat": "yyyy-MM-dd HH:mm:ss.fff"
  },
  "localization": {
    "defaultCulture": "en-US",
    "supportedCultures": ["en-US", "da-DK"],
    "autoGenerateTemplates": true
  },
  "libraries": {
    "discoveryPattern": "CL.*",
    "enableDependencyResolution": true,
    "configHotReload": {
      "enabled": false,
      "debounceMs": 500
    }
  },
  "healthChecks": {
    "enabled": true,
    "intervalSeconds": 10
  }
}
```

---

## File Location Helpers

```csharp
var opts = CodeLogic.GetOptions();

opts.GetCodeLogicConfigPath();
opts.GetCodeLogicDevelopmentConfigPath();
```

Both files live under `{FrameworkRoot}/Framework/`.
