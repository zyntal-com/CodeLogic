using System.Reflection;
using System.Text.Json.Nodes;
using CodeLogic.Core.Events;
using CodeLogic.Core.Localization;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Utilities;
using CodeLogic.Framework.Application;
using CodeLogic.Framework.Application.Plugins;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace CodeLogic.Tests;

/// <summary>
/// Runtime lifecycle guards, framework config validation, development config selection, library
/// dependency attributes and IDs, the library assembly resolver, plugin directory/manifest checks,
/// localization template generation and the centralized debug log.
/// </summary>
public sealed class FrameworkHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_harden_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* log files may still be open */ }
    }

    // ── Runtime ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_runs_the_application_stop_once()
    {
        var runtime = new CodeLogicRuntime();
        try
        {
            var init = await runtime.InitializeAsync(o => { o.FrameworkRootPath = _root; o.HandleShutdownSignals = false; });
            Assert.True(init.Success, init.Message);
            var app = new CountingApp();
            runtime.RegisterApplication(app);
            await runtime.ConfigureAsync();
            await runtime.StartAsync();

            await runtime.StopAsync();
            await runtime.StopAsync(); // e.g. the process-exit handler after an explicit stop
            await runtime.ResetAsync();

            Assert.Equal(1, app.Stops);
        }
        finally { await runtime.ResetAsync(); }
    }

    [Fact]
    public async Task InitializationResult_reports_the_first_run()
    {
        var first = new CodeLogicRuntime();
        var second = new CodeLogicRuntime();
        try
        {
            var a = await first.InitializeAsync(o => { o.FrameworkRootPath = _root; o.HandleShutdownSignals = false; });
            var b = await second.InitializeAsync(o => { o.FrameworkRootPath = _root; o.HandleShutdownSignals = false; });

            Assert.True(a.IsFirstRun);
            Assert.False(b.IsFirstRun);
        }
        finally
        {
            await first.ResetAsync();
            await second.ResetAsync();
        }
    }

    [Theory]
    [InlineData("healthChecks", "intervalSeconds", 0, "healthChecks.intervalSeconds")]
    [InlineData("healthChecks", "intervalSeconds", -5, "healthChecks.intervalSeconds")]
    [InlineData("logging", "timestampFormat", "%", "logging.timestampFormat")]
    [InlineData("logging", "maxFileSizeMb", 0, "logging.maxFileSizeMb")]
    public async Task Invalid_framework_config_fails_initialization(string section, string key, object value, string expected)
    {
        await FirstRunManager.ScaffoldAsync(_root);
        EditFrameworkConfig("CodeLogic.json", section, key, value);

        var runtime = new CodeLogicRuntime();
        try
        {
            var result = await runtime.InitializeAsync(o => { o.FrameworkRootPath = _root; o.HandleShutdownSignals = false; });

            Assert.False(result.Success);
            Assert.Contains(expected, result.Message);
        }
        finally { await runtime.ResetAsync(); }
    }

    [Fact]
    public void Disabled_health_checks_may_have_any_interval()
    {
        var config = new CodeLogicConfiguration { HealthChecks = { Enabled = false, IntervalSeconds = 0 } };
        Assert.Empty(config.Validate());
    }

    [Theory]
    [InlineData(true, 7)]
    [InlineData(false, 30)]
    public async Task Development_config_is_used_only_when_selected(bool useDevelopment, int expectedInterval)
    {
        await FirstRunManager.ScaffoldAsync(_root);
        EditFrameworkConfig("CodeLogic.Development.json", "healthChecks", "intervalSeconds", 7);

        var runtime = new CodeLogicRuntime();
        try
        {
            var result = await runtime.InitializeAsync(o =>
            {
                o.FrameworkRootPath = _root;
                o.HandleShutdownSignals = false;
                o.UseDevelopmentConfig = useDevelopment;
            });
            Assert.True(result.Success, result.Message);
            Assert.Equal(expectedInterval, runtime.GetConfiguration().HealthChecks.IntervalSeconds);
        }
        finally { await runtime.ResetAsync(); }
    }

    [Fact]
    public async Task By_default_the_development_config_follows_the_environment_not_the_debugger()
    {
        await FirstRunManager.ScaffoldAsync(_root);
        EditFrameworkConfig("CodeLogic.Development.json", "healthChecks", "intervalSeconds", 7);

        var runtime = new CodeLogicRuntime();
        try
        {
            var result = await runtime.InitializeAsync(o => { o.FrameworkRootPath = _root; o.HandleShutdownSignals = false; });
            Assert.True(result.Success, result.Message);
            Assert.Equal(CodeLogicEnvironment.IsDevelopment ? 7 : 30, runtime.GetConfiguration().HealthChecks.IntervalSeconds);
        }
        finally { await runtime.ResetAsync(); }
    }

    [Fact]
    public void Exit_after_generate_flag_is_parsed()
    {
        Assert.True(CliArgParser.Parse(["--generate-configs", "--exit-after-generate"]).ExitAfterGenerate);
        Assert.False(CliArgParser.Parse(["--generate-configs"]).ExitAfterGenerate);
    }

    private void EditFrameworkConfig(string file, string section, string key, object value)
    {
        var path = Path.Combine(_root, "Framework", file);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (root[section] is not JsonObject obj)
            root[section] = obj = new JsonObject();
        obj[key] = JsonValue.Create(value);
        File.WriteAllText(path, root.ToJsonString());
    }

    private sealed class CountingApp : IApplication
    {
        public int Stops;
        public ApplicationManifest Manifest { get; } = new() { Id = "app", Name = "App", Version = "1.0.0" };
        public Task OnConfigureAsync(ApplicationContext context) => Task.CompletedTask;
        public Task OnInitializeAsync(ApplicationContext context) => Task.CompletedTask;
        public Task OnStartAsync(ApplicationContext context) => Task.CompletedTask;
        public Task OnStopAsync() { Interlocked.Increment(ref Stops); return Task.CompletedTask; }
    }

    // ── Logging ──────────────────────────────────────────────────────────────

    [Fact]
    public void Centralized_debug_log_defaults_under_the_framework_root_and_needs_no_debug_mode()
    {
        var config = new LoggingConfig { CentralizedDebugLog = true };

        var options = config.ToLoggingOptions(_root);

        Assert.Equal(Path.Combine(_root, "Framework", "logs", "debug"), options.CentralizedLogsPath);
        Assert.False(options.EnableDebugMode);
        ILogger logger = new Logger("C", Path.Combine(_root, "c"), LogLevel.Critical, options);
        Assert.True(logger.IsEnabled(LogLevel.Trace)); // the centralized log takes every level

        var relative = new LoggingConfig { CentralizedDebugLog = true, CentralizedLogsPath = "central" }.ToLoggingOptions(_root);
        Assert.Equal(Path.Combine(_root, "central"), relative.CentralizedLogsPath);
    }

    // ── Libraries ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LibraryDependency_attribute_orders_and_validates_like_the_manifest()
    {
        using var manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        AttributeConsumer.Order.Clear();
        await manager.LoadLibraryAsync<AttributeConsumer>(); // registered first, depends on the provider
        await manager.LoadLibraryAsync<AttributeProvider>();

        await manager.ConfigureAllAsync();

        Assert.Equal(["CL.AttrProvider", "CL.AttrConsumer"], AttributeConsumer.Order);
        var consumer = manager.GetLoadedLibraries().Single(l => l.Manifest.Id == "CL.AttrConsumer");
        Assert.Equal("2.0.0", Assert.Single(consumer.Dependencies).MinVersion);
    }

    [Fact]
    public async Task LibraryDependency_attribute_minimum_version_is_enforced()
    {
        using var manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await manager.LoadLibraryAsync<AttributeTooNew>();
        await manager.LoadLibraryAsync<AttributeProvider>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfigureAllAsync());
        Assert.Contains("'CL.AttrProvider' >= 9.0.0", ex.Message);
    }

    [Fact]
    public async Task Library_ids_that_are_not_a_file_name_segment_are_rejected()
    {
        using var manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.LoadLibraryAsync<TraversalLibrary>());
        Assert.Empty(manager.GetLoadedLibraries());
    }

    [Fact]
    public void Library_resolver_serves_only_matching_assemblies_from_library_folders()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "plugin-fixture", "CodeLogic.Tests.PluginFixture.Dependency.dll");
        var libA = Directory.CreateDirectory(Path.Combine(_root, "Libraries", "CL.A")).FullName;
        var libB = Directory.CreateDirectory(Path.Combine(_root, "Libraries", "CL.B")).FullName;
        File.Copy(source, Path.Combine(libB, "CodeLogic.Tests.PluginFixture.Dependency.dll"));
        File.Copy(source, Path.Combine(libA, "Renamed.dll")); // file name does not match its assembly name
        string[] dirs = [libA, libB];

        var found = LibraryManager.FindLibraryDependency(
            new AssemblyName("CodeLogic.Tests.PluginFixture.Dependency"), dirs, requestingLocation: null);
        Assert.Equal(Path.Combine(libB, "CodeLogic.Tests.PluginFixture.Dependency.dll"), found);

        // A request from inside CL.A is answered from CL.A only.
        Assert.Null(LibraryManager.FindLibraryDependency(
            new AssemblyName("CodeLogic.Tests.PluginFixture.Dependency"), dirs, Path.Combine(libA, "CL.A.dll")));
        // The file must carry the requested assembly name.
        Assert.Null(LibraryManager.FindLibraryDependency(new AssemblyName("Renamed"), dirs, null));
        // Names that are not a plain assembly name never reach the file system.
        Assert.Null(LibraryManager.FindLibraryDependency(new AssemblyName { Name = "../CL.B/CodeLogic.Tests.PluginFixture.Dependency" }, dirs, null));
        // A requested public key token must match.
        var signed = new AssemblyName("CodeLogic.Tests.PluginFixture.Dependency");
        signed.SetPublicKeyToken([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Null(LibraryManager.FindLibraryDependency(signed, dirs, null));
    }

    private abstract class TestLibrary : ILibrary
    {
        public abstract LibraryManifest Manifest { get; }
        public virtual Task OnConfigureAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnInitializeAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnStopAsync() => Task.CompletedTask;
        public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy());
        public void Dispose() { }
    }

    private sealed class AttributeProvider : TestLibrary
    {
        public override LibraryManifest Manifest { get; } = new() { Id = "CL.AttrProvider", Name = "Provider", Version = "2.1.0" };
        public override Task OnConfigureAsync(LibraryContext context)
        {
            AttributeConsumer.Order.Add(Manifest.Id);
            return Task.CompletedTask;
        }
    }

    [LibraryDependency(Id = "CL.AttrProvider", MinVersion = "2.0.0")]
    private sealed class AttributeConsumer : TestLibrary
    {
        public static readonly List<string> Order = [];
        public override LibraryManifest Manifest { get; } = new() { Id = "CL.AttrConsumer", Name = "Consumer", Version = "1.0.0" };
        public override Task OnConfigureAsync(LibraryContext context)
        {
            Order.Add(Manifest.Id);
            return Task.CompletedTask;
        }
    }

    [LibraryDependency(Id = "CL.AttrProvider", MinVersion = "9.0.0")]
    private sealed class AttributeTooNew : TestLibrary
    {
        public override LibraryManifest Manifest { get; } = new() { Id = "CL.AttrTooNew", Name = "TooNew", Version = "1.0.0" };
    }

    private sealed class TraversalLibrary : TestLibrary
    {
        public override LibraryManifest Manifest { get; } = new() { Id = "../../escape", Name = "Escape", Version = "1.0.0" };
    }

    // ── Plugins ──────────────────────────────────────────────────────────────

    [Fact]
    public void Relative_plugin_directories_resolve_against_the_app_base_directory_not_the_cwd()
    {
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "plugins")),
            PluginManager.ResolvePluginsDirectory("data/plugins"));
        Assert.Equal(Path.GetFullPath(_root), PluginManager.ResolvePluginsDirectory(_root));
        if (!global::CodeLogic.CodeLogic.IsInitialized)
            Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "CodeLogic", "Plugins")),
                PluginManager.ResolvePluginsDirectory(PluginOptions.DefaultPluginsDirectory));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Plugin_ids_that_are_not_a_file_name_segment_are_rejected(string id)
    {
        var manifest = new PluginManifest { Id = id, Name = "x", Version = "1.0.0" };
        Assert.Throws<InvalidOperationException>(() => PluginManager.ValidateManifest(manifest));
    }

    [Fact]
    public void MinFrameworkVersion_is_enforced()
    {
        var running = SemanticVersion.Parse(CodeLogicEnvironment.FrameworkVersion);
        PluginManifest With(string? min) => new() { Id = "p", Name = "p", Version = "1.0.0", MinFrameworkVersion = min };

        PluginManager.ValidateManifest(With(null));
        PluginManager.ValidateManifest(With($"{running.Major}.{running.Minor}.{running.Patch}")); // a preview build counts as its version
        PluginManager.ValidateManifest(With("1.0.0"));
        Assert.Throws<InvalidOperationException>(() => PluginManager.ValidateManifest(With($"{running.Major + 1}.0.0")));
        Assert.Throws<InvalidOperationException>(() => PluginManager.ValidateManifest(With("5.0")));
    }

    [Fact]
    public async Task A_plugin_requiring_a_newer_framework_is_not_loaded()
    {
        await using var pm = new PluginManager(new EventBus(),
            new PluginOptions { PluginsDirectory = Path.Combine(_root, "plugins"), EnableHotReload = false });
        var source = Path.Combine(AppContext.BaseDirectory, "plugin-fixture");
        var target = Path.Combine(_root, "newer");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        File.WriteAllText(Path.Combine(target, "behavior.txt"), "id=newer\nminFramework=999.0.0");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pm.LoadPluginAsync(Path.Combine(target, "CodeLogic.Tests.PluginFixture.dll")));

        Assert.Contains("999.0.0", ex.Message);
        Assert.Empty(pm.GetLoadedPlugins());
    }

    [Fact]
    public async Task In_process_plugins_register_without_an_assembly_location_and_are_never_hot_reloaded()
    {
        await using var pm = new PluginManager(new EventBus(),
            new PluginOptions { PluginsDirectory = Path.Combine(_root, "plugins"), EnableHotReload = false });
        var plugin = new InProcessPlugin();
        var ctx = new PluginContext
        {
            PluginId = "inproc", PluginDirectory = _root, ConfigDirectory = _root, LocalizationDirectory = _root,
            LogsDirectory = _root, DataDirectory = _root, Logger = NullLogger.Instance,
            Configuration = new Core.Configuration.ConfigurationManager(_root),
            Localization = new LocalizationManager(Path.Combine(_root, "loc")), Events = new EventBus()
        };

        await pm.RegisterInProcessPluginAsync(plugin, ctx);

        var loaded = Assert.Single(pm.GetLoadedPlugins());
        Assert.True(loaded.IsInProcess);
        Assert.Equal(PluginState.Started, loaded.State);
        await pm.UnloadPluginAsync("inproc");
        Assert.Empty(pm.GetLoadedPlugins());
    }

    private sealed class InProcessPlugin : IPlugin
    {
        public PluginManifest Manifest { get; } = new() { Id = "inproc", Name = "In-process", Version = "1.0.0" };
        public PluginState State => PluginState.Started;
        public Task OnConfigureAsync(PluginContext context) => Task.CompletedTask;
        public Task OnInitializeAsync(PluginContext context) => Task.CompletedTask;
        public Task OnStartAsync(PluginContext context) => Task.CompletedTask;
        public Task OnUnloadAsync() => Task.CompletedTask;
        public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy());
        public void Dispose() { }
    }

    // ── Localization ─────────────────────────────────────────────────────────

    [LocalizationSection("hello")]
    private sealed class HelloStrings : LocalizationModelBase
    {
        public string Hello { get; set; } = "Hello";
    }

    [Fact]
    public async Task Without_template_generation_missing_cultures_use_the_built_in_strings_and_nothing_is_written()
    {
        var dir = Path.Combine(_root, "loc");
        var localization = new LocalizationManager(dir);
        localization.Register<HelloStrings>();

        await LibraryManager.LoadLocalizationAsync(localization, ["en-US", "da-DK"], generateTemplates: false);

        Assert.Empty(Directory.GetFiles(dir));
        Assert.Equal("Hello", localization.Get<HelloStrings>("da-DK").Hello);
        Assert.Equal("da-DK", localization.Get<HelloStrings>("da-DK").Culture);

        await LibraryManager.LoadLocalizationAsync(localization, ["en-US"], generateTemplates: true);
        Assert.Single(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Library_localization_reload_publishes_LocalizationReloadedEvent()
    {
        var bus = new EventBus();
        string? reloaded = null;
        using var sub = bus.Subscribe<LocalizationReloadedEvent>(e => reloaded = e.ComponentId);
        using var manager = new LibraryManager(bus) { FrameworkRootPath = _root };
        await manager.LoadLibraryAsync<AttributeProvider>();
        await manager.ConfigureAllAsync();

        var ctx = manager.GetLoadedLibraries().Single().Context!;
        await ctx.Localization.ReloadAllAsync(["en-US"]);

        Assert.Equal("CL.AttrProvider", reloaded);
    }
}
