# Changelog

## Unreleased (4.9)

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
