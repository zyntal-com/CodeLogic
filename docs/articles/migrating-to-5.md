# Migrating to CodeLogic 5.0

CodeLogic 5.0 targets **.NET 11 only** and builds on C# 15. The 4.x line (.NET 10) stays on nuget.org and gets fixes from the `support/4.x` branch, so you can move when you're ready.

## Before you start

- Target `net11.0` in every project that references CodeLogic or a `CodeLogic.*` / `CL.*` 5.0 package.
- Use the .NET 11 SDK. A `global.json` with `"rollForward": "latestFeature"` is recommended.
- Move all CodeLogic packages together. Libraries and plugins built against 4.x won't load into a 5.0 host.

## Breaking changes

### `Error` is a closed record hierarchy

`Error` used to be a sealed class, and its factories didn't record the kind of error. In 5.0 it is a `closed record` with one sealed subtype per kind:

| Factory | Returns |
|---|---|
| `Error.NotFound` | `NotFoundError` |
| `Error.Validation` | `ValidationError` |
| `Error.Internal`, `Error.FromException` | `InternalError` |
| `Error.Unauthorized` | `UnauthorizedError` |
| `Error.Conflict` | `ConflictError` |
| `Error.Timeout` | `TimeoutError` |
| `Error.Unavailable` | `UnavailableError` |

Effects on existing code:

- **Factory calls compile unchanged.** The factories still return `Error`.
- **Equality is by value now.** Two errors with the same kind, code, message, details and inner error are equal. In 4.x, two separately created errors were never equal.
- **You can't derive from `Error` outside CodeLogic.** For library-specific kinds, use a code (for example `"mail.smtp_auth"`) on one of the existing kinds.
- **`WithInner` / `WithDetails` keep the kind.** The copy is the same subtype as the original.

### `Result<T>` and `Result` are unions

The 4.x members still exist: `IsSuccess`, `IsFailure`, `Value`, `Error`, `Success`, `Failure`, the implicit conversions, `Map`, `OnSuccess`, `OnFailure`, `ValueOrDefault`, `ValueOrThrow` and `ToResult`. What changes:

- **Patterns test the contents.** `result is Error e` and `result is User u` now look inside the result, not at the struct itself. `result is Result<T>` no longer compiles; use `result.HasValue` or a `var` pattern instead.
- **A `default` result is empty.** It matches the `null` pattern. `Map`, `Bind`, `OnFailure` and `ToResult` report `Error.Empty` for it instead of a `null` error.
- **A `null` success value matches the `null` pattern.** `IsSuccess` is still `true`.
- **`Result<Error>` has no implicit conversion.** Use `Result<Error>.Success(...)` or `Result<Error>.Failure(...)`.
- **Results compare by value** (`==`, `Equals`).

### Framework events share a closed base

All 13 events in `FrameworkEvents.cs` now derive from `closed record FrameworkEvent`.

- **Subscribe once for all of them.** `Subscribe<FrameworkEvent>` receives every framework event, and you can switch over them.
- **You can't derive from framework events outside CodeLogic.** Your own events should implement `IEvent`, as before.

### Culture-invariant logging

Timestamps, date folders in log paths, and upper-cased library and plugin IDs now use the invariant culture. On machines with a non-Gregorian calendar (such as th-TH) or a Turkish locale, log folders and logger names may differ from 4.x.

### Plugins

- **Dependencies resolve inside the plugin.** A plugin no longer receives whatever assembly with a matching name is already loaded in the process. `CodeLogic` is always the host's copy; other dependencies come from the plugin's own folder first and from the host only when the plugin doesn't ship them. If the host and a plugin exchange types from another assembly (a shared contracts library, say) and the plugin ships its own copy, add that assembly's simple name to `PluginOptions.SharedAssemblies`.
- **Plugin IDs must be unique.** `LoadPluginAsync` throws `InvalidOperationException` for an ID that is already loaded instead of replacing the running plugin. Use `ReloadPluginAsync` to replace one.
- **`ResetAsync` disposes the registered `PluginManager`.** After a reset, create and register a new manager; the old one throws `ObjectDisposedException`.
- **`ResetAsync` drops event subscriptions.** Every subscription on the runtime's event bus is removed by a reset, along with the shutdown handlers. Subscribe again after re-initializing.

### Libraries, configuration and localization

- **Dependency versions are SemVer 2.0.** `MinVersion` checks compare pre-release versions (`5.0.0-preview.1` does not satisfy `5.0.0`), and an unparseable `MinVersion` or library `Version` now fails dependency validation. `SemanticVersion` parses `-prerelease`/`+build`, rejects leading zeros, and its `ToString()` includes the suffixes.
- **Non-string secrets are masked.** Admin UIs that read `GetSchema` get `ConfigField.SecretMask` for every set secret value, including numbers, `Guid`s and enums. Send the mask back unchanged to keep the stored value.
- **Culture names are validated.** Localization cultures may contain only ASCII letters, digits, `-` and `_`; anything else throws `ArgumentException`.
- **`LibraryManager.GetAllLibraries` / `GetLoadedLibraries` return snapshots** instead of live views.
- **Masks that can't be matched are rejected.** `UpdateSectionAsync` rejects an update that still contains `ConfigField.SecretMask` for a value it cannot pair with a stored one (a new list or dictionary entry, or an entry of a list without an `Id`/`Name`/`Key` property whose length changed). Admin UIs must send such values in clear text; give list entries a key property so entries can be added without re-entering every secret. `ConfigValidationResult.FieldErrors` names the field.
- **Schema paths use JSON names.** `GetSchema` paths follow `[JsonPropertyName]` and the camelCase policy, and skip `[JsonIgnore]` properties. Only properties with such attributes or leading acronyms (`URLPath` → `urlPath`) change.
- **Names in file names are validated.** Config section names (`Register<T>(name)`), library IDs and plugin IDs may not contain path separators, `..`, `:`, `*`, `?`, `"`, `<`, `>`, `|`, control characters, or leading/trailing whitespace or a trailing dot.
- **Config files are `0600` on Unix.** If another account must read a library's `config*.json`, grant it explicitly after CodeLogic writes the file.
- **Invalid `CodeLogic.json` values fail initialization**: `healthChecks.intervalSeconds <= 0` with health checks enabled, an invalid `logging.timestampFormat`, `logging.maxFileSizeMb <= 0`, `logging.maxRolledFiles < 0`.

### Environment, plugins and logging

- **`CodeLogic.Development.json` needs a Development environment.** It is used only when `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` or `CODELOGIC_ENVIRONMENT` is `Development`, or with `CodeLogicOptions.UseDevelopmentConfig = true`. An attached debugger no longer selects it. Add `DOTNET_ENVIRONMENT=Development` to your launch profile (ASP.NET Core profiles already set `ASPNETCORE_ENVIRONMENT`). `CodeLogicEnvironment.IsDevelopment` follows the same rule.
- **Relative plugin directories resolve against the app.** `PluginOptions.PluginsDirectory` left at its default means `{FrameworkRoot}/Plugins`; any other relative path resolves against `AppContext.BaseDirectory`, not the working directory. Use `PluginManager.PluginsDirectory` to see the path in use, and pass an absolute path if you relied on the working directory.
- **`MinFrameworkVersion` is enforced.** A plugin requiring a newer CodeLogic is not loaded.
- **Library dependencies resolve only from discovered library folders.** A library registered by type no longer makes CodeLogic probe the host folder for assemblies missing from the host's `.deps.json`; reference them from the host project instead.
- **The centralized debug log works without `enableDebugMode`.** With `centralizedDebugLog: true` it is written to `{FrameworkRoot}/Framework/logs/debug/yyyy/MM/dd/debug_all.log` by default; `centralizedLogsPath` is a directory.

## New in 5.0

- Exhaustive `switch` over `Result<T>`, `Result`, `Error` and `FrameworkEvent`.
- `Result<T>.Bind`, `Match`, `MapAsync`, `BindAsync`; `Result.Bind`, `Match`.
- `Ok` (the success case of `Result`) and `Error.Empty`.
- Runtime-native async on .NET 11: shorter async stack traces and lower overhead, with no code changes.
