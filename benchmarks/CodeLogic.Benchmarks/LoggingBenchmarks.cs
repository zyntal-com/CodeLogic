using BenchmarkDotNet.Attributes;
using CodeLogic.Core.Logging;

namespace CodeLogic.Benchmarks;

[MemoryDiagnoser]
public class LoggingBenchmarks
{
    private string _root = "";
    private ILogger _warningOnly = null!;
    private ILogger _singleFile = null!;
    private ILogger _dateFolder = null!;
    private readonly int _userId = 4711;
    private readonly string _action = "checkout";
    private readonly double _elapsedMs = 12.5;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "cl-bench-" + Guid.NewGuid().ToString("N"));
        var single = new LoggingOptions { Mode = LoggingMode.SingleFile, MaxFileSizeMb = 512, EnableConsoleOutput = false };
        var dated = new LoggingOptions { Mode = LoggingMode.DateFolder, EnableConsoleOutput = false };
        _warningOnly = new Logger("BENCH", Path.Combine(_root, "warn"), LogLevel.Warning, single);
        _singleFile = new Logger("BENCH", Path.Combine(_root, "single"), LogLevel.Info, single);
        _dateFolder = new Logger("BENCH", Path.Combine(_root, "dated"), LogLevel.Info, dated);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Flush();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // A filtered-out level: should cost (close to) nothing, including the interpolation.
    [Benchmark(Baseline = true)]
    public void Debug_disabled_interpolated() =>
        _warningOnly.Debug($"user {_userId} ran {_action} in {_elapsedMs} ms");

    [Benchmark]
    public void Debug_disabled_format() =>
        _warningOnly.Debug("user {0} ran {1} in {2} ms", _userId, _action, _elapsedMs);

    [Benchmark]
    public void Info_single_file() => _singleFile.Info("request handled");

    [Benchmark]
    public void Info_date_folder() => _dateFolder.Info("request handled");

    private static void Flush()
    {
        // The background writer (perf pass) exposes Logger.FlushAsync(); the baseline has none.
        var flush = typeof(Logger).GetMethod("FlushAsync", Type.EmptyTypes);
        if (flush?.Invoke(null, null) is Task task) task.GetAwaiter().GetResult();
    }
}
