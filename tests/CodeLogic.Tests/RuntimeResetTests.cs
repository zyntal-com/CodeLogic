using System.Reflection;
using System.Text.Json;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Utilities;
using Xunit;

namespace CodeLogic.Tests;

/// <summary><see cref="CodeLogicRuntime.ResetAsync"/> state and the reported framework version.</summary>
public sealed class RuntimeResetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_reset_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* log files may still be open */ }
    }

    private sealed record Ping : IEvent;

    [Fact]
    public async Task Reset_drops_event_subscriptions_from_the_previous_run()
    {
        var runtime = new CodeLogicRuntime();
        var calls = 0;
        var subscription = runtime.GetEventBus().Subscribe<Ping>(_ => calls++);

        await runtime.ResetAsync();
        runtime.GetEventBus().Publish(new Ping());
        subscription.Dispose(); // disposing a dropped subscription is harmless

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Reset_unregisters_shutdown_handlers_and_the_logger_and_reinitialize_registers_them_again()
    {
        var runtime = new CodeLogicRuntime();
        try
        {
            var first = await runtime.InitializeAsync(o => { o.FrameworkRootPath = _root; o.HandleShutdownSignals = true; });
            Assert.True(first.Success, first.Message);
            Assert.True(runtime.ShutdownHandlersRegistered);
            Assert.IsNotType<NullLogger>(runtime.FrameworkLogger);

            await runtime.ResetAsync();
            Assert.False(runtime.ShutdownHandlersRegistered);
            Assert.IsType<NullLogger>(runtime.FrameworkLogger);

            var second = await runtime.InitializeAsync(o => { o.FrameworkRootPath = _root; o.HandleShutdownSignals = true; });
            Assert.True(second.Success, second.Message);
            Assert.True(runtime.ShutdownHandlersRegistered); // the stale flag used to skip this
        }
        finally
        {
            await runtime.ResetAsync();
        }
    }

    [Fact]
    public async Task Framework_version_comes_from_the_assembly_and_is_used_by_first_run_scaffolding()
    {
        var informational = typeof(CodeLogicRuntime).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        Assert.Equal(informational, CodeLogicEnvironment.FrameworkVersion);
        Assert.NotEqual("3.0.0", CodeLogicEnvironment.FrameworkVersion);

        var result = await FirstRunManager.ScaffoldAsync(_root);
        Assert.True(result.Success, result.Error);

        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "Framework", "CodeLogic.json")));
        Assert.Equal(CodeLogicEnvironment.FrameworkVersion,
            config.RootElement.GetProperty("framework").GetProperty("version").GetString());
        Assert.Equal(CodeLogicEnvironment.FrameworkVersion, new FrameworkConfig().Version);
    }
}
