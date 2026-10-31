using CodeLogic.Core.Events;
using CodeLogic.Core.Utilities;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace CodeLogic.Tests;

/// <summary>Registration snapshots and dependency version checks of <see cref="LibraryManager"/>.</summary>
public sealed class LibraryManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_libmgr_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Readers never see the library list being modified ────────────────────

    [Fact]
    public async Task Readers_can_enumerate_while_libraries_are_registered()
    {
        using var manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        using var stop = new CancellationTokenSource();
        Exception? failure = null;

        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    // Slow enumeration widens the window in which a registration lands mid-loop.
                    foreach (var library in manager.GetAllLibraries()) { _ = library.Manifest.Id; Thread.SpinWait(2000); }
                    foreach (var loaded in manager.GetLoadedLibraries()) { _ = loaded.State; Thread.SpinWait(2000); }
                    _ = manager.GetLibrary<CountingLibrary>();
                    _ = manager.GetAllConfigSections();
                    _ = await manager.GetHealthAsync();
                }
                catch (Exception ex) { failure ??= ex; }
            }
        })).ToArray();

        for (var i = 0; i < 300; i++)
            Assert.True(await manager.LoadLibraryAsync<CountingLibrary>());

        stop.Cancel();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(failure); // List<T> enumeration threw "Collection was modified"
        Assert.Equal(300, manager.GetLoadedLibraries().Count());
    }

    [Fact]
    public async Task GetLoadedLibraries_returns_a_snapshot()
    {
        using var manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await manager.LoadLibraryAsync<CountingLibrary>();
        var snapshot = manager.GetLoadedLibraries();

        await manager.LoadLibraryAsync<CountingLibrary>();

        Assert.Single(snapshot);
        Assert.Equal(2, manager.GetLoadedLibraries().Count());
    }

    // ── Dependency MinVersion checks ─────────────────────────────────────────

    [Theory]
    [InlineData("5.0.0-preview.1", "5.0.0", false)]   // pre-release is below the release
    [InlineData("5.0.0-preview.2", "5.0.0-preview.10", false)]
    [InlineData("5.0.0-rc.1", "5.0.0-preview.9", true)]
    [InlineData("5.0.0", "5.0.0-rc.1", true)]
    [InlineData("5.1.0-preview", "5.0.3", true)]
    [InlineData("4.9.101", "5.0.0-preview.1", false)]
    public async Task Prerelease_versions_take_part_in_the_minimum_version_check(string installed, string minimum, bool ok)
    {
        ProviderLibrary.Version = installed;
        ConsumerLibrary.MinVersion = minimum;
        using var manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await manager.LoadLibraryAsync<ProviderLibrary>();
        await manager.LoadLibraryAsync<ConsumerLibrary>();

        var error = await Record.ExceptionAsync(() => manager.ConfigureAllAsync());

        if (ok)
        {
            Assert.Null(error);
        }
        else
        {
            var ex = Assert.IsType<InvalidOperationException>(error);
            Assert.Contains($">= {minimum}", ex.Message);
        }
    }

    [Theory]
    [InlineData("not-a-version", "1.0.0", "loaded version 'not-a-version' is not a valid semantic version")]
    [InlineData("1.0.0", "1.0", "invalid minimum version '1.0'")]
    public async Task Unparseable_versions_fail_validation_instead_of_passing(string installed, string minimum, string expected)
    {
        ProviderLibrary.Version = installed;
        ConsumerLibrary.MinVersion = minimum;
        using var manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await manager.LoadLibraryAsync<ProviderLibrary>();
        await manager.LoadLibraryAsync<ConsumerLibrary>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfigureAllAsync());

        Assert.Contains(expected, ex.Message);
    }

    // ── SemanticVersion ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "", "")]
    [InlineData("5.0.0-preview.1", 5, 0, 0, "preview.1", "")]
    [InlineData("5.0.0-rc.2+sha.5114f85", 5, 0, 0, "rc.2", "sha.5114f85")]
    [InlineData("1.0.0+001", 1, 0, 0, "", "001")]
    [InlineData("1.0.0-alpha-beta.x-y", 1, 0, 0, "alpha-beta.x-y", "")]
    [InlineData(" 2.0.0 ", 2, 0, 0, "", "")]
    public void SemanticVersion_parses_semver_2(string text, int major, int minor, int patch, string pre, string build)
    {
        var v = SemanticVersion.Parse(text);

        Assert.Equal((major, minor, patch, pre, build), (v.Major, v.Minor, v.Patch, v.Prerelease, v.BuildMetadata));
        Assert.Equal(pre.Length > 0, v.IsPrerelease);
        Assert.Equal(text.Trim(), v.ToString());
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-a_b")]
    [InlineData("v1.2.3")]
    [InlineData("+1.2.3")]
    public void SemanticVersion_rejects_invalid_versions(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out var parsed));
        Assert.Null(parsed);
        Assert.Throws<FormatException>(() => SemanticVersion.Parse(text));
    }

    [Fact]
    public void SemanticVersion_orders_by_semver_precedence()
    {
        // The example chain from semver.org section 11.
        string[] ordered =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
            "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"
        ];
        for (var i = 0; i < ordered.Length - 1; i++)
            Assert.True(SemanticVersion.Parse(ordered[i]) < SemanticVersion.Parse(ordered[i + 1]),
                $"{ordered[i]} < {ordered[i + 1]}");

        var a = SemanticVersion.Parse("1.0.0-rc.1+build.1");
        var b = SemanticVersion.Parse("1.0.0-rc.1+build.2");
        Assert.Equal(a, b); // build metadata does not affect precedence
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(SemanticVersion.Parse("1.0.0-99999999999999999999") < SemanticVersion.Parse("1.0.0-100000000000000000000"));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private abstract class TestLibrary : ILibrary
    {
        public abstract LibraryManifest Manifest { get; }
        public Task OnConfigureAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnInitializeAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnStopAsync() => Task.CompletedTask;
        public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy());
        public void Dispose() { }
    }

    /// <summary>Every instance gets its own ID, so one type can be registered many times.</summary>
    private sealed class CountingLibrary : TestLibrary
    {
        private static int _next;
        public override LibraryManifest Manifest { get; } = new()
        {
            Id = $"CL.Counting{Interlocked.Increment(ref _next)}",
            Name = "Counting",
            Version = "1.0.0"
        };
    }

    private sealed class ProviderLibrary : TestLibrary
    {
        public static string Version = "1.0.0";
        public override LibraryManifest Manifest { get; } = new() { Id = "CL.Provider", Name = "Provider", Version = Version };
    }

    private sealed class ConsumerLibrary : TestLibrary
    {
        public static string MinVersion = "1.0.0";
        public override LibraryManifest Manifest { get; } = new()
        {
            Id = "CL.Consumer",
            Name = "Consumer",
            Version = "1.0.0",
            Dependencies = [LibraryDependency.Required("CL.Provider", MinVersion)]
        };
    }
}
