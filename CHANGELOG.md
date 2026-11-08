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
- **Localization culture names are validated.** `LocalizationManager` (constructor default culture,
  `GenerateTemplatesAsync`, `LoadAsync`, `LoadAllAsync`, `GenerateAllTemplatesAsync`, `ReloadAllAsync`)
  accepts only culture names made of ASCII letters, digits, `-` and `_`, and checks that the file path
  stays inside the localization directory. Anything else (`../x`, `C:`, spaces, empty) throws
  `ArgumentException` instead of reading or writing a file outside the directory.
- **Secret config fields are masked whatever their type.** `GetSchema(..., includeSecrets: false)` now
  replaces every set value of a `[ConfigField(Secret = true)]` field with `ConfigField.SecretMask`:
  numbers, `bool`, `Guid`, `DateTime`, enums and lists of them, not just strings (they used to be
  returned in clear text). `null` and `""` stay visible. Secret enum fields no longer list
  `AllowedValues`. `UpdateSectionAsync` restores an echoed mask in such a field from the stored value
  (lists by position, list entries paired by key like string secrets); a mask with no stored
  counterpart is rejected with a validation error.
- **Library dependency versions use SemVer 2.0.** A `MinVersion` check now compares pre-release
  versions instead of skipping them (`5.0.0-preview.1` no longer satisfies `>= 5.0.0`), and a set
  `MinVersion` whose own value or the loaded library's `Version` cannot be parsed fails dependency
  validation instead of passing silently.
- **`SemanticVersion` follows SemVer 2.0.** It parses `-prerelease` and `+build` suffixes (new
  `Prerelease`, `BuildMetadata`, `IsPrerelease` and a constructor overload taking them), orders by
  SemVer precedence and ignores build metadata for equality. Leading zeros in numeric components and
  signs/whitespace inside components are now rejected, and `ToString()` includes the suffixes.
- `LibraryManager.GetAllLibraries` and `GetLoadedLibraries` return snapshots instead of live views.
- **`CodeLogic.Development.json` is chosen by the environment, not a debugger.** It replaces
  `CodeLogic.json` only when `CODELOGIC_ENVIRONMENT`, `DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT` is
  `Development`, or when the new `CodeLogicOptions.UseDevelopmentConfig` is `true`. Attaching a debugger to a
  production process no longer switches its configuration. `CodeLogicEnvironment.IsDevelopment` follows the
  same rule (it no longer reports `true` for an attached debugger or a DEBUG build of CodeLogic).
- **Plugin directories resolve against the app, not the working directory.** The default
  `PluginOptions.PluginsDirectory` (`CodeLogic/Plugins`) now means `{FrameworkRoot}/Plugins` of the initialized
  runtime (`GetPluginsPath()`), and any other relative path resolves against `AppContext.BaseDirectory`.
  A service started from another directory no longer loads plugins from there.
- **Names used in file names are validated.** `IConfigurationManager.Register<T>(name)` throws
  `ArgumentException`, and registering a library or loading/registering a plugin throws
  `InvalidOperationException`, when the section name or manifest ID contains path separators, `..`, control
  characters, `:`, `*`, `?`, `"`, `<`, `>`, `|`, or leading/trailing whitespace or a trailing dot.
- **The library assembly resolver is restricted.** It serves a missing dependency only from the folder of a
  library discovered under `Libraries/CL.*` (the requesting library's own folder when known), only for a plain
  assembly name, and only when the file carries that name (and public key token, if requested). Libraries
  registered by type no longer make the resolver probe the host's folder.
- **`PluginManifest.MinFrameworkVersion` is enforced.** A plugin whose minimum is newer than the running
  CodeLogic (pre-release builds count as their version) or not a semantic version is not loaded.
- **An invalid `CodeLogic.json` fails `InitializeAsync`**: `healthChecks.intervalSeconds <= 0` (with health
  checks enabled), an empty or invalid `logging.timestampFormat`, `logging.maxFileSizeMb <= 0` or
  `logging.maxRolledFiles < 0`. These used to throw later on the health timer or the first log line.
- **Config schema paths use JSON names.** `GetSchema` field paths use `[JsonPropertyName]` or the camelCase
  naming policy (as the file and `UpdateSectionAsync` do) instead of lower-casing the first letter, and
  `[JsonIgnore]` properties are left out. Paths differ only for such properties (e.g. `URLPath` →
  `urlPath`, was `uRLPath`).
- **Config files are created with mode `0600` on Unix** (owner read/write), whatever the umask. Processes
  running as another user can no longer read them.
- **The centralized debug log no longer needs `enableDebugMode`.** `centralizedDebugLog: true` alone now
  writes `{FrameworkRoot}/Framework/logs/debug/yyyy/MM/dd/debug_all.log` (or under `centralizedLogsPath`).

### Added

- `Result<T>.Bind`, `Match`, `MapAsync`, `BindAsync`; `Result.Bind`, `Match`; `Ok`;
  `Error.Empty`; value equality for `Result` and `Result<T>`.
- `Result.ThrowIfFailure()`, the non-generic counterpart of `ValueOrThrow`.
- `ResultFailedException`, which carries the `Error`. `ValueOrThrow` and `ThrowIfFailure` throw it. It
  derives from `InvalidOperationException`, so existing `catch` blocks keep working; checks for the exact
  type (`Assert.Throws<InvalidOperationException>`) need `ThrowsAny` or the new type.
- `CodeLogicEnvironment.FrameworkVersion`: the CodeLogic assembly's informational version without
  the `+commit` suffix.
- `CodeLogicOptions.UseDevelopmentConfig` and `CodeLogicEnvironment.EnvironmentName`.
- `PluginManager.PluginsDirectory` (the resolved absolute path), `PluginOptions.DefaultPluginsDirectory`,
  `PluginOptions.GenerateLocalizationTemplates` and `LoadedPlugin.IsInProcess`.
- `LoadedLibrary.Dependencies`: manifest dependencies merged with `[LibraryDependency]` attributes.
- `HealthReport.ToJson(bool includeDetails)`: `false` returns only the overall flag, timestamp and status
  levels, for public health endpoints.
- `CodeLogicConfiguration.Validate()` and `LoggingConfig.ToLoggingOptions(string frameworkRootPath)`, which
  fills in the default centralized log directory.
- `ConfigSectionAttribute.Description`, returned as `ConfigSchema.Description` / `ConfigSectionInfo.Description`.
- `--exit-after-generate` CLI flag (sets `CodeLogicOptions.ExitAfterGenerate`).
- `GetSchema` handles `Dictionary<string, scalar>` (one `dict` field, secret values masked), arrays, and
  lists of enums (values and `AllowedValues` as names). `List<Enum>` no longer produces a bogus `capacity` field.

### Changed

- Builds with C# 15. Comparer and capacity collections use collection expression arguments
  (`[with(StringComparer.OrdinalIgnoreCase), ...]`).
- Async methods compile to .NET 11 runtime-native async (shorter stack traces, lower overhead).
- `object` locks are replaced by `System.Threading.Lock`, and `JsonSerializerOptions`
  instances are cached.
- `src` builds with `AnalysisLevel=latest-recommended`.
- `global.json` pins the .NET 11 SDK, and CI installs it from there.
- CI also builds `samples/CodeLogic.Demo.Console` and `samples/CodeLogic.Demo.Web`, so public API
  breaks show up in pull requests.
- `--version`, `--info`, the generated `CodeLogic.json`, the first-run marker and the
  `FrameworkConfig.Version` default report `CodeLogicEnvironment.FrameworkVersion` instead of the
  hardcoded `3.0.0`.
- `PluginManager` unload no longer forces ten blocking full garbage collections while holding
  the lifecycle lock. They could never complete the unload (the returned `LoadedPlugin` still
  references the load context) and only stalled the process for 100 ms or more per plugin. The context
  is collected once nothing references the plugin's types.

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
- **Plugin reentrancy detection is per manager.** The flow marker was a static shared by every
  `PluginManager`: when a callback of manager A called manager B, and a callback of B called back into
  A, A's check missed it and the call deadlocked on A's lifecycle lock. It now throws
  `InvalidOperationException` like any other reentrant call; independent managers can still nest calls.
- **`LibraryManager` readers are safe during registration.** The library list is a copy-on-write
  snapshot, so the assembly resolver, health checks, config watcher and accessors no longer throw
  "Collection was modified" (or read a torn list) while a library is being registered.
- **`LocalizationManager.ReloadAllAsync` no longer empties the cache first.** It loads into a new
  snapshot and swaps it in, so concurrent `Get<T>` calls keep working during a reload and after a
  failed one. Loads are serialized and `Get<T>` reads an immutable snapshot.
- **`CodeLogicRuntime.ResetAsync` clears all run state.** It unregisters the CTRL+C / process-exit
  handlers (re-initializing registers them again as configured), drops every event bus subscription
  made during the previous run, and detaches the framework logger.
- **Masked secrets are never written to disk.** `UpdateSectionAsync` used to save `••••••••` over the real
  value when a masked secret sat in a list entry that could not be paired with a stored one (a new entry, or
  an entry of a list without an `Id`/`Name`/`Key` property whose length changed), and skipped `IList<T>`,
  `IReadOnlyList<T>`, array, `IDictionary<,>` and `IReadOnlyDictionary<,>` properties entirely. Those
  properties are now restored like `List<T>`/`Dictionary<,>`, and any mask left after restoring rejects the
  update with a validation error naming the field (`FieldErrors`); nothing is written.
- `ConfigurationManager.Register` is safe to call concurrently with readers (copy-on-write registrations).
- **`StopAsync` runs once.** `IApplication.OnStopAsync` (and plugin unload/library stop) no longer run twice
  when `StopAsync` is called explicitly and again by the process-exit handler. `StartAsync` re-arms it.
- `RegisterInProcessPluginAsync` works in single-file apps (it no longer builds a load context from the empty
  `Assembly.Location`), and in-process plugins are never hot-reloaded.
- `[LibraryDependency]` attributes are honoured for startup ordering and `MinVersion` validation, as documented.
- `localization.autoGenerateTemplates: false` is honoured: the application, libraries and plugins write no
  template files and use the models' built-in strings for missing cultures.
- `LocalizationReloadedEvent` is published after `ReloadAllAsync` on framework-created localization managers.
- `InitializationResult.IsFirstRun` reports first-run scaffolding.
- `ConfigModelBase.Validate()` reports DataAnnotations errors on top-level properties in `FieldErrors` too.

### Documentation

- Plugin docs and README examples use the real API (`LoadPluginAsync`/`UnloadPluginAsync`/
  `ReloadPluginAsync`, the actual `PluginManager` constructor, `PluginState` values and manifest
  members), and the isolation section describes what the load context does and does not isolate.
- The Core, Framework and Reference guides and the in-depth getting-started page are now part of the
  docs site.
- The web demo's config endpoints carry authentication/authorization guidance.
- Configuration docs describe secret masking and restore rules, and that secrets are stored **in clear text**
  in `config*.json` (owner-only mode on Unix; restrict the directory ACL on Windows).
- `ConfigFieldAttribute.Required`/`Min`/`Max`/`MaxLength`/`AllowedValues`, `LibraryManifest.Description`/`Tags`,
  `IPlugin.State`, `PluginState.Discovered` and `LoadedPlugin.WeakReference` are documented as hints or
  informational: the framework does not enforce or read them.
- The web demo's event endpoints accept local requests only, limit input and never log caller text at
  Error; its public `/health` returns status levels only (details are local-only).
- All guides were checked against the 5.x source. Examples now use exhaustive `switch` on results,
  errors and `FrameworkEvent`, and the guides no longer describe APIs that don't exist (for example
  `IEventSubscription` members, localization `SetCultureAsync`, made-up `HealthReport` members).
  `[ConfigSection]` is documented correctly: it does not set the config file name.
- The demos show 5.x idioms: the console demo subscribes to `FrameworkEvent` and handles a
  `Result<int>` with an exhaustive `switch`, and the web demo maps a `Result<T>` and the `Error` kind
  to HTTP status codes (`GET /plugins/{id}`).

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
