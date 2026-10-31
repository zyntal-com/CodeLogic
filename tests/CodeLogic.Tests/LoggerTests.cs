using System.Globalization;
using CodeLogic.Core.Logging;
using Xunit;

namespace CodeLogic.Tests;

public sealed class LoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl-logger-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Logger.FlushAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_root, recursive: true); } catch { /* files may still be held briefly */ }
    }

    private static LoggingOptions Options(LoggingMode mode = LoggingMode.SingleFile) =>
        new() { Mode = mode, EnableConsoleOutput = false, IncludeMachineName = false };

    // The background writer keeps files open for writing, so read them the way a log viewer does.
    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Disabled_level_does_not_evaluate_interpolation_holes()
    {
        ILogger logger = new Logger("T", Path.Combine(_root, "a"), LogLevel.Warning, Options());
        var evaluated = 0;
        string Probe() { evaluated++; return "x"; }

        logger.Debug($"value {Probe()}");
        logger.Trace($"value {Probe()}");
        logger.Info($"value {Probe()}");
        Assert.Equal(0, evaluated);

        logger.Warning($"value {Probe()}");
        Assert.Equal(1, evaluated);
    }

    [Fact]
    public void Disabled_level_does_not_format_composite_arguments()
    {
        ILogger logger = new Logger("T", Path.Combine(_root, "b"), LogLevel.Warning, Options());
        var probe = new FormatProbe();

        logger.Debug("value {0}", probe);
        logger.Info("value {0}", probe);
        Assert.Equal(0, probe.Calls);

        logger.Warning("value {0}", probe);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void NullLogger_reports_every_level_disabled()
    {
        var evaluated = false;
        string Probe() { evaluated = true; return "x"; }

        ILogger logger = NullLogger.Instance;
        logger.Warning($"{Probe()}");

        Assert.False(logger.IsEnabled(LogLevel.Critical));
        Assert.False(evaluated);
    }

    [Fact]
    public async Task Lines_are_written_in_order_and_flushed()
    {
        var dir = Path.Combine(_root, "order");
        ILogger logger = new Logger("ORDER", dir, LogLevel.Info, Options());

        for (var i = 0; i < 500; i++)
            logger.Info($"line {i}");
        await Logger.FlushAsync();

        var lines = ReadLines(Path.Combine(dir, "order.log"));
        Assert.Equal(500, lines.Length);
        for (var i = 0; i < 500; i++)
            Assert.EndsWith($"[INFO] line {i}", lines[i]);
    }

    [Fact]
    public async Task Concurrent_writers_lose_no_lines()
    {
        var dir = Path.Combine(_root, "concurrent");
        var logger = new Logger("MANY", dir, LogLevel.Info, Options());

        Parallel.For(0, 8, thread =>
        {
            for (var i = 0; i < 500; i++)
                logger.Info($"thread {thread} line {i}");
        });
        await Logger.FlushAsync();

        Assert.Equal(4000, ReadLines(Path.Combine(dir, "many.log")).Length);
    }

    [Fact]
    public async Task Single_file_rolls_at_the_size_limit()
    {
        var dir = Path.Combine(_root, "roll");
        var options = Options();
        options.MaxFileSizeMb = 1;
        options.MaxRolledFiles = 2;
        var logger = new Logger("ROLL", dir, LogLevel.Info, options);

        var payload = new string('x', 1024);
        for (var i = 0; i < 1500; i++) // ~1.5 MB
            logger.Info(payload);
        await Logger.FlushAsync();

        Assert.True(File.Exists(Path.Combine(dir, "roll.1.log")), "the full file should have rolled to roll.1.log");
        Assert.True(new FileInfo(Path.Combine(dir, "roll.1.log")).Length >= 1024 * 1024);
        Assert.True(new FileInfo(Path.Combine(dir, "roll.log")).Length < 1024 * 1024);
    }

    [Fact]
    public async Task Date_folder_paths_use_the_invariant_calendar()
    {
        var dir = Path.Combine(_root, "dated");
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // th-TH uses the Buddhist calendar: the year would be 543 years ahead.
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            ILogger logger = new Logger("DATED", dir, LogLevel.Info, Options(LoggingMode.DateFolder));
            logger.Info("hello");
            await Logger.FlushAsync();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var now = DateTime.Now;
        var expected = Path.Combine(dir, now.Year.ToString("D4", CultureInfo.InvariantCulture),
            now.Month.ToString("D2", CultureInfo.InvariantCulture), now.Day.ToString("D2", CultureInfo.InvariantCulture), "info.log");
        Assert.True(File.Exists(expected), $"expected {expected}");
    }

    [Fact]
    public async Task Centralized_debug_log_receives_entries_with_the_component_name()
    {
        var central = Path.Combine(_root, "central");
        var options = Options();
        options.EnableDebugMode = true;
        options.CentralizedDebugLog = true;
        options.CentralizedLogsPath = central;
        ILogger logger = new Logger("COMP", Path.Combine(_root, "comp"), LogLevel.Warning, options);

        Assert.True(logger.IsEnabled(LogLevel.Trace)); // the central log takes every level
        logger.Debug("to the central log only");
        await Logger.FlushAsync();

        var file = Directory.GetFiles(central, "debug_all.log", SearchOption.AllDirectories).Single();
        Assert.Contains(ReadLines(file), l => l.StartsWith("[COMP] [COMP]", StringComparison.Ordinal) && l.EndsWith("to the central log only", StringComparison.Ordinal));
    }

    private sealed class FormatProbe : IFormattable
    {
        public int Calls { get; private set; }
        public string ToString(string? format, IFormatProvider? formatProvider) { Calls++; return "probe"; }
        public override string ToString() { Calls++; return "probe"; }
    }
}

public sealed class LoggerInterpolationCompatibilityTests
{
    // Every hole that compiles in a plain $"" string must compile and render the same in a log call.
    [Fact]
    public async Task Interpolation_holes_render_like_string_interpolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "cl-logger-compat-" + Guid.NewGuid().ToString("N"));
        try
        {
            ILogger logger = new Logger("COMPAT", root, LogLevel.Info,
                new LoggingOptions { EnableConsoleOutput = false, IncludeMachineName = false });
            var max = 0;
            ReadOnlySpan<char> span = "span";
            logger.Info($"{(max > 0 ? max : "∞")}|{"right",8}|{span,6}|{3.14159:F2}|{42,-4}|{null}|{(object?)null}");
            await Logger.FlushAsync();

            using var stream = new FileStream(Path.Combine(root, "compat.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var line = new StreamReader(stream).ReadToEnd().TrimEnd();
            var expected = string.Create(System.Globalization.CultureInfo.CurrentCulture,
                $"{(max > 0 ? max : "∞")}|{"right",8}|{"span",6}|{3.14159:F2}|{42,-4}||");
            Assert.EndsWith("[INFO] " + expected, line);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
