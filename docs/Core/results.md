# Results

CodeLogic provides `Result<T>` and `Result` as structured return types for operations that can fail. Use these instead of throwing exceptions for expected failure cases.

---

## Pattern matching (5.0, C# 15)

Since 5.0, `Result<T>` is a C# 15 **union** of `T` and `Error`, `Result` is a union of `Ok` and `Error`, and `Error` is a **closed** record whose kinds are fixed subtypes. The compiler checks that a `switch` handles every case:

```csharp
IResult ToHttp(Result<User> result) => result switch
{
    User user               => Results.Ok(user),
    NotFoundError e         => Results.NotFound(e.Message),
    ValidationError e       => Results.UnprocessableEntity(e.Message),
    Error e                 => Results.Problem(e.Message),   // every other kind
    null                    => Results.Problem("empty result"),
};

int StatusCode(Error error) => error switch   // no default arm needed
{
    NotFoundError     => 404,
    ValidationError   => 422,
    UnauthorizedError => 401,
    ConflictError     => 409,
    TimeoutError      => 504,
    UnavailableError  => 503,
    InternalError     => 500,
};
```

The `null` arm covers `default(Result<T>)` (never assigned) and a success whose value is `null`. `IsSuccess`, `Value`, `Error`, `Map`, `OnSuccess`, `ValueOrThrow` and the other 4.x members are unchanged, so existing code keeps compiling. New in 5.0: `Bind`, `Match`, `MapAsync`, `BindAsync`, value equality, and `Error.Empty`, which a `default` result reports from `Map`/`OnFailure`/`ToResult`.

See [Migrating to 5.0](../articles/migrating-to-5.md) for the breaking changes.

---

## Result\<T\>

A discriminated union containing either a success value or an `Error`:

```csharp
public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T? Value { get; }
    public Error? Error { get; }

    public static Result<T> Success(T value);
    public static Result<T> Failure(Error error);

    // Implicit conversions
    public static implicit operator Result<T>(T value);    // T → Success(value)
    public static implicit operator Result<T>(Error error); // Error → Failure(error)

    // Transformation
    public Result<TOut> Map<TOut>(Func<T, TOut> mapper);
    public Result ToResult();

    // Callbacks
    public Result<T> OnSuccess(Action<T> action);
    public Result<T> OnFailure(Action<Error> action);

    // Unwrapping
    public T ValueOrDefault(T defaultValue);
    public T ValueOrThrow();
}
```

### Usage

```csharp
// Return a result
public Result<User> FindUser(string id)
{
    var user = _db.Find(id);
    if (user == null)
        return Error.NotFound(ErrorCode.NotFound, $"User '{id}' not found");

    return user; // implicit: Result<User>.Success(user)
}

// Check and handle
var result = FindUser("user-123");

if (result.IsFailure)
{
    logger.Warning($"User lookup failed: {result.Error}");
    return;
}

var user = result.Value!;
```

### Implicit conversions

```csharp
// These are equivalent:
return Result<User>.Success(user);
return user; // implicit conversion — cleaner

return Result<User>.Failure(error);
return error; // implicit conversion — cleaner
```

### Map

Transform a success value without unwrapping:

```csharp
Result<string> emailResult = FindUser("user-123")
    .Map(u => u.Email);
```

If the original result was a failure, `Map` propagates the failure without calling the mapper.

### Callbacks

```csharp
FindUser("user-123")
    .OnSuccess(u => logger.Info($"Found: {u.Email}"))
    .OnFailure(e => logger.Warning($"Failed: {e}"));
```

### ValueOrDefault and ValueOrThrow

```csharp
// Use a fallback on failure (no exception)
var user = FindUser("user-123").ValueOrDefault(User.Guest);

// Throw on failure (use sparingly — prefer explicit error handling)
var user = FindUser("user-123").ValueOrThrow();
```

---

## Result (non-generic)

For operations that succeed or fail without a return value:

```csharp
public readonly struct Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error? Error { get; }

    public static Result Success();
    public static Result Failure(Error error);

    public static implicit operator Result(Error error);

    public Result OnSuccess(Action action);
    public Result OnFailure(Action<Error> action);
}
```

```csharp
public Result DeleteUser(string id)
{
    if (!_db.Exists(id))
        return Error.NotFound(ErrorCode.NotFound, $"User '{id}' not found");

    _db.Delete(id);
    return Result.Success();
}

var result = DeleteUser("user-123");
if (result.IsFailure)
    return result.Error!;
```

---

## Error

Represents a structured, serializable error:

```csharp
public sealed class Error
{
    public string Code { get; }     // "category.specific_issue"
    public string Message { get; }  // human-readable description
    public string? Details { get; } // optional extra context
    public Error? InnerError { get; } // error chaining
}
```

### Factory methods

```csharp
Error.NotFound("user.not_found", "User not found", $"Id={id}")
Error.Validation("email.invalid", "Email address is invalid", email)
Error.Internal("db.query_failed", "Database query failed", innerError: dbError)
Error.Unauthorized("auth.token_expired", "Access token has expired")
Error.Conflict("user.duplicate", "A user with this email already exists")
Error.Timeout("api.timeout", "API call timed out after 30s")
Error.Unavailable("db.unavailable", "Database is currently unavailable")
Error.FromException(ex)  // wraps exception.Message as an internal error
```

### Chaining

```csharp
var dbError = Error.Internal("db.query_failed", "Query failed");
var appError = Error.Internal("user.create_failed", "Could not create user")
    .WithInner(dbError);

// ToString: [user.create_failed] Could not create user → [db.query_failed] Query failed
```

### Adding details

```csharp
var error = Error.NotFound(ErrorCode.NotFound, "Resource not found")
    .WithDetails($"Searched for Id={id} in table={table}");
```

---

## ErrorCode Constants

Predefined codes in the `ErrorCode` static class:

### General

| Constant | Value | Use for |
|----------|-------|---------|
| `ErrorCode.NotFound` | `"not_found"` | Resource does not exist |
| `ErrorCode.AlreadyExists` | `"already_exists"` | Duplicate resource |
| `ErrorCode.InvalidArgument` | `"invalid_argument"` | Bad input argument |
| `ErrorCode.InvalidState` | `"invalid_state"` | Wrong state for operation |
| `ErrorCode.NotSupported` | `"not_supported"` | Not implemented |
| `ErrorCode.Cancelled` | `"cancelled"` | Operation was cancelled |
| `ErrorCode.Timeout` | `"timeout"` | Timed out |
| `ErrorCode.Internal` | `"internal"` | Unexpected error |
| `ErrorCode.Unauthorized` | `"unauthorized"` | Not authenticated |
| `ErrorCode.Forbidden` | `"forbidden"` | Authenticated but no permission |

### Validation

| Constant | Value |
|----------|-------|
| `ErrorCode.ValidationFailed` | `"validation.failed"` |
| `ErrorCode.RequiredMissing` | `"validation.required_missing"` |
| `ErrorCode.InvalidFormat` | `"validation.invalid_format"` |
| `ErrorCode.OutOfRange` | `"validation.out_of_range"` |

### Configuration

| Constant | Value |
|----------|-------|
| `ErrorCode.ConfigNotFound` | `"config.not_found"` |
| `ErrorCode.ConfigInvalid` | `"config.invalid"` |
| `ErrorCode.ConfigLoadFailed` | `"config.load_failed"` |

### IO

| Constant | Value |
|----------|-------|
| `ErrorCode.FileNotFound` | `"io.file_not_found"` |
| `ErrorCode.FileReadFailed` | `"io.read_failed"` |
| `ErrorCode.FileWriteFailed` | `"io.write_failed"` |

### Network / Connection

| Constant | Value |
|----------|-------|
| `ErrorCode.ConnectionFailed` | `"connection.failed"` |
| `ErrorCode.ConnectionLost` | `"connection.lost"` |
| `ErrorCode.ConnectionTimeout` | `"connection.timeout"` |

### Framework

| Constant | Value |
|----------|-------|
| `ErrorCode.NotInitialized` | `"framework.not_initialized"` |
| `ErrorCode.AlreadyStarted` | `"framework.already_started"` |
| `ErrorCode.LibraryNotFound` | `"framework.library_not_found"` |
| `ErrorCode.PluginNotFound` | `"framework.plugin_not_found"` |

---

## Complete Example

```csharp
public class UserService
{
    private readonly IDatabase _db;
    private readonly ILogger _log;

    public Result<User> CreateUser(string email, string name)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Error.Validation(ErrorCode.RequiredMissing, "Email is required");

        if (!email.Contains('@'))
            return Error.Validation(ErrorCode.InvalidFormat, "Email is invalid", email);

        if (_db.UserExists(email))
            return Error.Conflict(ErrorCode.AlreadyExists, "User already exists", email);

        try
        {
            var user = _db.CreateUser(email, name);
            return user; // implicit Success
        }
        catch (Exception ex)
        {
            _log.Error("Failed to create user", ex);
            return Error.FromException(ex, "user.create_failed");
        }
    }
}

// Caller
var result = userService.CreateUser("alice@example.com", "Alice");

result
    .OnSuccess(u => logger.Info($"Created user {u.Id}"))
    .OnFailure(e => logger.Warning($"Create failed: {e}"));

if (result.IsSuccess)
{
    var user = result.Value!;
}
```
