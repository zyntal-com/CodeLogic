# CLI Arguments

CodeLogic parses command-line arguments during `InitializeAsync()`. CLI flags override programmatic options.

---

## Supported Flags

| Argument | Description |
|----------|-------------|
| `--version` | Print framework and app version, then exit |
| `--info` | Print runtime info, then exit |
| `--health` | Run a health check after startup |
| `--generate-configs` | Generate missing config files before startup continues |
| `--generate-configs-force` | Overwrite existing config files with defaults |
| `--dry-run` | Run `ConfigureAsync()` only, validate startup inputs, and do not write files |

---

## `--version`

```text
$ myapp --version
CodeLogic 4.8.142 | App 1.0.0
```

This sets `InitializationResult.ShouldExit = true`.

---

## `--info`

```text
$ myapp --info
CodeLogic 4.8.142
  App version  : 1.0.0
  Machine      : MY-MACHINE
  Framework    : C:\MyApp\CodeLogic
  Application  : C:\MyApp\CodeLogic\Application
  Libraries    : C:\MyApp\CodeLogic\Libraries
  Development  : False
```

This also sets `InitializationResult.ShouldExit = true`.

---

## `--health`

`--health` does not exit immediately. It sets `InitializationResult.RunHealthCheck = true`, so the caller can complete startup first:

```csharp
var result = await CodeLogic.InitializeAsync(o => o.AppVersion = "1.0.0");
if (result.ShouldExit) return;

await Libraries.LoadAsync<SqliteLibrary>();
CodeLogic.RegisterApplication(new MyApp());
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

if (result.RunHealthCheck)
{
    var report = await CodeLogic.GetHealthAsync();
    Console.WriteLine(report.ToConsoleString());
    await CodeLogic.StopAsync();
    return;
}
```

---

## `--generate-configs`

Generates missing application and library config files during `ConfigureAsync()`. Existing files are left untouched.

```text
$ myapp --generate-configs
```

You can scope generation to specific libraries by appending library IDs:

```text
$ myapp --generate-configs CL.SQLite CL.Mail
```

Scoping applies to libraries only. The application config still follows the normal generation rules.

---

## `--generate-configs-force`

Overwrites existing config files with defaults during `ConfigureAsync()`.

```text
$ myapp --generate-configs-force
```

Scoped force generation is also supported:

```text
$ myapp --generate-configs-force CL.SQLite
```

When scoped, only the listed libraries are force-regenerated.

---

## `--dry-run`

Runs `ConfigureAsync()` only:

- runs `OnConfigureAsync()` on the application and libraries
- validates existing config and localization files
- prints any config files that would be generated or overwritten
- does not write files
- skips `OnInitializeAsync()` and `OnStartAsync()`

Example:

```text
$ myapp --generate-configs --dry-run
[dry-run] Would generate: CodeLogic\Application\config.json
[dry-run] Would generate: CodeLogic\Libraries\CL.SQLite\config.json
Dry run complete - all config files validated successfully.
```

If `--generate-configs-force` is combined with `--dry-run`, existing config files are reported as `Would overwrite`.

---

## `InitializationResult`

```csharp
public sealed class InitializationResult
{
    bool Success { get; init; }
    bool IsFirstRun { get; init; }
    bool ShouldExit { get; init; }
    string Message { get; init; }
    bool RunHealthCheck { get; init; }
}
```

Typical handling:

```csharp
var result = await CodeLogic.InitializeAsync(o => o.AppVersion = "1.0.0");

if (!result.Success)
{
    Console.Error.WriteLine(result.Message);
    Environment.Exit(1);
}

if (result.ShouldExit)
    return;
```

---

## Programmatic Equivalents

```csharp
await CodeLogic.InitializeAsync(o =>
{
    o.AppVersion = "1.0.0";
    o.GenerateConfigs = true;
    o.GenerateConfigsForce = false;
    o.GenerateConfigsFor = null;
    o.ExitAfterGenerate = false;
    o.DryRun = false;
});
```
