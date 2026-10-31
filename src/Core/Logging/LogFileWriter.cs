using System.Text;
using System.Threading.Channels;
using CodeLogic.Core.Utilities;

namespace CodeLogic.Core.Logging;

/// <summary>
/// The process-wide background writer behind every <see cref="Logger"/>.
/// </summary>
/// <remarks>
/// Callers only enqueue; one consumer task writes. That keeps file I/O off request threads and
/// keeps each file open between lines instead of opening, appending and closing it per line.
/// <list type="bullet">
/// <item>Order is preserved per file (single consumer).</item>
/// <item>The queue is bounded: when the disk cannot keep up, producers wait instead of the
/// process growing without limit or lines being dropped.</item>
/// <item>Files stay open with <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>, so log
/// viewers keep working; files idle for a while are closed (date-folder paths change daily), also when
/// nothing is logged at all afterwards.</item>
/// <item>A missing directory is (re)created when a file is opened, so a log folder deleted while the
/// process runs, or a transient creation failure, recovers on the next line.</item>
/// <item>Pending lines are flushed by <see cref="FlushAsync"/>, on process exit and on an unhandled
/// exception.</item>
/// </list>
/// </remarks>
internal sealed class LogFileWriter
{
    /// <summary>The writer shared by every logger in the process.</summary>
    internal static LogFileWriter Shared { get; } = new();

    private const int Capacity = 10_000;
    private static readonly TimeSpan DefaultIdleClose = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultMaintenanceInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitFlushTimeout = TimeSpan.FromSeconds(3);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly int NewLineBytes = Utf8NoBom.GetByteCount(Environment.NewLine);

    private readonly Channel<Entry> _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });

    // Owned by the consumer task only. Paths compare the way the platform's file system does.
    private readonly Dictionary<string, OpenFile> _files = new(PathComparison.Comparer);
    private int _openFileCount;

    private readonly TimeSpan _idleClose;
    private readonly TimeSpan _maintenanceInterval;
    private readonly bool _hookProcessExit;
    private readonly Lock _startLock = new();
    private Task? _consumer;

    private LogFileWriter() : this(DefaultIdleClose, DefaultMaintenanceInterval, hookProcessExit: true) { }

    /// <summary>A private writer with its own timings; tests use it.</summary>
    internal LogFileWriter(TimeSpan idleClose, TimeSpan maintenanceInterval, bool hookProcessExit = false)
    {
        _idleClose = idleClose;
        _maintenanceInterval = maintenanceInterval;
        _hookProcessExit = hookProcessExit;
    }

    /// <summary>Number of files currently held open (diagnostics and tests).</summary>
    internal int OpenFileCount => Volatile.Read(ref _openFileCount);

    /// <summary>Queues one entry. <paramref name="rollAtBytes"/> 0 disables size-based rolling.</summary>
    public void Enqueue(string path, string text, long rollAtBytes, int maxRolledFiles)
    {
        EnsureStarted();
        var entry = new Entry(path, text, rollAtBytes, maxRolledFiles, Flush: null);
        if (!_channel.Writer.TryWrite(entry))
            _channel.Writer.WriteAsync(entry).AsTask().GetAwaiter().GetResult(); // queue full: back-pressure
    }

    /// <summary>Completes once every entry queued before the call has been written and flushed.</summary>
    public Task FlushAsync()
    {
        if (Volatile.Read(ref _consumer) is null)
            return Task.CompletedTask; // nothing was ever logged
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var marker = new Entry("", "", 0, 0, done);
        if (!_channel.Writer.TryWrite(marker))
            return WriteMarkerAsync(marker, done);
        return done.Task;
    }

    private async Task WriteMarkerAsync(Entry marker, TaskCompletionSource done)
    {
        await _channel.Writer.WriteAsync(marker).ConfigureAwait(false);
        await done.Task.ConfigureAwait(false);
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _consumer) is not null)
            return;
        lock (_startLock)
        {
            if (_consumer is not null)
                return;
            if (_hookProcessExit)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushOnExit();
                AppDomain.CurrentDomain.UnhandledException += (_, _) => FlushOnExit();
            }
            Volatile.Write(ref _consumer, Task.Run(ConsumeAsync));
        }
    }

    private void FlushOnExit()
    {
        try { FlushAsync().Wait(ExitFlushTimeout); }
        catch { /* the process is going away; never throw from here */ }
    }

    private async Task ConsumeAsync()
    {
        var reader = _channel.Reader;
        while (await WaitForEntriesAsync(reader).ConfigureAwait(false))
        {
            while (reader.TryRead(out var entry))
            {
                if (entry.Flush is not null)
                {
                    FlushAll();
                    entry.Flush.TrySetResult();
                    continue;
                }
                Write(entry);
            }

            // The queue is drained: push buffered text to disk and close files nobody writes to.
            FlushAll();
            CloseIdleFiles();
        }
    }

    /// <summary>
    /// Waits for the next entry. While files are open the wait is bounded, so files that went idle
    /// after the last line get closed even if nothing else is ever logged.
    /// </summary>
    private async Task<bool> WaitForEntriesAsync(ChannelReader<Entry> reader)
    {
        while (true)
        {
            if (_files.Count == 0)
                return await reader.WaitToReadAsync().ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(_maintenanceInterval);
            try
            {
                return await reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                CloseIdleFiles();
            }
        }
    }

    private void Write(Entry entry)
    {
        try
        {
            var file = GetOrOpen(entry.Path);
            if (entry.RollAtBytes > 0 && file.Length >= entry.RollAtBytes)
            {
                Close(entry.Path);
                RollFiles(entry.Path, entry.MaxRolledFiles);
                file = GetOrOpen(entry.Path);
            }

            file.Writer.Write(entry.Text);
            file.Writer.Write(Environment.NewLine);
            file.Length += Utf8NoBom.GetByteCount(entry.Text) + NewLineBytes;
            file.LastWrite = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            Close(entry.Path);
            // Logging must never crash the app — fall back to Console.Error.
            try
            {
                Console.Error.WriteLine($"[CodeLogic.Logger] Failed to write to {entry.Path}: {ex.Message}");
                Console.Error.WriteLine(entry.Text);
            }
            catch { /* if Console.Error also fails, give up silently */ }
        }
    }

    private OpenFile GetOrOpen(string path)
    {
        if (_files.TryGetValue(path, out var open))
            return open;

        FileStream stream;
        try
        {
            stream = OpenStream(path);
        }
        catch (DirectoryNotFoundException)
        {
            // First file in the folder, or the folder was removed while we ran: create it and retry.
            // Nothing is cached, so a failure here is retried on the next line instead of sticking.
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                throw;
            Directory.CreateDirectory(directory);
            stream = OpenStream(path);
        }

        open = new OpenFile(new StreamWriter(stream, Utf8NoBom, bufferSize: 16 * 1024), stream.Length);
        _files[path] = open;
        Volatile.Write(ref _openFileCount, _files.Count);
        return open;
    }

    private static FileStream OpenStream(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 16 * 1024);

    private void FlushAll()
    {
        foreach (var (path, file) in _files)
        {
            try { file.Writer.Flush(); }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[CodeLogic.Logger] Failed to flush {path}: {ex.Message}"); }
                catch { /* ignore */ }
            }
        }
    }

    private void CloseIdleFiles()
    {
        if (_files.Count == 0)
            return;
        var cutoff = Environment.TickCount64 - (long)_idleClose.TotalMilliseconds;
        List<string>? idle = null;
        foreach (var (path, file) in _files)
            if (file.LastWrite < cutoff)
                (idle ??= []).Add(path);
        if (idle is not null)
            foreach (var path in idle)
                Close(path);
    }

    private void Close(string path)
    {
        if (!_files.Remove(path, out var file))
            return;
        Volatile.Write(ref _openFileCount, _files.Count);
        try { file.Writer.Dispose(); }
        catch { /* the handle is gone either way */ }
    }

    private static void RollFiles(string activeFilePath, int maxRolledFiles)
    {
        // Delete the oldest file if at limit
        var oldest = GetRolledFilePath(activeFilePath, maxRolledFiles);
        if (File.Exists(oldest))
            File.Delete(oldest);

        // Shift: component.N.log → component.(N+1).log
        for (var i = maxRolledFiles - 1; i >= 1; i--)
        {
            var src = GetRolledFilePath(activeFilePath, i);
            var dest = GetRolledFilePath(activeFilePath, i + 1);
            if (File.Exists(src))
                File.Move(src, dest, overwrite: true);
        }

        // component.log → component.1.log
        File.Move(activeFilePath, GetRolledFilePath(activeFilePath, 1), overwrite: true);
    }

    private static string GetRolledFilePath(string activeFilePath, int index)
    {
        // component.log → component.1.log
        var dir = Path.GetDirectoryName(activeFilePath)!;
        var name = Path.GetFileNameWithoutExtension(activeFilePath);
        var ext = Path.GetExtension(activeFilePath);
        return Path.Combine(dir, $"{name}.{index}{ext}");
    }

    private readonly record struct Entry(string Path, string Text, long RollAtBytes, int MaxRolledFiles, TaskCompletionSource? Flush);

    private sealed class OpenFile(StreamWriter writer, long length)
    {
        public StreamWriter Writer { get; } = writer;
        public long Length { get; set; } = length;
        public long LastWrite { get; set; } = Environment.TickCount64;
    }
}
