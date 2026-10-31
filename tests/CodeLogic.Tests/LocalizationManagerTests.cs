using CodeLogic.Core.Localization;
using Xunit;

namespace CodeLogic.Tests;

/// <summary>Culture-name validation and snapshot reloads of <see cref="LocalizationManager"/>.</summary>
public sealed class LocalizationManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_loc_" + Guid.NewGuid().ToString("N"));
    private readonly string _dir;

    public LocalizationManagerTests() => _dir = Path.Combine(_root, "localization");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [LocalizationSection("greet")]
    private sealed class Greetings : LocalizationModelBase
    {
        public string Hello { get; set; } = "Hello";
    }

    // ── Culture names are validated before they reach a file path ────────────

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("en-US/../../evil")]
    [InlineData("C:")]
    [InlineData("en US")]
    [InlineData("")]
    public async Task Culture_names_that_are_not_plain_tags_are_rejected_and_write_nothing(string culture)
    {
        var loc = new LocalizationManager(_dir);
        loc.Register<Greetings>();

        await Assert.ThrowsAsync<ArgumentException>(() => loc.GenerateTemplatesAsync<Greetings>([culture]));
        await Assert.ThrowsAsync<ArgumentException>(() => loc.LoadAsync<Greetings>([culture]));
        await Assert.ThrowsAsync<ArgumentException>(() => loc.LoadAllAsync([culture]));
        await Assert.ThrowsAsync<ArgumentException>(() => loc.GenerateAllTemplatesAsync([culture]));

        Assert.Empty(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Invalid_default_culture_is_rejected_by_the_constructor()
    {
        Assert.Throws<ArgumentException>(() => new LocalizationManager(_dir, "../../x"));
    }

    [Fact]
    public async Task Regular_culture_tags_are_accepted()
    {
        var loc = new LocalizationManager(_dir);
        loc.Register<Greetings>();

        await loc.LoadAllAsync(["en-US", "zh-Hans-CN", "de-DE_phoneb"]);

        Assert.Equal(["en-US", "zh-Hans-CN", "de-DE_phoneb"], loc.GetLoadedCultures<Greetings>());
        Assert.True(File.Exists(Path.Combine(_dir, "greet.zh-Hans-CN.json")));
    }

    // ── Reloads publish a complete snapshot ──────────────────────────────────

    [Fact]
    public async Task Get_keeps_working_while_ReloadAllAsync_runs()
    {
        var loc = new LocalizationManager(_dir);
        loc.Register<Greetings>();
        await loc.LoadAllAsync(["en-US", "da-DK"]);

        using var stop = new CancellationTokenSource();
        var failures = 0;
        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    _ = loc.Get<Greetings>("da-DK").Hello;
                    _ = loc.Get<Greetings>().Hello;
                }
                catch { Interlocked.Increment(ref failures); }
            }
        })).ToArray();

        for (var i = 0; i < 40; i++)
            await loc.ReloadAllAsync(["en-US", "da-DK"]).WaitAsync(TimeSpan.FromSeconds(10));

        stop.Cancel();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, failures); // the old code cleared the cache first: readers saw "not loaded"
    }

    [Fact]
    public async Task A_failed_reload_keeps_the_previous_translations()
    {
        var loc = new LocalizationManager(_dir);
        loc.Register<Greetings>();
        await File.WriteAllTextAsync(Path.Combine(Directory.CreateDirectory(_dir).FullName, "greet.en-US.json"),
            """{ "hello": "Hi" }""");
        await loc.LoadAllAsync(["en-US"]);

        await File.WriteAllTextAsync(Path.Combine(_dir, "greet.en-US.json"), "{ not json");
        await Assert.ThrowsAnyAsync<Exception>(() => loc.ReloadAllAsync(["en-US"]));

        Assert.Equal("Hi", loc.Get<Greetings>().Hello);
    }

    [Fact]
    public async Task A_reload_picks_up_edited_files()
    {
        var loc = new LocalizationManager(_dir);
        loc.Register<Greetings>();
        await loc.LoadAllAsync(["en-US"]);

        await File.WriteAllTextAsync(Path.Combine(_dir, "greet.en-US.json"), """{ "hello": "Howdy" }""");
        await loc.ReloadAllAsync(["en-US"]);

        Assert.Equal("Howdy", loc.Get<Greetings>().Hello);
    }
}
