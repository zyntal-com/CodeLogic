# CodeLogic.json Reference

The framework configuration file lives at `{FrameworkRoot}/Framework/CodeLogic.json`.

---

## Files and Load Order

| File | When loaded |
|------|-------------|
| `CodeLogic.json` | Normal production/default runtime config |
| `CodeLogic.Development.json` | Loaded instead of `CodeLogic.json` in a `DEBUG` build or when a debugger is attached |

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
| `enableDebugMode` | bool | `false` | Enables verbose debug logging features |
| `centralizedDebugLog` | bool | `false` | Writes library logs to a shared debug file |
| `centralizedLogsPath` | string? | `null` | Custom centralized log path |
| `includeMachineName` | bool | `true` | Prefix entries with machine name |
| `timestampFormat` | string | `"yyyy-MM-dd HH:mm:ss.fff"` | Timestamp output format |

### `localization`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `defaultCulture` | string | `"en-US"` | Fallback culture |
| `supportedCultures` | string[] | `["en-US"]` | Cultures to generate and load |
| `autoGenerateTemplates` | bool | `true` | Generate missing localization templates |

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
| `intervalSeconds` | int | `30` | Time between scheduled health checks |

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
