using CodeLogic.Core.Logging;
using CodeLogic.Core.Utilities;
using Xunit;

namespace CodeLogic.Tests;

/// <summary>Filesystem failure recovery, idle handle closing and path comparison of the log writer.</summary>
public sealed class LogFileWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl-logwriter-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static LogFileWriter FastIdleWriter() =>
        new(idleClose: TimeSpan.FromMilliseconds(50), maintenanceInterval: TimeSpan.FromMilliseconds(50));

    private static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Idle_files_are_closed_even_when_nothing_else_is_logged()
    {
        var writer = FastIdleWriter();
        writer.Enqueue(Path.Combine(_root, "idle", "a.log"), "only line", 0, 0);
        await writer.FlushAsync();

        // No further entries: the timed maintenance pass has to close the handle on its own.
        await WaitUntilAsync(() => writer.OpenFileCount == 0);
    }

    [Fact]
    public async Task Log_directory_removed_while_running_is_recreated()
    {
        var writer = FastIdleWriter();
        var dir = Path.Combine(_root, "removed");
        var file = Path.Combine(dir, "a.log");

        writer.Enqueue(file, "first", 0, 0);
        await writer.FlushAsync();
        await WaitUntilAsync(() => writer.OpenFileCount == 0);
        Directory.Delete(dir, recursive: true);

        writer.Enqueue(file, "second", 0, 0);
        await writer.FlushAsync();

        Assert.Contains("second", Read(file));
    }

    [Fact]
    public async Task Failed_directory_creation_is_retried_on_the_next_line()
    {
        var writer = FastIdleWriter();
        var dir = Path.Combine(_root, "blocked");
        var file = Path.Combine(dir, "a.log");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(dir, "a file where the directory should be");

        writer.Enqueue(file, "lost", 0, 0); // directory creation fails: falls back to Console.Error
        await writer.FlushAsync();
        File.Delete(dir);

        writer.Enqueue(file, "recovered", 0, 0);
        await writer.FlushAsync();

        Assert.Contains("recovered", Read(file));
    }

    [Fact]
    public void Path_comparison_matches_the_platform()
    {
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        Assert.Same(expected, PathComparison.Comparer);
    }

    [Fact]
    public async Task Paths_differing_only_in_case_get_separate_files_on_case_sensitive_systems()
    {
        if (PathComparison.IgnoreCase)
            return; // one file on Windows/macOS by definition

        var writer = FastIdleWriter();
        var dir = Path.Combine(_root, "case");
        writer.Enqueue(Path.Combine(dir, "App.log"), "upper", 0, 0);
        writer.Enqueue(Path.Combine(dir, "app.log"), "lower", 0, 0);
        await writer.FlushAsync();

        Assert.Equal("upper", Read(Path.Combine(dir, "App.log")).Trim());
        Assert.Equal("lower", Read(Path.Combine(dir, "app.log")).Trim());
    }
}
