using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace CodeLogic.Tests;

public sealed class ConfigReloadTests
{
    // ── ConfigDiff ───────────────────────────────────────────────────────────

    [Fact]
    public void Keyed_diff_splits_added_removed_changed_and_unchanged()
    {
        var old = new Dictionary<string, DbConfig>
        {
            ["A"] = new() { Host = "a" },
            ["B"] = new() { Host = "b" },
            ["C"] = new() { Host = "c" }
        };
        var @new = new Dictionary<string, DbConfig>
        {
            ["A"] = new() { Host = "a" },
            ["B"] = new() { Host = "b2" },
            ["D"] = new() { Host = "d" }
        };

        var diff = ConfigDiff.Keyed(old, @new);

        Assert.Equal(["D"], diff.Added.Keys);
        Assert.Equal(["C"], diff.Removed.Keys);
        Assert.Equal(["B"], diff.Changed.Keys);
        Assert.Equal("b", diff.Changed["B"].Old.Host);
        Assert.Equal("b2", diff.Changed["B"].New.Host);
        Assert.Equal(["A"], diff.Unchanged.Keys);
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void Changed_properties_use_schema_paths_and_detect_restart_fields()
    {
        var old = new KeyedConfig();
        var @new = new KeyedConfig();
        @new.Databases["A"].Host = "elsewhere";
        @new.Databases["B"].PoolSize = 99;

        var changed = ConfigDiff.ChangedProperties(old, @new);

        Assert.Equal(["databases.A.host", "databases.B.poolSize"], changed.Order());
        Assert.True(ConfigDiff.RequiresRestart(typeof(KeyedConfig), ["databases.A.host"]));
        Assert.False(ConfigDiff.RequiresRestart(typeof(KeyedConfig), ["databases.B.poolSize"]));
    }

    // ── Per-instance changes ─────────────────────────────────────────────────

    [Fact]
    public async Task Updating_one_instance_reports_only_that_instance_as_changed()
    {
        await using var fixture = await StartedFixture.CreateAsync<ReloadableLibrary>();
        var library = fixture.Library<ReloadableLibrary>();

        var payload = JsonNode.Parse(await File.ReadAllTextAsync(fixture.KeyedPath))!;
        payload["databases"]!["B"]!["host"] = "db-b.new";

        var result = await fixture.Manager.UpdateConfigAsync("CL.Fake", "db", payload.ToJsonString(), "admin");

        Assert.True(result.Applied, result.ToString());
        var change = Assert.Single(library.Changes);
        Assert.Equal("db", change.SectionName);
        Assert.Equal(ConfigChangeKind.Updated, change.Kind);
        Assert.Equal("admin", change.ChangedBy);

        var diff = ConfigDiff.Keyed(change.GetOld<KeyedConfig>()!.Databases, change.GetNew<KeyedConfig>()!.Databases);
        Assert.Equal(["B"], diff.Changed.Keys);
        Assert.Equal(["A", "C"], diff.Unchanged.Keys.Order());
        Assert.Contains("db-b.new", await File.ReadAllTextAsync(fixture.KeyedPath));
    }

    [Fact]
    public async Task Saving_a_mutated_Get_instance_still_has_a_detached_old_snapshot()
    {
        await using var fixture = await StartedFixture.CreateAsync<ReloadableLibrary>();
        var library = fixture.Library<ReloadableLibrary>();
        var configuration = fixture.Context.Configuration;

        var config = configuration.Get<KeyedConfig>();
        config.Databases["C"].PoolSize = 42; // in-place mutation of the live instance
        await configuration.SaveAsync(config);

        var change = Assert.Single(library.Changes);
        var diff = ConfigDiff.Keyed(change.GetOld<KeyedConfig>()!.Databases, change.GetNew<KeyedConfig>()!.Databases);
        Assert.Equal(["C"], diff.Changed.Keys);
        Assert.Equal(10, diff.Changed["C"].Old.PoolSize);
        Assert.Equal(42, diff.Changed["C"].New.PoolSize);
    }

    [Fact]
    public async Task Invalid_update_leaves_file_and_library_untouched()
    {
        await using var fixture = await StartedFixture.CreateAsync<ReloadableLibrary>();
        var before = await File.ReadAllTextAsync(fixture.KeyedPath);

        var result = await fixture.Manager.UpdateConfigAsync(
            "CL.Fake", "db", """{"databases":{"A":{"host":""}}}""");

        Assert.False(result.IsValid);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.KeyedPath));
        Assert.Empty(fixture.Library<ReloadableLibrary>().Changes);
    }

    [Fact]
    public async Task Library_without_reload_support_still_persists_the_change()
    {
        await using var fixture = await StartedFixture.CreateAsync<PlainLibrary>();

        var payload = JsonNode.Parse(await File.ReadAllTextAsync(fixture.KeyedPath))!;
        payload["databases"]!["A"]!["host"] = "moved";
        var result = await fixture.Manager.UpdateConfigAsync("CL.Fake", "db", payload.ToJsonString());

        Assert.True(result.Applied, result.ToString());
        Assert.Equal("moved", fixture.Context.Configuration.Get<KeyedConfig>().Databases["A"].Host);
    }

    // ── Apply failures ───────────────────────────────────────────────────────

    [Fact]
    public async Task Failed_apply_keeps_file_reports_error_and_degrades_health_until_fixed()
    {
        await using var fixture = await StartedFixture.CreateAsync<ReloadableLibrary>(l => l.FailNext = true);
        var failures = new List<ConfigApplyFailedEvent>();
        fixture.Bus.Subscribe<ConfigApplyFailedEvent>(failures.Add);

        var payload = JsonNode.Parse(await File.ReadAllTextAsync(fixture.KeyedPath))!;
        payload["databases"]!["A"]!["host"] = "unreachable";
        var failed = await fixture.Manager.UpdateConfigAsync("CL.Fake", "db", payload.ToJsonString());

        Assert.True(failed.IsValid);
        Assert.False(failed.Applied);
        Assert.NotNull(failed.ApplyError);
        Assert.Contains("unreachable", await File.ReadAllTextAsync(fixture.KeyedPath));
        Assert.Single(failures);
        Assert.True((await fixture.Manager.GetHealthAsync())["CL.Fake"].IsDegraded);

        payload["databases"]!["A"]!["host"] = "reachable";
        var fixedResult = await fixture.Manager.UpdateConfigAsync("CL.Fake", "db", payload.ToJsonString());

        Assert.True(fixedResult.Applied, fixedResult.ToString());
        Assert.True((await fixture.Manager.GetHealthAsync())["CL.Fake"].IsHealthy);
    }

    [Fact]
    public async Task Own_SaveAsync_throws_ConfigApplyException_when_the_library_cannot_apply()
    {
        await using var fixture = await StartedFixture.CreateAsync<ReloadableLibrary>(l => l.FailNext = true);
        var configuration = fixture.Context.Configuration;

        var config = configuration.Get<KeyedConfig>();
        config.Databases["A"].PoolSize = 5;

        await Assert.ThrowsAsync<ConfigApplyException>(() => configuration.SaveAsync(config));
        Assert.Contains("\"poolSize\": 5", await File.ReadAllTextAsync(fixture.KeyedPath));
    }

    [Fact]
    public async Task Handler_that_saves_again_does_not_deadlock()
    {
        await using var fixture = await StartedFixture.CreateAsync<ReloadableLibrary>(l => l.SaveAgainOnFirstChange = true);
        var library = fixture.Library<ReloadableLibrary>();

        var payload = JsonNode.Parse(await File.ReadAllTextAsync(fixture.KeyedPath))!;
        payload["databases"]!["A"]!["poolSize"] = 11;
        var update = fixture.Manager.UpdateConfigAsync("CL.Fake", "db", payload.ToJsonString());

        Assert.Same(update, await Task.WhenAny(update, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.True((await update).Applied);
        await WaitUntilAsync(() => library.Changes.Count == 2);
        Assert.Equal(12, fixture.Context.Configuration.Get<KeyedConfig>().Databases["A"].PoolSize);
    }

    // ── Reload ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reload_all_commits_nothing_when_any_section_is_invalid()
    {
        await using var fixture = await StartedFixture.CreateAsync<ReloadableLibrary>();
        var configuration = fixture.Context.Configuration;

        var keyed = JsonNode.Parse(await File.ReadAllTextAsync(fixture.KeyedPath))!;
        keyed["databases"]!["A"]!["host"] = "changed-on-disk";
        await File.WriteAllTextAsync(fixture.KeyedPath, keyed.ToJsonString());
        await File.WriteAllTextAsync(fixture.MainPath, """{"host":""}""");

        var result = await fixture.Manager.ReloadConfigAsync("CL.Fake");

        Assert.False(result.IsValid);
        Assert.Equal("a", configuration.Get<KeyedConfig>().Databases["A"].Host);
        Assert.Empty(fixture.Library<ReloadableLibrary>().Changes);
    }

    [Fact]
    public async Task Reload_publishes_events()
    {
        await using var fixture = await StartedFixture.CreateAsync<ReloadableLibrary>();
        var reloaded = new List<ConfigReloadedEvent>();
        var changed = new List<ConfigChangedEvent>();
        fixture.Bus.Subscribe<ConfigReloadedEvent>(reloaded.Add);
        fixture.Bus.Subscribe<ConfigChangedEvent>(changed.Add);

        await File.WriteAllTextAsync(fixture.MainPath, """{"host":"reloaded"}""");
        var result = await fixture.Manager.ReloadConfigAsync("CL.Fake", "main");

        Assert.True(result.Applied, result.ToString());
        Assert.Equal(typeof(MainConfig), Assert.Single(reloaded).ConfigType);
        Assert.Equal(ConfigChangeKind.Reloaded, Assert.Single(changed).Kind);
    }

    // ── Runtime overrides ────────────────────────────────────────────────────

    [Fact]
    public async Task Overrides_survive_updates_and_reloads_and_never_reach_the_file()
    {
        await using var fixture = await StartedFixture.CreateAsync<ReloadableLibrary>(
            setup: manager => manager.OverrideConfig<MainConfig>("CL.Fake", "main", c => c.Host = "override"));
        var configuration = fixture.Context.Configuration;
        Assert.Equal("override", configuration.Get<MainConfig>().Host);

        // Admin UI echoes the effective (overridden) value back while changing something else.
        var echoed = await fixture.Manager.UpdateConfigAsync("CL.Fake", "main", """{"host":"override","port":2}""");
        Assert.True(echoed.Applied, echoed.ToString());
        Assert.Equal("json", ReadMain(fixture)["host"]!.GetValue<string>());
        Assert.Equal(2, ReadMain(fixture)["port"]!.GetValue<int>());
        Assert.Equal("override", configuration.Get<MainConfig>().Host);

        // Library saves its live (overridden) instance.
        var live = configuration.Get<MainConfig>();
        live.Port = 3;
        await configuration.SaveAsync(live);
        Assert.Equal("json", ReadMain(fixture)["host"]!.GetValue<string>());

        // Reload from disk re-applies the override.
        await File.WriteAllTextAsync(fixture.MainPath, """{"host":"json-edited","port":4}""");
        Assert.True((await fixture.Manager.ReloadConfigAsync("CL.Fake", "main")).Applied);
        Assert.Equal("override", configuration.Get<MainConfig>().Host);
        Assert.Equal(4, configuration.Get<MainConfig>().Port);

        // Schema marks the field.
        var schema = fixture.Manager.GetConfigSchema("CL.Fake", "main")!;
        Assert.True(schema.Fields.Single(f => f.Path == "host").IsOverridden);
        Assert.False(schema.Fields.Single(f => f.Path == "port").IsOverridden);

        // An explicit change to an overridden field is persisted (the override still wins in memory).
        Assert.True((await fixture.Manager.UpdateConfigAsync("CL.Fake", "main", """{"host":"explicit","port":4}""")).Applied);
        Assert.Equal("explicit", ReadMain(fixture)["host"]!.GetValue<string>());
        Assert.Equal("override", configuration.Get<MainConfig>().Host);
    }

    // ── File watcher ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Watcher_reloads_external_edits_once_and_ignores_own_writes_and_invalid_files()
    {
        await using var fixture = await StartedFixture.CreateAsync<ReloadableLibrary>(setup: manager =>
        {
            manager.EnableConfigHotReload = true;
            manager.ConfigHotReloadDebounce = TimeSpan.FromMilliseconds(100);
        });
        fixture.Manager.StartConfigWatcher();
        var library = fixture.Library<ReloadableLibrary>();
        var configuration = fixture.Context.Configuration;

        // External edit → exactly one reload.
        var keyed = JsonNode.Parse(await File.ReadAllTextAsync(fixture.KeyedPath))!;
        keyed["databases"]!["B"]!["host"] = "edited-by-hand";
        await File.WriteAllTextAsync(fixture.KeyedPath, keyed.ToJsonString());
        await WaitUntilAsync(() => library.Changes.Count >= 1);
        await Task.Delay(600);
        var change = Assert.Single(library.Changes);
        Assert.Equal(ConfigChangeKind.Reloaded, change.Kind);
        Assert.Equal("file-watcher", change.ChangedBy);
        Assert.Equal("edited-by-hand", configuration.Get<KeyedConfig>().Databases["B"].Host);

        // Our own write → no extra reload from the watcher.
        keyed["databases"]!["C"]!["host"] = "via-api";
        Assert.True((await fixture.Manager.UpdateConfigAsync("CL.Fake", "db", keyed.ToJsonString())).Applied);
        await Task.Delay(600);
        Assert.Equal(2, library.Changes.Count);

        // Invalid external edit → current values kept.
        await File.WriteAllTextAsync(fixture.KeyedPath, "{ not json");
        await Task.Delay(600);
        Assert.Equal(2, library.Changes.Count);
        Assert.Equal("via-api", configuration.Get<KeyedConfig>().Databases["C"].Host);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static JsonNode ReadMain(StartedFixture fixture) =>
        JsonNode.Parse(File.ReadAllText(fixture.MainPath))!;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 10s.");
            await Task.Delay(25);
        }
    }

    private sealed class StartedFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"CodeLogic.Tests.{Guid.NewGuid():N}");

        private StartedFixture()
        {
            Bus = new EventBus();
            Manager = new LibraryManager(Bus) { FrameworkRootPath = _root };
            var directory = Path.Combine(_root, "Libraries", "CL.Fake");
            Directory.CreateDirectory(directory);
            MainPath = Path.Combine(directory, "config.main.json");
            KeyedPath = Path.Combine(directory, "config.db.json");
            File.WriteAllText(MainPath, """{"host":"json","port":1}""");
        }

        public static async Task<StartedFixture> CreateAsync<T>(
            Action<T>? prepareLibrary = null,
            Action<LibraryManager>? setup = null) where T : class, ILibrary, new()
        {
            var fixture = new StartedFixture();
            setup?.Invoke(fixture.Manager);
            await fixture.Manager.LoadLibraryAsync<T>();
            prepareLibrary?.Invoke(fixture.Library<T>());
            await fixture.Manager.ConfigureAllAsync();
            await fixture.Manager.InitializeAllAsync();
            await fixture.Manager.StartAllAsync();
            return fixture;
        }

        public EventBus Bus { get; }
        public LibraryManager Manager { get; }
        public string MainPath { get; }
        public string KeyedPath { get; }

        public LibraryContext Context =>
            Manager.GetLoadedLibraries().Single(l => l.Manifest.Id == "CL.Fake").Context!;

        public T Library<T>() where T : class, ILibrary => Manager.GetLibrary<T>()!;

        public ValueTask DisposeAsync()
        {
            Manager.Dispose();
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch (IOException) { } // logger may still hold its file briefly
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MainConfig : ConfigModelBase
    {
        [Required]
        public string Host { get; set; } = string.Empty;

        public int Port { get; set; }
    }

    private sealed class KeyedConfig : ConfigModelBase
    {
        public Dictionary<string, DbConfig> Databases { get; set; } = new()
        {
            ["A"] = new() { Host = "a" },
            ["B"] = new() { Host = "b" },
            ["C"] = new() { Host = "c" }
        };

        public override ConfigValidationResult Validate()
        {
            var empty = Databases.Where(d => string.IsNullOrEmpty(d.Value.Host)).Select(d => $"Database '{d.Key}': host is required");
            var errors = empty.ToList();
            return errors.Count > 0 ? ConfigValidationResult.Invalid(errors) : ConfigValidationResult.Valid();
        }
    }

    private sealed class DbConfig
    {
        [ConfigField(RequiresRestart = true)]
        public string Host { get; set; } = string.Empty;

        public int PoolSize { get; set; } = 10;
    }

    private class PlainLibrary : ILibrary
    {
        public LibraryManifest Manifest { get; } = new() { Id = "CL.Fake", Name = "Fake", Version = "1.0.0" };

        public Task OnConfigureAsync(LibraryContext context)
        {
            context.Configuration.Register<MainConfig>("main");
            context.Configuration.Register<KeyedConfig>("db");
            return Task.CompletedTask;
        }

        public Task OnInitializeAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnStopAsync() => Task.CompletedTask;
        public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy());
        public void Dispose() { }
    }

    private sealed class ReloadableLibrary : PlainLibrary, IConfigReloadable
    {
        private readonly object _gate = new();
        private readonly List<ConfigChangedEventArgs> _changes = [];

        public bool FailNext { get; set; }
        public bool SaveAgainOnFirstChange { get; set; }

        public IReadOnlyList<ConfigChangedEventArgs> Changes
        {
            get { lock (_gate) return _changes.ToList(); }
        }

        public async Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
        {
            int count;
            lock (_gate)
            {
                _changes.Add(change);
                count = _changes.Count;
            }

            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("cannot connect");
            }

            if (SaveAgainOnFirstChange && count == 1)
            {
                var config = context.Configuration.Get<KeyedConfig>();
                config.Databases["A"].PoolSize++;
                await context.Configuration.SaveAsync(config);
            }
        }
    }
}
