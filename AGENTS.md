# CodeLogic: guidance for agents and contributors

CodeLogic is a modular .NET framework: lifecycle-managed libraries, applications and plugins, with
configuration, localization, logging, events and health checks. It has **zero NuGet dependencies**,
so keep it that way. The `CodeLogic.*` library family lives in
[zyntal-com/CodeLogic.Libs](https://github.com/zyntal-com/CodeLogic.Libs) and depends on this package.

## Release lines and branches

There is one release line per major version. Know which line you are working on before changing anything.

| Line | Target | Integration branch | Preview channel | Stable channel | `version.txt` |
|------|--------|--------------------|-----------------|----------------|---------------|
| 5.x | .NET 11, C# 15 | `main` | `prerelease/5.x` | `release/5.x` | `5.0` |
| 4.x | .NET 10, C# 13 | `support/4.x` | `prerelease/4.x` | `release/4.x` | `4.9` |

- **Integration branches** (`main`, `support/**`) take changes through pull requests only, and the
  required checks must pass.
- **Channel branches** (`release/**`, `prerelease/**`) are protected by a repository ruleset. Only
  the **Promote** workflow can write to them: it pushes with the `PROMOTE_DEPLOY_KEY` deploy key,
  which is the ruleset's only bypass. Never try to push there, and never ask anyone to switch the
  ruleset off.
- **Shipping a change:**
  1. Open a PR into the line's integration branch.
  2. Once it's merged, run **Promote** from the Actions tab (`source` = `main` or `support/4.x`,
     `channel` = `prerelease` or `release`).
  3. `release.yml` re-runs the suite, then publishes `<major.minor>.<run>[-preview]`.
- **Fixes for both lines:** make the fix on one integration branch, then cherry-pick it in a
  second PR into the other.
- **Branch creation never publishes.** `release.yml` skips `push` events that create a branch.
  Promote dispatches the release itself when it creates a channel branch.
- **Full description:** [`docs/Reference/versioning.md`](docs/Reference/versioning.md).

**Publishing to nuget.org is irreversible.** Never merge a PR or run Promote unless the repository
owner explicitly asked for that specific merge or release.

## Building and testing

```sh
dotnet build src/CodeLogic.csproj -c Release          # must be 0 warnings
dotnet test tests/CodeLogic.Tests -c Release
dotnet build samples/CodeLogic.Demo.Console -c Release
dotnet build samples/CodeLogic.Demo.Web -c Release
dotnet run -c Release --project benchmarks/CodeLogic.Benchmarks -- --filter '*'   # see benchmarks/README.md
```

- `global.json` pins the SDK: the .NET 11 SDK on `main` (prerelease allowed until GA,
  `rollForward: latestFeature`). CI installs the SDK from `global.json`.
- `src` builds with `AnalysisLevel=latest-recommended`. Deliberate exceptions are in
  `.editorconfig`, each with its reason. Fix new warnings instead of suppressing them.
- **Before changing a public API, check the downstream build.** Pack locally and build
  CodeLogic.Libs against the package:
  1. `dotnet pack src/CodeLogic.csproj -c Release -o <feed> -p:Version=5.0.0-local.1`
  2. In CodeLogic.Libs, build with
     `-p:CodeLogicFromNuGet=true -p:CodeLogicPackageVersion=5.0.0-local.1 "-p:RestoreAdditionalProjectSources=<feed>"`.

## Conventions (5.x)

- **Results.** `Result<T>` is a C# 15 union of `T | Error`, and `Result` a union of `Ok | Error`.
  - `Error` is a `closed record` with one subtype per kind (`NotFoundError`, `ValidationError`,
    `InternalError`, `UnauthorizedError`, `ConflictError`, `TimeoutError`, `UnavailableError`).
  - Return a `Result` for expected failures; throw only for bugs.
  - Use the `Error.*` factories with `area.snake_case` codes.
  - Prefer an exhaustive `switch` over `IsSuccess` chains. Tests treat CS8509/CS8655 (non-exhaustive
    switch) as errors.
- **Closed hierarchies.** `Error`, `FrameworkEvent` and, in the libs, `StorageConnectionConfigBase`
  are closed. Adding a case is a breaking change, and every exhaustive `switch` must handle it.
- **Events.** Framework events derive from `closed record FrameworkEvent`; application and library
  events implement `IEvent`.
  - `EventBus.Publish` is lock-free and allocation-free over a copy-on-write snapshot. Keep it that
    way: no locks or allocations on the publish path.
- **Logging.**
  - Log calls only queue the line. `LogFileWriter` writes in the background and flushes on
    `StopAsync`, process exit and unhandled exceptions.
  - Never do file I/O on the caller's thread.
  - Use interpolated strings (`logger.Debug($"…{x}…")`): disabled levels don't evaluate the holes.
  - Gate expensive message building with `logger.IsEnabled(level)`.
  - Use the invariant culture for anything that ends up in a file name or path.
- **Modern C#.**
  - Collection expressions, including `[with(StringComparer.Ordinal), …]` for comparers and capacity.
  - `System.Threading.Lock` instead of `object` locks.
  - Cached `JsonSerializerOptions`.
  - `ArgumentException.ThrowIf…` helpers.
- **Compatibility.** Within a major line, changes must be source-compatible. New interface members
  get default implementations. Breaking changes wait for the next major and go into the migration
  guide (`docs/articles/migrating-to-5.md` for 4 → 5).
- **Changelog.** Every user-visible change gets a line in `CHANGELOG.md` under the line's section.
  NuGet release notes are generated from it.

## 4.x line (`support/4.x`)

The 4.x line takes **fixes only**: no new features and no API changes. It targets `net10.0` with C# 13
and builds with the .NET 10 SDK. Don't bring 5.x-only constructs (unions, `closed`, C# 15 syntax) into it.
