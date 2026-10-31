using CodeLogic.Core.Events;
using CodeLogic.Framework.Application.Plugins;
using Xunit;

namespace CodeLogic.Tests;

/// <summary>
/// Loads copies of the CodeLogic.Tests.PluginFixture output folder as real plugins and injects
/// failures through its behavior.txt.
/// </summary>
public sealed class PluginManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl-plugin-tests", Guid.NewGuid().ToString("N"));
    private readonly EventBus _bus = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* assemblies may still be mapped */ }
    }

    private PluginManager NewManager() =>
        new(_bus, new PluginOptions { PluginsDirectory = Path.Combine(_root, "plugins"), EnableHotReload = false });

    /// <summary>Copies the fixture into its own folder and returns the plugin assembly path.</summary>
    private string PluginFolder(string name, string behavior)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "plugin-fixture");
        var target = Path.Combine(_root, name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        File.WriteAllText(Path.Combine(target, "behavior.txt"), behavior);
        return Path.Combine(target, "CodeLogic.Tests.PluginFixture.dll");
    }

    private static string[] Calls(string pluginPath)
    {
        var log = Path.Combine(Path.GetDirectoryName(pluginPath)!, "calls.log");
        return File.Exists(log) ? File.ReadAllLines(log) : [];
    }

    [Fact]
    public async Task Load_and_unload_run_the_full_lifecycle()
    {
        await using var pm = NewManager();
        var path = PluginFolder("ok", "id=ok");

        await pm.LoadPluginAsync(path);
        Assert.Equal(PluginState.Started, Assert.Single(pm.GetLoadedPlugins()).State);
        Assert.NotNull(pm.GetPlugin<IPlugin>("ok")); // CodeLogic is shared: IPlugin has one identity

        await pm.UnloadPluginAsync("ok");

        Assert.Empty(pm.GetLoadedPlugins());
        Assert.Equal(["configure", "initialize", "start", "unload", "dispose"], Calls(path));
    }

    [Fact]
    public async Task Initialize_failure_disposes_the_instance_and_registers_nothing()
    {
        await using var pm = NewManager();
        var path = PluginFolder("init", "id=init\nfail=initialize");
        PluginFailedEvent? failed = null;
        using var sub = _bus.Subscribe<PluginFailedEvent>(e => failed = e);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pm.LoadPluginAsync(path));

        Assert.Empty(pm.GetLoadedPlugins());
        Assert.Equal(["configure", "initialize", "dispose"], Calls(path));
        Assert.Equal("init", failed?.PluginId);
    }

    [Fact]
    public async Task Start_failure_unloads_and_disposes_the_instance()
    {
        await using var pm = NewManager();
        var path = PluginFolder("start", "id=start\nfail=start");

        await Assert.ThrowsAsync<InvalidOperationException>(() => pm.LoadPluginAsync(path));

        Assert.Empty(pm.GetLoadedPlugins());
        Assert.Equal(["configure", "initialize", "start", "unload", "dispose"], Calls(path));
    }

    [Fact]
    public async Task Unload_failure_still_disposes_and_removes_the_plugin()
    {
        await using var pm = NewManager();
        var path = PluginFolder("unload", "id=unload\nfail=unload");
        await pm.LoadPluginAsync(path);
        var loaded = Assert.Single(pm.GetLoadedPlugins());
        Exception? reported = null;
        pm.OnPluginError += (_, ex) => reported = ex;

        await pm.UnloadPluginAsync("unload"); // reported, not thrown

        Assert.Empty(pm.GetLoadedPlugins());
        Assert.Equal(PluginState.Failed, loaded.State);
        Assert.Equal(["configure", "initialize", "start", "unload", "dispose"], Calls(path));
        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public async Task Duplicate_id_is_rejected_before_it_starts_and_keeps_the_running_instance()
    {
        await using var pm = NewManager();
        var first = PluginFolder("first", "id=dup");
        var second = PluginFolder("second", "id=dup");
        await pm.LoadPluginAsync(first);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pm.LoadPluginAsync(second));

        Assert.Contains("already loaded", ex.Message);
        Assert.Equal(first, Assert.Single(pm.GetLoadedPlugins()).AssemblyPath);
        Assert.Equal(["dispose"], Calls(second));
        Assert.Equal(["configure", "initialize", "start"], Calls(first));
    }

    [Fact]
    public async Task Concurrent_loads_of_the_same_id_are_serialized_and_only_one_starts()
    {
        await using var pm = NewManager();
        var a = PluginFolder("a", "id=race\nstartDelayMs=150");
        var b = PluginFolder("b", "id=race\nstartDelayMs=150");

        var results = await Task.WhenAll(
            Capture(pm.LoadPluginAsync(a)),
            Capture(pm.LoadPluginAsync(b)));

        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r is InvalidOperationException);
        Assert.Single(pm.GetLoadedPlugins());
        Assert.Single(new[] { Calls(a), Calls(b) }, calls => calls.Contains("start"));

        static async Task<Exception?> Capture(Task task)
        {
            try { await task; return null; }
            catch (Exception ex) { return ex; }
        }
    }

    [Fact]
    public async Task Readers_can_enumerate_while_plugins_load_and_unload()
    {
        await using var pm = NewManager();
        var path = PluginFolder("churn", "id=churn");
        using var cts = new CancellationTokenSource();

        var readers = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await pm.GetHealthAsync();
                _ = pm.GetLoadedPlugins().Count();
                _ = pm.GetAllPlugins().Count();
            }
        });

        for (var i = 0; i < 5; i++)
        {
            await pm.LoadPluginAsync(path);
            await pm.UnloadPluginAsync("churn");
        }

        await cts.CancelAsync();
        await readers; // throws if any reader saw a collection-modified exception
    }

    [Fact]
    public async Task Plugin_dependencies_load_into_each_plugins_own_context()
    {
        await using var pm = NewManager();
        await pm.LoadPluginAsync(PluginFolder("iso-a", "id=iso-a"));
        await pm.LoadPluginAsync(PluginFolder("iso-b", "id=iso-b"));

        await pm.GetHealthAsync(); // touches the dependency in both plugins

        foreach (var loaded in pm.GetLoadedPlugins())
        {
            var names = loaded.LoadContext.Assemblies.Select(a => a.GetName().Name).ToList();
            Assert.Contains("CodeLogic.Tests.PluginFixture.Dependency", names); // its own copy, not the other plugin's
            Assert.DoesNotContain("CodeLogic", names);                          // the host contract is shared
        }
    }

    [Fact]
    public void Load_context_shares_codelogic_and_configured_contracts_only()
    {
        var ctx = new PluginLoadContext(typeof(PluginManagerTests).Assembly.Location, ["My.Contracts"]);
        try
        {
            Assert.True(ctx.IsShared(new System.Reflection.AssemblyName("CodeLogic")));
            Assert.True(ctx.IsShared(new System.Reflection.AssemblyName("my.contracts")));
            Assert.False(ctx.IsShared(new System.Reflection.AssemblyName("Some.Dependency")));
            Assert.Same(typeof(PluginManager).Assembly, ctx.LoadFromAssemblyName(new System.Reflection.AssemblyName("CodeLogic")));
        }
        finally { ctx.Unload(); }
    }

    [Fact]
    public async Task Failing_notification_handler_does_not_fail_the_load()
    {
        await using var pm = NewManager();
        pm.OnPluginLoaded += _ => throw new InvalidOperationException("handler bug");
        var path = PluginFolder("notify", "id=notify");

        await pm.LoadPluginAsync(path);

        Assert.Equal(PluginState.Started, Assert.Single(pm.GetLoadedPlugins()).State);
        Assert.DoesNotContain("unload", Calls(path));
    }

    [Fact]
    public async Task Dispose_unloads_everything_is_idempotent_and_rejects_later_use()
    {
        var pm = NewManager();
        var path = PluginFolder("dispose", "id=dispose");
        await pm.LoadPluginAsync(path);

        await pm.DisposeAsync();
        await pm.DisposeAsync();

        Assert.Equal(["configure", "initialize", "start", "unload", "dispose"], Calls(path));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pm.LoadPluginAsync(path));
    }

    // ── Re-entrancy: a callback that calls back into the manager must fail fast, not deadlock ──

    private static readonly TimeSpan ReentryTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("start", "load")]
    [InlineData("start", "unload")]
    [InlineData("start", "reload")]
    [InlineData("start", "dispose")]
    [InlineData("unload", "load")]
    [InlineData("unload", "unload")]
    [InlineData("unload", "reload")]
    [InlineData("unload", "dispose")]
    public async Task Lifecycle_callback_reentering_the_manager_throws_instead_of_deadlocking(string callback, string op)
    {
        await using var pm = NewManager();
        var path = PluginFolder($"re-{callback}-{op}", $"id=re\nreenter={callback}");
        var other = PluginFolder($"re-{callback}-{op}-other", "id=other");
        Exception? outcome = null;
        var reentered = false;

        using var sub = _bus.SubscribeAsync<ComponentAlertEvent>(async e =>
        {
            if (e.AlertType != "reenter") return;
            reentered = true;
            try
            {
                Task nested = op switch
                {
                    "load"    => pm.LoadPluginAsync(other),
                    "unload"  => pm.UnloadPluginAsync("re"),
                    "reload"  => pm.ReloadPluginAsync("re"),
                    "dispose" => pm.DisposeAsync().AsTask(),
                    _ => throw new ArgumentOutOfRangeException(nameof(op))
                };
                await nested.WaitAsync(ReentryTimeout); // a deadlock surfaces as TimeoutException
            }
            catch (Exception ex) { outcome = ex; }
        });

        await pm.LoadPluginAsync(path).WaitAsync(ReentryTimeout * 2);
        if (callback == "unload")
            await pm.UnloadPluginAsync("re").WaitAsync(ReentryTimeout * 2);

        Assert.True(reentered);
        Assert.IsType<InvalidOperationException>(outcome);
    }

    [Fact]
    public async Task Error_handler_reentering_the_manager_throws_instead_of_deadlocking()
    {
        await using var pm = NewManager();
        var path = PluginFolder("re-error", "id=re-error\nfail=initialize");
        Exception? outcome = null;
        pm.OnPluginError += (_, _) =>
        {
            try { pm.UnloadAllAsync().WaitAsync(ReentryTimeout).GetAwaiter().GetResult(); }
            catch (Exception ex) { outcome ??= ex; }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pm.LoadPluginAsync(path).WaitAsync(ReentryTimeout * 2));

        Assert.IsType<InvalidOperationException>(outcome);
    }

    [Fact]
    public async Task Loaded_handler_runs_after_the_operation_and_may_call_lifecycle_methods()
    {
        await using var pm = NewManager();
        var path = PluginFolder("after", "id=after");
        Task? nested = null;
        pm.OnPluginLoaded += id => nested = pm.UnloadPluginAsync(id);

        await pm.LoadPluginAsync(path).WaitAsync(ReentryTimeout);
        await nested!.WaitAsync(ReentryTimeout);

        Assert.Empty(pm.GetLoadedPlugins());
    }

    [Fact]
    public async Task Runtime_reset_disposes_and_forgets_the_plugin_manager()
    {
        var runtime = new CodeLogicRuntime();
        var pm = NewManager();
        var path = PluginFolder("reset", "id=reset");
        await pm.LoadPluginAsync(path);
        runtime.SetPluginManager(pm);

        await runtime.ResetAsync();

        Assert.Null(runtime.GetPluginManager());
        Assert.Equal(["configure", "initialize", "start", "unload", "dispose"], Calls(path));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pm.LoadPluginAsync(path));
    }
}
