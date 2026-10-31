using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using Xunit;

namespace CodeLogic.Tests;

// The test project sets CS8509/CS8655 as errors, so every switch below without a default arm
// is also a compile-time check that the union / closed hierarchy is exhaustive.
public sealed class ResultTests
{
    private static Result<int> Parse(string text) =>
        int.TryParse(text, out var n) ? n : Error.Validation("parse.invalid", $"'{text}' is not a number");

    private static string Describe(Result<int> result) => result switch
    {
        int n => $"ok:{n}",
        Error e => $"error:{e.Code}",
        null => "empty",
    };

    [Fact]
    public void Implicit_conversions_create_success_and_failure()
    {
        Assert.Equal("ok:42", Describe(Parse("42")));
        Assert.Equal("error:parse.invalid", Describe(Parse("x")));
    }

    [Fact]
    public void Legacy_members_still_work()
    {
        var ok = Parse("7");
        Assert.True(ok.IsSuccess);
        Assert.False(ok.IsFailure);
        Assert.Equal(7, ok.Value);
        Assert.Null(ok.Error);

        var fail = Parse("nope");
        Assert.True(fail.IsFailure);
        Assert.IsType<ValidationError>(fail.Error);
        Assert.Equal(0, fail.ValueOrDefault(0));
        Assert.ThrowsAny<InvalidOperationException>(() => fail.ValueOrThrow());   // existing catch blocks keep working
        Assert.IsType<ValidationError>(Assert.Throws<ResultFailedException>(() => fail.ValueOrThrow()).Error);
    }

    [Fact]
    public void ThrowIfFailure_throws_only_for_failures_and_carries_the_error()
    {
        Result.Success().ThrowIfFailure();

        Result failed = Error.Conflict("thing.conflict", "already there");
        var thrown = Assert.Throws<ResultFailedException>(failed.ThrowIfFailure);
        Assert.IsType<ConflictError>(thrown.Error);
        Assert.Equal("thing.conflict", thrown.Error.Code);
        Assert.IsAssignableFrom<InvalidOperationException>(thrown);

        Result empty = default;
        Assert.Equal(Error.Empty, Assert.Throws<ResultFailedException>(empty.ThrowIfFailure).Error);
    }

    [Fact]
    public void Default_result_is_empty_and_reports_Error_Empty()
    {
        Result<int> empty = default;

        Assert.Equal("empty", Describe(empty));
        Assert.False(empty.IsSuccess);
        Assert.False(empty.HasValue);
        Assert.Null(empty.Error);
        Assert.Same(Error.Empty, empty.Map(x => x + 1).Error);
        Assert.Equal("Empty", empty.ToString());
    }

    [Fact]
    public void Null_success_value_matches_null_pattern_but_is_success()
    {
        var result = Result<string?>.Success(null);

        Assert.True(result.IsSuccess);
        Assert.False(result.HasValue);
        Assert.Equal("null", result switch { string => "string", Error => "error", null => "null" });
    }

    [Fact]
    public void Map_Bind_and_Match_short_circuit_on_failure()
    {
        Assert.Equal(84, Parse("42").Map(x => x * 2).Value);
        Assert.Equal("parse.invalid", Parse("x").Map(x => x * 2).Error!.Code);

        var bound = Parse("10").Bind(x => x > 5 ? Result<string>.Success("big") : Error.Validation("small", "too small"));
        Assert.Equal("big", bound.Value);

        Assert.Equal("failed", Parse("x").Match(_ => "parsed", _ => "failed"));
    }

    [Fact]
    public async Task Async_combinators_work()
    {
        var mapped = await Parse("3").MapAsync(async x => { await Task.Yield(); return x.ToString(); });
        Assert.Equal("3", mapped.Value);

        var bound = await Parse("3").BindAsync(async x => { await Task.Yield(); return (Result<int>)(x + 1); });
        Assert.Equal(4, bound.Value);
    }

    [Fact]
    public void Non_generic_result_is_a_union_of_Ok_and_Error()
    {
        Result ok = Result.Success();
        Result fail = Error.Conflict("dup", "already exists");
        Result empty = default;

        static string Show(Result r) => r switch
        {
            Ok => "ok",
            Error e => e.Code,
            null => "empty",
        };

        Assert.Equal("ok", Show(ok));
        Assert.Equal("dup", Show(fail));
        Assert.Equal("empty", Show(empty));
        Assert.Equal("ok", Show(Ok.Value));
    }

    [Fact]
    public void Results_have_value_equality()
    {
        Assert.Equal(Parse("5"), Parse("5"));
        Assert.NotEqual(Parse("5"), Parse("6"));
        Assert.True(Result.Success() == Result.Success());
    }
}

public sealed class ErrorTests
{
    // Exhaustive without a default arm: Error is a closed record.
    private static int HttpStatus(Error error) => error switch
    {
        NotFoundError => 404,
        ValidationError => 422,
        UnauthorizedError => 401,
        ConflictError => 409,
        TimeoutError => 504,
        UnavailableError => 503,
        InternalError => 500,
    };

    [Fact]
    public void Factories_return_the_matching_subtype()
    {
        Assert.Equal(404, HttpStatus(Error.NotFound("a", "b")));
        Assert.Equal(422, HttpStatus(Error.Validation("a", "b")));
        Assert.Equal(401, HttpStatus(Error.Unauthorized("a", "b")));
        Assert.Equal(409, HttpStatus(Error.Conflict("a", "b")));
        Assert.Equal(504, HttpStatus(Error.Timeout("a", "b")));
        Assert.Equal(503, HttpStatus(Error.Unavailable("a", "b")));
        Assert.Equal(500, HttpStatus(Error.Internal("a", "b")));
        Assert.Equal(500, HttpStatus(Error.FromException(new InvalidOperationException("boom"))));
    }

    [Fact]
    public void FromException_keeps_message_and_type_name()
    {
        var error = Error.FromException(new InvalidOperationException("boom"));
        Assert.Equal("internal.exception", error.Code);
        Assert.Equal("boom", error.Message);
        Assert.Equal(nameof(InvalidOperationException), error.Details);
    }

    [Fact]
    public void With_helpers_preserve_the_error_kind()
    {
        var inner = Error.Unavailable("db.down", "database down");
        var error = Error.NotFound("user.not_found", "no user").WithDetails("id=5").WithInner(inner);

        Assert.IsType<NotFoundError>(error);
        Assert.Equal("id=5", error.Details);
        Assert.Same(inner, error.InnerError);
    }

    [Fact]
    public void ToString_format_is_unchanged_from_4x()
    {
        Assert.Equal("[a.b] msg", Error.NotFound("a.b", "msg").ToString());
        Assert.Equal("[a.b] msg (d)", Error.NotFound("a.b", "msg", "d").ToString());
        Assert.Equal(
            "[outer] o → [inner] i",
            Error.Internal("outer", "o", innerError: Error.Internal("inner", "i")).ToString());
    }

    [Fact]
    public void Errors_have_value_equality_including_kind()
    {
        Assert.Equal(Error.NotFound("a", "b"), Error.NotFound("a", "b"));
        Assert.NotEqual(Error.NotFound("a", "b"), Error.Validation("a", "b"));
    }
}

public sealed class FrameworkEventTests
{
    [Fact]
    public void FrameworkEvent_subscribers_receive_every_framework_event()
    {
        var bus = new EventBus();
        var seen = new List<string>();
        var exact = 0;

        bus.Subscribe<FrameworkEvent>(e => seen.Add(e switch
        {
            LibraryStartedEvent s => $"started:{s.LibraryId}",
            ShutdownRequestedEvent r => $"shutdown:{r.Reason}",
            _ => "other",
        }));
        bus.Subscribe<LibraryStartedEvent>(_ => exact++);

        bus.Publish(new LibraryStartedEvent("cl.test", "Test"));
        bus.Publish(new ShutdownRequestedEvent("CTRL+C"));
        bus.Publish(new FrameworkEventTestsCustomEvent());

        Assert.Equal(["started:cl.test", "shutdown:CTRL+C"], seen);
        Assert.Equal(1, exact);
    }

    [Fact]
    public void Publishing_as_FrameworkEvent_is_not_delivered_twice()
    {
        var bus = new EventBus();
        var count = 0;
        bus.Subscribe<FrameworkEvent>(_ => count++);

        bus.Publish<FrameworkEvent>(new LibraryStartedEvent("a", "A"));

        Assert.Equal(1, count);
    }
}

public sealed record FrameworkEventTestsCustomEvent : IEvent;
