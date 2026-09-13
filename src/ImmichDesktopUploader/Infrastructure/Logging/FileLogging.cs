using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.Async;

namespace ImmichDesktopUploader.Infrastructure.Logging;

public sealed record FileLoggingOptions(long FileSizeLimitBytes = 10 * 1024 * 1024,
    int RetainedFileCount = 30, int BufferSize = 2048);

public sealed class LoggingHealth : IAsyncLogEventSinkMonitor
{
    private int failed;
    private IAsyncLogEventSinkInspector? inspector;
    private long finalDropped;
    public bool FileUnavailable => Volatile.Read(ref failed) != 0;
    public long DroppedEvents => Volatile.Read(ref inspector)?.DroppedMessagesCount ?? Interlocked.Read(ref finalDropped);
    internal void Failed()
    {
        Interlocked.Exchange(ref failed, 1);
        // SelfLog can run on the producer when its queue is full. Do no I/O here,
        // including debugger I/O; the GUI observes this flag. Never expose SelfLog text.
    }
    public void StartMonitoring(IAsyncLogEventSinkInspector value) => Volatile.Write(ref inspector, value);
    public void StopMonitoring(IAsyncLogEventSinkInspector value)
    { Interlocked.Exchange(ref finalDropped, value.DroppedMessagesCount); Volatile.Write(ref inspector, null); }
}

// One primary-process owner. Microsoft ILogger is the common entry point; Serilog owns
// the bounded worker and rolling files, not the CLI/session/UI threads.
public sealed class FileLogging : IAsyncDisposable
{
    private readonly object gate = new();
    private Task? disposal;
    private readonly FileLoggingOptions options;
    public string LogsPath { get; }
    public SecretRegistry Secrets { get; } = new();
    public LoggingHealth Health { get; } = new();
    public ILoggerFactory Factory { get; }
    public FileLogging(string logsPath, FileLoggingOptions? options = null) : this(logsPath, options, null) { }
    internal FileLogging(string logsPath, FileLoggingOptions? options, ILogEventSink? testSink)
    {
        LogsPath = Path.GetFullPath(logsPath); options ??= new();
        this.options = options;
        if (options.FileSizeLimitBytes < 1 || options.RetainedFileCount < 1 || options.BufferSize < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        Serilog.Debugging.SelfLog.Enable(_ => Health.Failed());
        Serilog.ILogger logger;
        try
        {
            Directory.CreateDirectory(LogsPath);
            logger = new LoggerConfiguration().MinimumLevel.Debug().Enrich.FromLogContext()
                .WriteTo.Async(sink =>
                {
                    if (testSink is not null) sink.Sink(testSink);
                    else sink.File(new RedactingJsonFormatter(Secrets), Path.Combine(LogsPath, "app-.log"),
                    rollingInterval: RollingInterval.Day, fileSizeLimitBytes: options.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true, retainedFileCountLimit: options.RetainedFileCount,
                    buffered: false);
                }, bufferSize: options.BufferSize, blockWhenFull: false, monitor: Health)
                .CreateLogger();
        }
        catch
        {
            Health.Failed();
            logger = new LoggerConfiguration().MinimumLevel.Debug()
                .WriteTo.Async(s => s.Sink(new DebugFallback()), bufferSize: options.BufferSize,
                    blockWhenFull: false, monitor: Health).CreateLogger();
        }
        Factory = new SerilogLoggerFactory(logger, dispose: true);
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= Task.Run(() =>
        {
            try
            {
                Factory.Dispose();
                // The async queue is drained and the rolling file handle is closed now.
                // Persist the final drop total outside that full queue, on this shutdown
                // worker, so reporting the loss cannot itself be dropped by the queue.
                if (Health.DroppedEvents > 0)
                {
                    using var summary = new LoggerConfiguration()
                        .WriteTo.File(new RedactingJsonFormatter(Secrets), Path.Combine(LogsPath, "app-.log"),
                            rollingInterval: RollingInterval.Day, fileSizeLimitBytes: options.FileSizeLimitBytes,
                            rollOnFileSizeLimit: true, retainedFileCountLimit: options.RetainedFileCount, buffered: false)
                        .CreateLogger();
                    summary.Warning("{EventName} Dropped={DroppedLogEvents}", "LogEventsDropped", Health.DroppedEvents);
                }
            }
            catch { Health.Failed(); }
            finally { Serilog.Debugging.SelfLog.Disable(); }
        }));
    }
    private sealed class DebugFallback : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => Debug.WriteLine("Immich Desktop Uploader: diagnostic event (file sink unavailable).");
    }
}
