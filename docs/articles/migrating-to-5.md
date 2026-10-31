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

## New in 5.0

- Exhaustive `switch` over `Result<T>`, `Result`, `Error` and `FrameworkEvent`.
- `Result<T>.Bind`, `Match`, `MapAsync`, `BindAsync`; `Result.Bind`, `Match`.
- `Ok` (the success case of `Result`) and `Error.Empty`.
- Runtime-native async on .NET 11: shorter async stack traces and lower overhead, with no code changes.
