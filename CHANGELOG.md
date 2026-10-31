# Changelog

## 5.0.x

5.0 targets .NET 11 and uses C# 15 in the public API. Read
[Migrating to 5.0](docs/articles/migrating-to-5.md) before upgrading. The 4.x line (.NET 10)
continues on the `support/4.x` branch (published from `release/4.x`).

### Breaking

- **Strong-named** with the CodeLogic key (public key token `53a89305eedc5d98`), shared with CodeLogic.Libs. Assemblies that reference CodeLogic now bind to a strong-named identity.

- **Targets `net11.0` only** (was `net10.0`).
- **`Error` is a `closed record`** with one subtype per kind: `NotFoundError`,
  `ValidationError`, `InternalError`, `UnauthorizedError`, `ConflictError`, `TimeoutError`,
  `UnavailableError`. The factories are unchanged and now return those subtypes. Errors
  compare by value.
- **`Result<T>` is a union of `T | Error`, and `Result` is a union of `Ok | Error`.** A
  `switch` over them is checked for exhaustiveness. All 4.x members remain. A
  `default(Result<T>)` is empty: it matches the `null` pattern and reports `Error.Empty`.
- **The 13 framework events derive from `closed record FrameworkEvent`.** Subscribing to
  `FrameworkEvent` receives all of them.
- Log timestamps, date folders and upper-cased library/plugin IDs use the invariant culture.
  This fixes Buddhist-calendar log paths on th-TH and Turkish-I casing.
- **Plugin dependency resolution is isolated.** `PluginLoadContext` no longer hands a plugin any
  already-loaded assembly with a matching simple name (which could be another plugin's copy).
  `CodeLogic` always resolves to the host; other dependencies resolve from the plugin's own
  `.deps.json`/folder first, then from the host. Assemblies whose types cross the host/plugin
  boundary must now be listed in the new `PluginOptions.SharedAssemblies` if the plugin ships its own
  copy. `PluginLoadContext`'s constructor takes an optional `sharedAssemblies` argument (recompile).
- **Loading a plugin whose ID is already loaded throws** `InvalidOperationException` instead of
  silently replacing the running instance. Use `ReloadPluginAsync`.
- **`ResetAsync` disposes the plugin manager registered with `SetPluginManager`** and clears the
  registration. Create a new manager after a reset. Using a disposed manager throws
  `ObjectDisposedException`.
- `PluginManager.RegisterInProcessPluginAsync` is now truly asynchronous, and `GetAllPlugins` /
  `GetLoadedPlugins` return snapshots instead of live views.

### Added

- `Result<T>.Bind`, `Match`, `MapAsync`, `BindAsync`; `Result.Bind`, `Match`; `Ok`;
  `Error.Empty`; value equality for `Result` and `Result<T>`.
- `Result.ThrowIfFailure()`, the non-generic counterpart of `ValueOrThrow`.
- `ResultFailedException`, which carries the `Error`. `ValueOrThrow` and `ThrowIfFailure` throw it. It
  derives from `InvalidOperationException`, so existing `catch` blocks keep working; checks for the exact
  type (`Assert.Throws<InvalidOperationException>`) need `ThrowsAny` or the new type.

### Changed

- Builds with C# 15. Comparer and capacity collections use collection expression arguments
  (`[with(StringComparer.OrdinalIgnoreCase), ...]`).
- Async methods compile to .NET 11 runtime-native async (shorter stack traces, lower overhead).
- `object` locks are replaced by `System.Threading.Lock`, and `JsonSerializerOptions`
  instances are cached.
- `src` builds with `AnalysisLevel=latest-recommended`.
- `global.json` pins the .NET 11 SDK, and CI installs it from there.

### Fixed

- **Plugin lifecycle is serialized.** Load, register, unload, reload, dispose and hot reload run one at
  a time; readers and health checks work on snapshots, so hot reload can no longer corrupt the plugin
  table or throw "collection was modified". Calling lifecycle methods on the same manager from a plugin's
  lifecycle callback or an `OnPluginError` handler of the operation in progress throws
  `InvalidOperationException` instead of deadlocking.
- **Plugin failures clean up.** A failed load disposes the instance (after `OnUnloadAsync` when
  `OnStartAsync` was entered) and unloads its load context. A throwing `OnUnloadAsync` no longer skips
  `Dispose`, context unload and removal. Exceptions from `OnPluginLoaded`/`OnPluginUnloaded` handlers
  are no longer reported as plugin failures.
- **Default config generation is serialized with saves and loads**, and atomic writes use a unique
  temp file, so concurrent generate/load/save can no longer collide on `config.json.tmp` or overwrite
  a competing save with defaults.
- **Log directories recover.** A failed directory creation is no longer cached, and a log folder
  deleted while the process runs is recreated on the next line.
- **Log paths compare case-sensitively on Linux** (case-insensitively on Windows and macOS), so
  `App.log` and `app.log` no longer share one writer there.
- **Idle log files are closed even when nothing else is logged**; previously the idle check only ran
  after new entries arrived.
- **Secret masking covers defaults and string lists.** `GetSchema` masks the default value of secret
  string fields and every entry of secret `List<string>` fields. Echoed masks in such lists are
  restored by position; an update that changes the list length while entries are still masked is
  rejected instead of saving the mask.
- `release.yml` checks out the commit that triggered the run (`github.sha`) instead of the branch
  head, so the packed commit is always the verified, tagged one.

### Documentation

- Plugin docs and README examples use the real API (`LoadPluginAsync`/`UnloadPluginAsync`/
  `ReloadPluginAsync`, the actual `PluginManager` constructor, `PluginState` values and manifest
  members), and the isolation section describes what the load context does and does not isolate.
- The Core, Framework and Reference guides and the in-depth getting-started page are now part of the
  docs site.
- The web demo's config endpoints carry authentication/authorization guidance.

### Performance

Measured with `benchmarks/CodeLogic.Benchmarks` against 5.0.28-preview.

- **Log calls no longer touch the disk.** Lines go to one background writer per process, which keeps
  files open, writes them in order and flushes when the queue is empty. An `Info` line costs about
  0.6 µs instead of about 130 µs. `StopAsync`, process exit and an unhandled exception flush pending
  lines; `Logger.FlushAsync()` is available to hosts.
- **Disabled log levels are almost free.** New interpolated-string overloads skip evaluating the holes
  when the level is off (8 ns and 0 B instead of 460 ns and 1 KB). New `ILogger.IsEnabled(LogLevel)`.
  The format overloads take `params ReadOnlySpan<object?>` and don't format when the level is off.
- **`EventBus.Publish` takes no lock and allocates nothing.** Subscribers live in a copy-on-write
  snapshot; a publish sees the subscribers that existed when it started. 8 ns instead of 29 ns for
  one subscriber.

## 4.9.x

### Added

- **Live config changes for libraries.** A library can implement the new optional
  `IConfigReloadable` interface to apply a config change without a restart. It receives the
  old and new section values and applies only what changed, e.g. reconnecting one database
  out of three while the others stay connected. Called after `Libraries.UpdateConfigAsync`,
  `Libraries.ResetConfigAsync`, the new `Libraries.ReloadConfigAsync`, and the file watcher.
- `ConfigDiff`: `Keyed(old, new)` splits keyed instance sections into added / removed /
  changed / unchanged; `ChangedProperties` and `RequiresRestart` report changed fields.
- `ConfigChangedEventArgs` now carries `ConfigType`, `OldValue` / `NewValue` and
  `GetOld<T>()` / `GetNew<T>()`.
- Opt-in config file watcher: `libraries.configHotReload { enabled, debounceMs }` in
  `CodeLogic.json` reloads a library section when its `config*.json` file is edited. Invalid
  edits are logged and ignored; the framework's own writes don't re-trigger it.
- `ConfigChangedEvent` (every committed change) and `ConfigApplyFailedEvent` (a saved change a
  library could not apply). `ConfigReloadedEvent` is now actually published.
- `LoadedLibrary.ConfigApplyError`; a library that failed to apply a change reports Degraded
  health until a later change applies.
- `ConfigField.IsOverridden` in `GetSchema` marks fields set by runtime overrides.

### Changed

- Package author and copyright are now Zyntal.com. NuGet release notes come from this
  changelog, and the NuGet readme no longer shows the oversized logo.
- `LibraryManager` / `Libraries` `UpdateConfigAsync` and `ResetConfigAsync` return
  `ConfigApplyResult` (validation result plus apply error) instead of `ConfigValidationResult`.
  `IsValid` and `Errors` are still there; `Applied` is new. Recompile callers.
- Config files are written atomically (temp file + rename), and changes to config sections
  are serialized.

### Fixed

- Runtime config overrides (`OverrideConfig`) were lost on reload/update and were written to
  the config file when the section was saved. They are now re-applied after every change and
  never persisted.
- `UpdateSectionAsync` / `UpdateConfigAsync` and `GetSchema` threw `TargetParameterCountException`
  for any section with a `List<T>` of objects (they reflected over the list's indexer). List
  entries now appear in the schema as `{list}.{index}.{field}` with secrets masked, and masked
  secrets sent back are restored from the matching previous entry (matched by its `Id` / `Name` /
  `Key` / `…Id` property, else by position when the length is unchanged).

## 4.8.x

### Changed

- Unified the version line with `CodeLogic.Libs` on **4.8.x**. The framework and every
  official library now share one `major.minor`, so a given `4.8.<patch>` means the same
  generation across all packages.
- Moved `major.minor` into a hand-controlled repo-root `version.txt`. The patch component
  is the CI run number, composed at pack time — no version is written back to the
  repository during a release.
- `AssemblyVersion` and `FileVersion` are now derived as `Major.Minor.0.0` (`4.8.0.0`)
  instead of being hardcoded. Assembly binding stays fixed across the whole `4.8` line,
  so every patch loads interchangeably without a binding redirect.

### Removed

- Removed commit-message-driven version bumping from the release workflow. `feat:` and
  `!`/`BREAKING CHANGE` prefixes no longer move `major` or `minor`; the version line moves
  only when `version.txt` is edited.

### Added

- Added a `prerelease` branch trigger to the release workflow, matching `CodeLogic.Libs`.
  Pushing to `prerelease` publishes `4.8.<run>-preview+<sha>`; pushing to `release`
  publishes `4.8.<run>+<sha>`.
- Added [Reference/versioning.md](docs/Reference/versioning.md) documenting the scheme,
  the run-number patch, assembly binding identity, and how to move to the next minor.

### Notes

- The previous published line was `4.1.x` with `AssemblyVersion` pinned at `3.3.0.0`.
  Assembly identity now moves to `4.8.0.0`, which is a binding-breaking change for
  anything compiled against the old value; recompilation is required.
