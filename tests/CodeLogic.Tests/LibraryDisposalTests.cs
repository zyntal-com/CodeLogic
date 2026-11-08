using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace CodeLogic.Tests;

/// <summary>The framework disposes library instances after stopping them, and adopts directories renamed by ID casing.</summary>
public sealed class LibraryDisposalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_dispose_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Libraries_are_disposed_once_in_reverse_dependency_order()
    {
        Log.Clear();
        using (var manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root })
        {
            await manager.LoadLibraryAsync<Consumer>();   // loaded first, depends on Provider
            await manager.LoadLibraryAsync<Provider>();
            await manager.ConfigureAllAsync();
            await manager.InitializeAllAsync();
            await manager.StartAllAsync();
            await manager.StopAllAsync();

            await manager.DisposeLibrariesAsync();
            await manager.DisposeLibrariesAsync();   // a second call disposes nothing
            Assert.All(manager.GetLoadedLibraries(), l => Assert.True(l.IsDisposed));
        }   // Dispose() does not dispose them again

        Assert.Equal(["stop:CL.DispConsumer", "stop:CL.DispProvider", "async:CL.DispConsumer", "sync:CL.DispProvider"], Log.Snapshot());
    }

    [Fact]
    public async Task Dispose_releases_libraries_that_were_never_started()
    {
        Log.Clear();
        using (var manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root })
        {
            await manager.LoadLibraryAsync<Provider>();
            await manager.ConfigureAllAsync();
        }
        Assert.Equal(["sync:CL.DispProvider"], Log.Snapshot());
    }

    [Fact]
    public void A_directory_that_differs_only_in_case_is_adopted()
    {
        var old = Path.Combine(_root, "CL.common");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "config.json"), "{}");

        var adopted = LibraryManager.AdoptDirectory(_root, "CL.Common");

        Assert.Equal(Path.Combine(_root, "CL.Common"), adopted);
        Assert.True(File.Exists(Path.Combine(adopted, "config.json")));
        // One directory, the old one (renamed on case-sensitive file systems; elsewhere the old casing already resolves).
        var single = Assert.Single(Directory.GetDirectories(_root));
        Assert.Equal("CL.Common", Path.GetFileName(single), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_directory_is_not_invented()
    {
        Directory.CreateDirectory(_root);
        Assert.Equal(Path.Combine(_root, "CL.New"), LibraryManager.AdoptDirectory(_root, "CL.New"));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    private static class Log
    {
        private static readonly Lock Gate = new();
        private static readonly List<string> Entries = [];

        public static void Add(string entry) { lock (Gate) Entries.Add(entry); }
        public static void Clear() { lock (Gate) Entries.Clear(); }
        public static string[] Snapshot() { lock (Gate) return [.. Entries]; }
    }

    private abstract class TestLibrary : ILibrary
    {
        public abstract LibraryManifest Manifest { get; }
        public Task OnConfigureAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnInitializeAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;
        public Task OnStopAsync() { Log.Add("stop:" + Manifest.Id); return Task.CompletedTask; }
        public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy());
        public void Dispose() => Log.Add("sync:" + Manifest.Id);
    }

    private sealed class Provider : TestLibrary
    {
        public override LibraryManifest Manifest { get; } = new() { Id = "CL.DispProvider", Name = "Provider", Version = "1.0.0" };
    }

    private sealed class Consumer : TestLibrary, IAsyncDisposable
    {
        public override LibraryManifest Manifest { get; } = new()
        {
            Id = "CL.DispConsumer", Name = "Consumer", Version = "1.0.0",
            Dependencies = [LibraryDependency.Required("CL.DispProvider")],
        };

        public ValueTask DisposeAsync()
        {
            Log.Add("async:" + Manifest.Id);
            return ValueTask.CompletedTask;
        }
    }
}
