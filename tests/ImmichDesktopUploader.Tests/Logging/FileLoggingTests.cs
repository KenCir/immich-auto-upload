using System.Text.Json;
using Microsoft.Extensions.Logging;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Logging;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Windows;
using ImmichDesktopUploader.Tests.Sessions;
using ImmichDesktopUploader.Tests.Connections;
using ImmichDesktopUploader.Tests.Management;
using ImmichDesktopUploader.Infrastructure.Persistence;
using Serilog.Core;
using Serilog.Events;
using P = ImmichDesktopUploader.Tests.Management.PersistenceTests;

namespace ImmichDesktopUploader.Tests.Logging;

internal static class FileLoggingTests
{
    public static async Task RunAllAsync(string host, Func<string, Func<Task>, Task> test)
    {
        await test("Logging: UI failure metadata and incomplete output are explicit without exception payload", async () =>
        {
            using var fixture = new P.StorageFixture(); var path = Path.Combine(fixture.Paths.DirectoryPath, "logs");
            await using (var logging = new FileLogging(path))
            {
                var diagnostics = new AppDiagnostics(logging.Factory.CreateLogger("MetadataTest"));
                diagnostics.UiFailure(new InvalidOperationException("do-not-log-decrypted-payload"));
                diagnostics.OutputCollectionInterrupted(Guid.NewGuid(), 8, 123);
                diagnostics.ProbeOutputCompleted(9, 124, 3, false);
            }
            var records = Directory.GetFiles(path).SelectMany(File.ReadAllLines).ToArray();
            P.Equal(3, records.Length);
            P.Check(!string.Join('\n', records).Contains("do-not-log-decrypted-payload"), "Exception payload leaked.");
            using var failure = JsonDocument.Parse(records[0]);
            P.Equal(typeof(InvalidOperationException).FullName, failure.RootElement.GetProperty("Properties").GetProperty("ExceptionType").GetString());
            using var interrupted = JsonDocument.Parse(records[1]);
            P.Check(interrupted.RootElement.GetProperty("Properties").GetProperty("TailMayBeIncomplete").GetBoolean(), "Interrupted collection is presented as complete.");
            using var probe = JsonDocument.Parse(records[2]);
            P.Equal("Warning", probe.RootElement.GetProperty("Level").GetString());
            P.Equal(3L, probe.RootElement.GetProperty("Properties").GetProperty("DroppedOutputChunks").GetInt64());
        });
        await test("Logging: connection and recovery events include generation and consumed edge", async () =>
        {
            using var fixture = new P.StorageFixture(); var path = Path.Combine(fixture.Paths.DirectoryPath, "logs");
            await using (var logging = new FileLogging(path))
            {
                var diagnostics = new AppDiagnostics(logging.Factory.CreateLogger("RecoveryLoggingTest"));
                var clock = new FakeSessionClock(); var probe = new ControlledProbe(); var factory = new ManagerFactory(); var folder = P.Folder();
                await using var manager = new UploadManager(P.Settings(folder), factory, diagnostics, connectionGeneration: 1);
                await using var monitor = new ConnectionMonitor(clock, diagnostics);
                await manager.StartAllAsync(); monitor.Configure(probe, 1); (await probe.Next()).Result.SetResult(false);
                await ConnectionMonitorTests.Until(() => clock.Delays.Any(d => d.Duration.TotalSeconds == 30 && !d.Task.IsCompleted));
                var session = factory.Sessions[folder.Id];
                session.Publish(session.Snapshot with { Status = UploadSessionStatus.Error, RunGeneration = 4, RetryCount = 3,
                    RetryExhausted = true, LastError = new(clock.UtcNow, SessionErrorKind.UnexpectedExit, "Upload run exited.") });
                await manager.ObserveConnectionAsync(monitor.Snapshot);
                clock.Advance(TimeSpan.FromSeconds(30)); (await probe.Next()).Result.SetResult(true);
                await ConnectionMonitorTests.Until(() => monitor.Snapshot.Status == ConnectionStatus.Reachable);
                await manager.ObserveConnectionAsync(monitor.Snapshot); P.Equal(1, session.Restarts);
                await manager.ObserveConnectionAsync(monitor.Snapshot); // Suppressed stale callback is logged.
            }
            var text = string.Join('\n', Directory.GetFiles(path).Select(File.ReadAllText));
            foreach (var name in new[] { "ProbeStarted", "ProbeFailed", "ProbeSucceeded", "ConnectionChanged", "ConnectionStatus",
                "ConnectionGenerationChanged", "RecoveryCandidateRegistered", "RecoveryEdgeDetected", "RecoveryConsumed", "RecoveryAttempted", "RecoverySuppressed", "StaleEvent" })
                P.Check(text.Contains(name), "Missing connection/recovery diagnostic: " + name);
        });
        await test("Logging: settings, credential failures and startup changes contain no payload", async () =>
        {
            using var fixture = new P.StorageFixture(); var path = Path.Combine(fixture.Paths.DirectoryPath, "logs");
            const string key = "private-test-key-logging-settings";
            await using (var logging = new FileLogging(path))
            {
                var diagnostics = new AppDiagnostics(logging.Factory.CreateLogger("PersistenceLoggingTest"), logging.Secrets.Register);
                var settings = new SettingsService(fixture.Paths, diagnostics); await settings.LoadAsync(); await settings.SaveAsync(P.Settings());
                await P.Fails(() => settings.RecoverBackupAsync(), AppFailure.InvalidSettings);
                var credentials = new CredentialService(fixture.Paths, diagnostics); await credentials.SaveAsync(new(P.Url, key));
                await P.Fails(() => credentials.LoadAsync("https://mismatch.invalid/api"), AppFailure.CredentialMismatch);
                await credentials.LoadAsync(P.Url);
                var startup = new StartupService("C:\\test\\app.exe", new MemoryRegistry(), diagnostics);
                startup.Register(); startup.Unregister();
            }
            var text = string.Join('\n', Directory.GetFiles(path).Select(File.ReadAllText));
            foreach (var name in new[] { "SettingsLoadFailed", "SettingsSaved", "SettingsRecoveryFailed", "CredentialsSaved", "CredentialsLoaded", "CredentialMismatch", "StartupRegistered", "StartupUnregistered" })
                P.Check(text.Contains(name), "Missing persistence log: " + name);
            P.Check(!text.Contains(key) && !text.Contains("ApiKey") && !text.Contains("Payload"), "Credential payload logged.");
        });
        await test("Logging: blocked sink never blocks producers; drop count survives flush", async () =>
        {
            using var fixture = new P.StorageFixture(); var sink = new BlockingSink();
            var logging = new FileLogging(Path.Combine(fixture.Paths.DirectoryPath, "logs"), new(BufferSize: 2), sink);
            var logger = logging.Factory.CreateLogger("SlowSinkTest");
            logger.LogInformation("Start"); await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var producer = Task.Run(() => { for (var i = 0; i < 500; i++) logger.LogInformation("Output {Index}", i); });
                await producer.WaitAsync(TimeSpan.FromSeconds(2));
                P.Check(logging.Health.DroppedEvents > 0, "Full queue did not count dropped events.");
                var disposal = logging.DisposeAsync().AsTask();
                P.Check(!disposal.IsCompleted, "Flush did not await the blocked sink.");
                sink.Release.Set(); await disposal.WaitAsync(TimeSpan.FromSeconds(5));
                P.Check(logging.Health.DroppedEvents > 0, "Final drop count was lost on disposal.");
                var summary = string.Join('\n', Directory.GetFiles(logging.LogsPath, "*.log").Select(File.ReadAllText));
                P.Check(summary.Contains("LogEventsDropped") && summary.Contains("DroppedLogEvents"), "Final dropped-event summary was not persisted.");
            }
            finally { sink.Release.Set(); await logging.DisposeAsync(); sink.Release.Dispose(); }
        });
        await test("Logging: actual Session lifecycle, retry and stderr source are recorded", async () =>
        {
            using var fixture = new P.StorageFixture(); var path = Path.Combine(fixture.Paths.DirectoryPath, "logs");
            var backend = new FakeUploadBackend(); var clock = new FakeSessionClock();
            await using (var logging = new FileLogging(path))
            {
                var diagnostics = new AppDiagnostics(logging.Factory.CreateLogger("SessionTest"));
                await using var session = new UploadSession(new(Guid.NewGuid(), "test-folder"), backend, clock, diagnostics);
                try
                {
                    await session.StartAsync(); var run = (await backend.NextStartAsync()).Succeed();
                    await ConnectionMonitorTests.Until(() => session.Snapshot.Status == UploadSessionStatus.Running);
                    run.Emit("ordinary stderr diagnostic");
                    await ConnectionMonitorTests.Until(() => session.Snapshot.LastActivityAt is not null);
                    run.Exit(17); await ConnectionMonitorTests.Until(() => session.Snapshot.Status == UploadSessionStatus.Restarting);
                    await session.SynchronizeAsync(); clock.Advance(TimeSpan.FromSeconds(2));
                    (await backend.NextStartAsync()).Succeed();
                    await ConnectionMonitorTests.Until(() => session.Snapshot.Status == UploadSessionStatus.Running);
                    await session.StopAsync();
                }
                finally { backend.ReleaseAll(); clock.ReleaseAll(); }
            }
            var text = string.Join('\n', Directory.GetFiles(path).Select(File.ReadAllText));
            foreach (var expected in new[] { "SessionStartRequested", "SessionStarting", "SessionRunning", "SessionRetryScheduled",
                "SessionRetryStarted", "SessionUnexpectedExit", "SessionStopped", "SessionDisposed", "Stderr", "FolderId", "RunGeneration" })
                P.Check(text.Contains(expected), "Missing lifecycle log: " + expected);
        });
        await test("Logging: file failure does not stop manager startup or cleanup", async () =>
        {
            using var fixture = new P.StorageFixture(); Directory.CreateDirectory(fixture.Paths.DirectoryPath);
            var blocked = Path.Combine(fixture.Paths.DirectoryPath, "blocked"); await File.WriteAllTextAsync(blocked, "file");
            await using var logging = new FileLogging(Path.Combine(blocked, "logs"));
            var diagnostics = new AppDiagnostics(logging.Factory.CreateLogger("FailureTest"), logging.Secrets.Register);
            var factory = new ManagerFactory(); var folder = P.Folder();
            await using (var coordinator = new AppCoordinator(new MemorySettings(P.Settings(folder)),
                new MemoryCredentials(new(P.Url, "fake-key")), _ => factory, diagnostics))
            {
                await coordinator.StartAsync(); P.Equal(1, factory.Sessions[folder.Id].Starts);
                P.Equal(UploadSessionStatus.Running, coordinator.Snapshot!.Folders[0].Session.Status);
            }
            P.Check(factory.Sessions[folder.Id].Disposed, "Logging failure interrupted cleanup.");
        });
        await test("Logging: fatal diagnostic flushes safe metadata without message or credential blob", async () =>
        {
            using var fixture = new P.StorageFixture(); var path = Path.Combine(fixture.Paths.DirectoryPath, "logs");
            await using var logging = new FileLogging(path); using var crash = new CrashDiagnostics(logging);
            var error = new InvalidOperationException("plaintext-credential-test"); error.Data["blob"] = "dpapi-blob-test";
            crash.Record("FatalHookTest", error, true); await logging.DisposeAsync();
            var text = string.Join('\n', Directory.GetFiles(path).Select(File.ReadAllText));
            P.Check(text.Contains("FatalHookTest") && text.Contains("InvalidOperationException"), "Crash metadata not flushed.");
            P.Check(!text.Contains("plaintext-credential-test") && !text.Contains("dpapi-blob-test"), "Crash hook exposed private exception data.");
        });
        await test("Logging: split API key from actual CLI output remains redacted in file", async () =>
        {
            using var fixture = new P.StorageFixture(); var path = Path.Combine(fixture.Paths.DirectoryPath, "logs");
            const string secret = "split-secret-logging-283794";
            await using (var logging = new FileLogging(path))
            {
                logging.Secrets.Register(secret);
                var diagnostics = new AppDiagnostics(logging.Factory.CreateLogger("CliOutputTest"), logging.Secrets.Register);
                await using var run = await new WindowsProcessRunner().StartAsync(LauncherCommandBuilder.Build(host,
                    ["secret-split"], "https://example.invalid/api", secret));
                var drain = Task.Run(async () =>
                {
                    await foreach (var output in run.Output.ReadAllAsync()) diagnostics.Output(output, Guid.Empty, 1, run.RootProcessId);
                });
                await run.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await drain;
                diagnostics.OutputLoss(Guid.Empty, 1, 23, true);
            }
            var text = string.Join('\n', Directory.GetFiles(path).Select(File.ReadAllText));
            P.Check(!text.Contains(secret) && text.Contains("[REDACTED]"), "Split secret leaked or output missing.");
            P.Check(text.Contains("Stdout") && text.Contains("DroppedOutputChunks"), "Output source/loss context missing.");
        });
        await test("Logging: structured file events, escaped secrets and exceptions are sanitized on flush", async () =>
        {
            using var fixture = new P.StorageFixture();
            var path = Path.Combine(fixture.Paths.DirectoryPath, "logs");
            var logging = new FileLogging(path);
            var secret = "synthetic-key-\"\\-98765"; logging.Secrets.Register(secret);
            var logger = logging.Factory.CreateLogger("LoggingTests");
            var diagnostics = new AppDiagnostics(logger, logging.Secrets.Register);
            diagnostics.Emit(AppEventKind.ProbeStarted, generation: 7);
            logger.LogWarning(new InvalidOperationException("failure " + secret),
                "Command {CommandSummary} secret {Credential}", "immich server-info " + secret, secret);
            logger.LogInformation("{EventName} {Payload}", "StructuredTest", new Dictionary<string, object> { ["nested"] = secret });
            await logging.DisposeAsync(); await logging.DisposeAsync();
            var files = Directory.GetFiles(path, "app-*.log"); P.Equal(1, files.Length);
            var lines = File.ReadAllLines(files[0]); P.Equal(3, lines.Length);
            foreach (var line in lines)
            {
                using var json = JsonDocument.Parse(line);
                P.Check(!ContainsSecret(json.RootElement, secret), "Secret persisted in structured JSON.");
            }
            using var first = JsonDocument.Parse(lines[0]);
            P.Equal("ProbeStarted", first.RootElement.GetProperty("Properties").GetProperty("EventName").GetString());
            P.Equal(7L, first.RootElement.GetProperty("Properties").GetProperty("ConnectionGeneration").GetInt64());
            P.Check(string.Join('\n', lines).Contains("[REDACTED]"), "Redaction marker missing.");
        });
        await test("Logging: size rollover and bounded file retention", async () =>
        {
            using var fixture = new P.StorageFixture(); var path = Path.Combine(fixture.Paths.DirectoryPath, "logs");
            await using (var logging = new FileLogging(path, new(512, 3, 256)))
            {
                var logger = logging.Factory.CreateLogger("RotationTests");
                for (var i = 0; i < 40; i++) logger.LogInformation("{EventName} {Index} {Data}", "Rotation", i, new string('x', 300));
            }
            var files = Directory.GetFiles(path, "app-*.log"); P.Equal(3, files.Length);
            P.Check(files.All(f => new FileInfo(f).Length < 4096), "Unexpectedly large rollover file.");
            P.Check(files.All(f => Path.GetFileName(f).Contains(DateTime.Now.ToString("yyyyMMdd"))), "Daily filename missing.");
        });
        await test("Logging: unwritable log directory falls back without throwing into application", async () =>
        {
            using var fixture = new P.StorageFixture(); Directory.CreateDirectory(fixture.Paths.DirectoryPath);
            var blocked = Path.Combine(fixture.Paths.DirectoryPath, "blocked"); await File.WriteAllTextAsync(blocked, "file");
            await using var logging = new FileLogging(Path.Combine(blocked, "logs"));
            P.Check(logging.Health.FileUnavailable, "Write failure not exposed to GUI health.");
            new AppDiagnostics(logging.Factory.CreateLogger("FallbackTest")).Emit(AppEventKind.ManagerStarted);
        });
    }
    private static bool ContainsSecret(JsonElement value, string secret) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(p => p.Name.Contains(secret) || ContainsSecret(p.Value, secret)),
        JsonValueKind.Array => value.EnumerateArray().Any(v => ContainsSecret(v, secret)),
        JsonValueKind.String => value.GetString()!.Contains(secret),
        _ => false
    };
    private sealed class BlockingSink : ILogEventSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void Emit(LogEvent logEvent) { Entered.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(10)); }
    }
    private sealed class MemoryRegistry : IStartupRegistryStore
    {
        private string? value;
        public string? Read() => value;
        public void Write(string command) => value = command;
        public void Delete() => value = null;
    }
}
