using CodeLogic;
using Xunit;

namespace CodeLogic.Tests;

public sealed class StartupParameterStoreTests
{
    [Theory]
    [InlineData("generate-configs")]
    [InlineData("generate-configs-force")]
    [InlineData("exit-after-generate")]
    [InlineData("dry-run")]
    [InlineData("version")]
    [InlineData("info")]
    [InlineData("health")]
    public void Framework_flags_are_not_startup_parameters(string flag)
    {
        var store = StartupParameterStore.From(["--" + flag, "--app-mode", "x"], new Dictionary<string, string>());

        Assert.Null(store.Get<string>(flag));
        Assert.Equal("x", store.Get<string>("app-mode"));
    }

    [Fact]
    public void Command_line_wins_and_final_occurrence_is_used()
    {
        var store = StartupParameterStore.From(
            ["--mysql-host", "first", "--MYSQL-HOST=final"],
            new Dictionary<string, string> { ["MYSQL_HOST"] = "environment" });

        Assert.Equal("final", store.GetRequired<string>("mysql-host"));
    }

    [Fact]
    public void Environment_name_is_normalized_and_case_insensitive()
    {
        var store = StartupParameterStore.From(
            [], new Dictionary<string, string> { ["mysql_host"] = "db.internal" });

        Assert.Equal("db.internal", store.GetRequired<string>("MySql-Host"));
    }

    [Fact]
    public void Supports_typed_values_defaults_and_missing_required_values()
    {
        var store = StartupParameterStore.From(
            ["--enabled=true", "--count", "42", "--id", "6f9619ff-8b86-d011-b42d-00c04fc964ff", "--delay", "00:00:05"],
            new Dictionary<string, string>());

        Assert.True(store.GetRequired<bool>("enabled"));
        Assert.Equal(42, store.GetRequired<int>("count"));
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), store.GetRequired<Guid>("id"));
        Assert.Equal(TimeSpan.FromSeconds(5), store.GetRequired<TimeSpan>("delay"));
        Assert.Equal(9, store.Get("missing", 9));
        Assert.Throws<InvalidOperationException>(() => store.GetRequired<string>("missing"));
    }

    [Fact]
    public void Invalid_values_do_not_echo_the_supplied_value()
    {
        var store = StartupParameterStore.From(
            ["--port", "not-a-secret-number"], new Dictionary<string, string>());

        var error = Assert.Throws<InvalidOperationException>(() => store.GetRequired<int>("port"));
        Assert.DoesNotContain("not-a-secret-number", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reserved_control_flags_are_not_application_parameters()
    {
        var store = StartupParameterStore.From(
            ["--version"], new Dictionary<string, string> { ["VERSION"] = "not-for-apps" });

        Assert.Equal("fallback", store.Get("version", "fallback"));
    }
}
