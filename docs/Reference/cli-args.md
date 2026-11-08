# CLI Arguments

CodeLogic parses command-line arguments during `InitializeAsync()`. CLI flags override programmatic options. Other `--name value` arguments are [startup parameters](#startup-parameters).

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
| `--exit-after-generate` | Sets `CodeLogicOptions.ExitAfterGenerate`: `StartAsync()` returns after the Configure phase has generated config files, without starting libraries or the application |

---

## `--version`

```text
$ myapp --version
CodeLogic 5.0.29 | App 1.0.0
```

This sets `InitializationResult.ShouldExit = true`.

---

## `--info`

```text
$ myapp --info
CodeLogic 5.0.29
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

await Libraries.LoadAsync<DataLibrary>();
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
$ myapp --generate-configs CL.Data CL.Mail
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
$ myapp --generate-configs-force CL.Data
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
[dry-run] Would generate: CodeLogic\Libraries\CL.Data\config.json
Dry run complete - all config files validated successfully.
```

If `--generate-configs-force` is combined with `--dry-run`, existing config files are reported as `Would overwrite`.

---

## `--exit-after-generate`

Stops after config generation. `ConfigureAsync()` generates the files as usual, then `StartAsync()` prints
`Config files generated. Edit them and restart.` and returns without starting libraries or the application.

```text
$ myapp --generate-configs --exit-after-generate
Config files generated. Edit them and restart.
```

It only applies while config generation is on (`CodeLogicOptions.GenerateConfigs`, `true` by default).
`InitializationResult.ShouldExit` stays `false`: the host still calls `ConfigureAsync()` and `StartAsync()`.

---

## Startup Parameters

Any other `--name value` or `--name=value` argument is an application startup parameter. Read it with
`CodeLogic.GetStartupParameter<T>` or `CodeLogic.GetRequiredStartupParameter<T>` after `InitializeAsync()`:

```csharp
var host = CodeLogic.GetStartupParameter("mysql-host", "localhost");
var port = CodeLogic.GetStartupParameter("mysql-port", 3306);
var key  = CodeLogic.GetRequiredStartupParameter<string>("api-key");   // throws when missing
```

- A command-line value wins over the environment variable. The variable name is the parameter name
  upper-cased, with every character that is not a letter or digit replaced by `_` (`mysql-host` →
  `MYSQL_HOST`).
- Names are case-insensitive. When a parameter is repeated, the last value wins.
- A flag without a value (`--verbose`) reads as `"true"`.
- Supported types: `string`, `bool`, enums (case-insensitive), `Guid`, `TimeSpan`, and the numeric types,
  parsed with the invariant culture. A value that does not convert throws `InvalidOperationException`
  (without the value, which may be a secret).
- `version`, `info`, `health`, `dry-run`, `generate-configs` and `generate-configs-force` are reserved
  and never returned as parameters.

---

## `InitializationResult`

```csharp
public sealed class InitializationResult
{
    public bool Success { get; init; }
    public bool IsFirstRun { get; init; }
    public bool ShouldExit { get; init; }
    public string Message { get; init; }
    public bool RunHealthCheck { get; init; }
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
    o.ExitAfterGenerate = false;   // --exit-after-generate
    o.DryRun = false;
});
```
