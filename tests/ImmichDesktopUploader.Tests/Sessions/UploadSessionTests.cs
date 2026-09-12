using ImmichDesktopUploader.Application;

namespace ImmichDesktopUploader.Tests.Sessions;

internal static class UploadSessionTests
{
    public static async Task RunAllAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Session: basic lifecycle / immutable snapshot / activity", async () =>
        {
            await using var f = new Fixture();
            var initial = f.Session.Snapshot;
            Equal(UploadSessionStatus.Stopped, initial.Status);
            var run = await f.StartRunning();
            Equal(UploadSessionStatus.Stopped, initial.Status);
            Equal(1L, f.Session.Snapshot.RunGeneration); Equal(1234, f.Session.Snapshot.LauncherPid);
            f.Clock.Advance(TimeSpan.FromSeconds(1)); run.Emit("not an upload success");
            await f.Until(s => s.LastActivityAt == f.Clock.UtcNow);
            var stop = f.Session.StopAsync(); await stop;
            Equal(UploadSessionStatus.Stopped, f.Session.Snapshot.Status);
            Equal(SessionStopReason.UserRequested, f.Session.Snapshot.StopReason);
            Equal(1, run.StopCalls); Equal(1, run.DisposeCalls); Equal(0, f.Backend.ActiveCount);
        });
        await test("Session: Start idempotent in Starting / Running / Restarting / Stopping", async () =>
        {
            await using var f = new Fixture();
            await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => f.Session.StartAsync()));
            var call = await f.Backend.NextStartAsync();
            Equal(1, f.Backend.StartCount);
            var run = call.Succeed(); await f.Running();
            await f.Session.StartAsync(); Equal(1, f.Backend.StartCount);
            run.Exit(); await f.Status(UploadSessionStatus.Restarting);
            await f.Session.StartAsync(); Equal(1, f.Backend.StartCount);
            f.Clock.Advance(TimeSpan.FromSeconds(2));
            var next = await f.Backend.NextStartAsync(); var second = next.Succeed(); second.HoldStop = true;
            await f.Running();
            var stopping = f.Session.StopAsync(); await second.StopEntered.Task;
            await f.Session.StartAsync(); Equal(2, f.Backend.StartCount);
            second.CompleteStop(); await stopping;
            Equal(UploadSessionStatus.Stopped, f.Session.Snapshot.Status);
        });
        await test("Session: initial plus 3 retries; exact 2 / 5 / 10 backoffs", async () =>
        {
            await using var f = new Fixture();
            var run = await f.StartRunning();
            var waits = new[] { 2, 5, 10 };
            for (var i = 0; i < waits.Length; i++)
            {
                run.Exit((uint)(i == 0 ? 0 : 17)); await f.Status(UploadSessionStatus.Restarting);
                Equal(i + 1, f.Session.Snapshot.RetryCount);
                await f.Session.SynchronizeAsync();
                Equal(TimeSpan.FromSeconds(waits[i]), f.Clock.Delays.Last().Duration);
                f.Clock.Advance(TimeSpan.FromSeconds(waits[i]) - TimeSpan.FromTicks(1));
                await f.Session.SynchronizeAsync(); Equal(i + 1, f.Backend.StartCount);
                f.Clock.Advance(TimeSpan.FromTicks(1));
                run = (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
                Equal((long)i + 2, f.Session.Snapshot.RunGeneration);
            }
            run.Exit(0); await f.Status(UploadSessionStatus.Error);
            Equal(4, f.Backend.StartCount); Equal(3, f.Session.Snapshot.RetryCount);
            Equal(SessionErrorKind.UnexpectedExit, f.Session.Snapshot.LastError!.Kind);
            Equal(0u, f.Session.Snapshot.LastError.ExitCode);
            f.Clock.Advance(TimeSpan.FromHours(1)); await f.Session.SynchronizeAsync();
            await f.Session.StartAsync(); Equal(4, f.Backend.StartCount);
            await f.Session.RestartAsync(); (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            Equal(0, f.Session.Snapshot.RetryCount); Check(f.Session.Snapshot.LastError is not null, "Historical error was erased.");
        });
        await test("Session: stable reset at 30 seconds, not 29", async () =>
        {
            await using var f = new Fixture(); var run = await f.StartRunning();
            run.Exit(5); await f.Status(UploadSessionStatus.Restarting);
            f.Clock.Advance(TimeSpan.FromSeconds(2)); (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            var generation = f.Session.Snapshot.RunGeneration;
            f.Clock.Advance(TimeSpan.FromSeconds(29)); await f.Session.SynchronizeAsync(); Equal(1, f.Session.Snapshot.RetryCount);
            f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Until(s => s.RetryCount == 0);
            Equal(generation, f.Session.Snapshot.RunGeneration); Equal(2, f.Backend.StartCount);
        });
        await test("Session: late stable timer cannot reset next generation", async () =>
        {
            await using var f = new Fixture(); var run = await f.StartRunning();
            f.Clock.Delays.Last().PreserveAfterCancellation = true;
            f.Clock.Advance(TimeSpan.FromSeconds(20)); run.Exit(); await f.Status(UploadSessionStatus.Restarting);
            f.Clock.Advance(TimeSpan.FromSeconds(2)); (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            f.Clock.Advance(TimeSpan.FromSeconds(8)); await f.Session.SynchronizeAsync();
            Equal(2L, f.Session.Snapshot.RunGeneration); Equal(1, f.Session.Snapshot.RetryCount);
        });
        await test("Session: Manual Restart from Stopped / Running / Restarting / Error", async () =>
        {
            await using var f = new Fixture();
            await f.Session.RestartAsync(); var run = (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            await f.Session.RestartAsync(); var second = (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            Equal(1, run.StopCalls); Equal(0, f.Session.Snapshot.RetryCount);
            second.Exit(); await f.Status(UploadSessionStatus.Restarting);
            await f.Session.RestartAsync(); (await f.Backend.NextStartAsync()).Fail(BackendFailureKind.NonRetryable);
            await f.Status(UploadSessionStatus.Error);
            await f.Session.RestartAsync(); (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            Equal(0, f.Session.Snapshot.RetryCount);
        });
        await test("Session: repeated Stop and Restart coalesce during cleanup", async () =>
        {
            await using var f = new Fixture(); var run = await f.StartRunning(); run.HoldStop = true;
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => f.Session.RestartAsync()));
            await run.StopEntered.Task; Equal(1, run.StopCalls); Equal(1, f.Backend.StartCount);
            run.CompleteStop(); var second = (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            second.HoldStop = true;
            var stops = Enumerable.Range(0, 20).Select(_ => f.Session.StopAsync()).ToArray();
            await second.StopEntered.Task; await f.Session.SynchronizeAsync(); Equal(1, second.StopCalls);
            second.CompleteStop(); await Task.WhenAll(stops); Equal(0, f.Backend.ActiveCount);
        });
        await test("Session: all intentional stop reasons cancel backoff", async () =>
        {
            foreach (var reason in Enum.GetValues<SessionStopReason>())
            {
                await using var f = new Fixture(); var run = await f.StartRunning(); run.Exit();
                await f.Status(UploadSessionStatus.Restarting); await f.Session.SynchronizeAsync();
                f.Clock.Delays.Last().PreserveAfterCancellation = true;
                await f.Session.StopAsync(reason);
                f.Clock.Advance(TimeSpan.FromHours(1)); await f.Session.SynchronizeAsync();
                Equal(1, f.Backend.StartCount); Equal(UploadSessionStatus.Stopped, f.Session.Snapshot.Status);
                Equal(reason, f.Session.Snapshot.StopReason);
            }
        });
        await test("Session: late retry timer after manual restart is ignored", async () =>
        {
            await using var f = new Fixture(); var run = await f.StartRunning(); run.Exit();
            await f.Status(UploadSessionStatus.Restarting); await f.Session.SynchronizeAsync();
            f.Clock.Delays.Last().PreserveAfterCancellation = true;
            await f.Session.RestartAsync(); (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            f.Clock.Advance(TimeSpan.FromSeconds(2)); await f.Session.SynchronizeAsync();
            Equal(2, f.Backend.StartCount); Equal(0, f.Session.Snapshot.RetryCount);
        });
        await test("Session: Stop intent precedes synchronous exit notification", async () =>
        {
            await using var f = new Fixture(); var run = await f.StartRunning(); run.HoldStop = true;
            var stop = f.Session.StopAsync(); run.Exit(0);
            await run.StopEntered.Task; run.CompleteStop(); await stop;
            f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.Session.SynchronizeAsync();
            Equal(1, f.Backend.StartCount); Equal(UploadSessionStatus.Stopped, f.Session.Snapshot.Status);
        });
        await test("Session: Restart intent and exit race creates one replacement", async () =>
        {
            await using var f = new Fixture(); var run = await f.StartRunning();
            var restart = f.Session.RestartAsync(); run.Exit(7); await restart;
            (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            Equal(2, f.Backend.StartCount); Equal(0, f.Session.Snapshot.RetryCount);
        });
        await test("Session: Stop while backend start is pending adopts and cleans late run", async () =>
        {
            await using var f = new Fixture(); await f.Session.StartAsync(); var call = await f.Backend.NextStartAsync();
            var stop = f.Session.StopAsync(SessionStopReason.Disabled); await f.Status(UploadSessionStatus.Stopping);
            Check(call.Token.IsCancellationRequested, "Pending start was not canceled."); Check(!stop.IsCompleted, "Stop abandoned pending start.");
            var late = call.Succeed(); await stop;
            Equal(1, late.StopCalls); Equal(1, late.DisposeCalls); Equal(1, f.Backend.StartCount);
            Equal(UploadSessionStatus.Stopped, f.Session.Snapshot.Status);
        });
        await test("Session: Restart while start is pending waits for late run disposal", async () =>
        {
            await using var f = new Fixture(); await f.Session.StartAsync(); var call = await f.Backend.NextStartAsync();
            await f.Session.RestartAsync(); await f.Status(UploadSessionStatus.Stopping);
            var late = f.Backend.NewRun(); late.HoldDispose = true; call.Succeed(late);
            await late.DisposeEntered.Task; Equal(1, f.Backend.StartCount);
            late.CompleteDispose(); (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            Equal(2L, f.Session.Snapshot.RunGeneration);
        });
        await test("Session: canceled start failure cannot schedule retry", async () =>
        {
            await using var f = new Fixture(); await f.Session.StartAsync(); var call = await f.Backend.NextStartAsync();
            var stop = f.Session.StopAsync(); await f.Status(UploadSessionStatus.Stopping);
            call.Fail(new OperationCanceledException()); await stop;
            Equal(0, f.Session.Snapshot.RetryCount); Equal(1, f.Backend.StartCount);
        });
        await test("Session: old RunExited completion cannot change new run", async () =>
        {
            await using var f = new Fixture(); var old = f.Backend.NewRun();
            old.IgnoreObservationCancellation = true; old.DelayExitNotification = true;
            await f.StartRunning(old); await old.ObservationEntered.Task;
            await f.Session.RestartAsync(); (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            var before = f.Session.Snapshot; old.Exit(99);
            await f.Session.DrainRetiredGenerationAsync(1);
            Equal(before, f.Session.Snapshot); Equal(2, f.Backend.StartCount);
        });
        await test("Session: settings restart resets count and uses newest configuration", async () =>
        {
            await using var f = new Fixture(); var run = await f.StartRunning(); run.Exit();
            await f.Status(UploadSessionStatus.Restarting);
            await f.Session.ApplyConfigurationAsync(f.Configuration with { Path = "new-path" });
            var call = await f.Backend.NextStartAsync(); Equal("new-path", call.Request.Configuration.Path);
            var next = call.Succeed(); next.HoldStop = true; await f.Running();
            Equal(0, f.Session.Snapshot.RetryCount);
            await f.Session.ApplyConfigurationAsync(f.Configuration with { Path = "middle" });
            await next.StopEntered.Task;
            await f.Session.ApplyConfigurationAsync(f.Configuration with { Path = "latest" });
            next.CompleteStop(); var latest = await f.Backend.NextStartAsync(); Equal("latest", latest.Request.Configuration.Path);
            latest.Succeed(); await f.Running();
        });
        await test("Session: retryable vs non-retryable start failures; no secret errors", async () =>
        {
            await using var f = new Fixture(); await f.Session.StartAsync();
            (await f.Backend.NextStartAsync()).Fail(new InvalidOperationException("API-KEY-DO-NOT-LOG"));
            await f.Status(UploadSessionStatus.Restarting); Equal(1, f.Session.Snapshot.RetryCount);
            Check(!f.Session.Snapshot.LastError!.Summary.Contains("API-KEY"), "Raw exception escaped.");
            f.Clock.Advance(TimeSpan.FromSeconds(2)); (await f.Backend.NextStartAsync()).Fail(BackendFailureKind.NonRetryable);
            await f.Status(UploadSessionStatus.Error); Equal(2, f.Backend.StartCount);
            await f.Session.RestartAsync(); (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            Equal(SessionErrorKind.StartFailed, f.Session.Snapshot.LastError!.Kind);
        });
        await test("Session: manual restart can interrupt backoff after a failed manual attempt", async () =>
        {
            await using var f = new Fixture(); await f.Session.RestartAsync();
            (await f.Backend.NextStartAsync()).Fail(BackendFailureKind.Retryable);
            await f.Status(UploadSessionStatus.Restarting); Equal(1, f.Session.Snapshot.RetryCount);
            await f.Session.RestartAsync(); (await f.Backend.NextStartAsync()).Succeed(); await f.Running();
            Equal(0, f.Session.Snapshot.RetryCount); Equal(2, f.Backend.StartCount);
        });
        await test("Session: observation/output failure stops run before retry", async () =>
        {
            foreach (var output in new[] { false, true })
            {
                await using var f = new Fixture(); var run = await f.StartRunning();
                if (output) run.FailOutput(); else run.FailObservation();
                await f.Status(UploadSessionStatus.Restarting);
                Equal(1, run.StopCalls); Equal(1, run.DisposeCalls); Equal(0, f.Backend.ActiveCount);
                Equal(output ? SessionErrorKind.OutputFailed : SessionErrorKind.ObservationFailed, f.Session.Snapshot.LastError!.Kind);
            }
        });
        await test("Session: Stop failure is fatal and quarantines unconfirmed ownership", async () =>
        {
            await using var f = new Fixture { ExpectDisposeFailure = true }; var run = await f.StartRunning(); run.ThrowOnStop = true;
            await Throws<InvalidOperationException>(() => f.Session.StopAsync()); await f.Status(UploadSessionStatus.Error);
            Equal(SessionErrorKind.CleanupFailed, f.Session.Snapshot.LastError!.Kind);
            Check(!f.Session.Snapshot.LastError.Summary.Contains("secret"), "Cleanup error leaked secret.");
            await f.Session.RestartAsync(); await f.Session.StartAsync(); Equal(1, f.Backend.StartCount);
            Equal(1, run.DisposeCalls); Equal(0, f.Backend.ActiveCount);
        });
        await test("Session: Stop result with unconfirmed tree blocks next run", async () =>
        {
            await using var f = new Fixture { ExpectDisposeFailure = true }; var run = await f.StartRunning(); run.HoldStop = true;
            var restart = f.Session.RestartAsync(); await restart; await run.StopEntered.Task;
            run.CompleteStop(new(1, true, false, false, 0, "secret cleanup detail")); await f.Status(UploadSessionStatus.Error);
            await f.Session.RestartAsync(); Equal(1, f.Backend.StartCount);
        });
        await test("Session: Dispose failure blocks replacement even if tree exited", async () =>
        {
            await using var f = new Fixture { ExpectDisposeFailure = true }; var run = await f.StartRunning(); run.ThrowOnDispose = true;
            await f.Session.RestartAsync(); await f.Status(UploadSessionStatus.Error);
            await f.Session.RestartAsync(); await f.Session.StartAsync(); Equal(1, f.Backend.StartCount);
            Equal(SessionErrorKind.CleanupFailed, f.Session.Snapshot.LastError!.Kind);
        });
        await test("Session: canceled Stop caller does not abandon cleanup", async () =>
        {
            await using var f = new Fixture(); var run = await f.StartRunning(); run.HoldStop = true;
            using var caller = new CancellationTokenSource(); var stop = f.Session.StopAsync(cancellationToken: caller.Token);
            await run.StopEntered.Task; caller.Cancel(); await Throws<OperationCanceledException>(() => stop);
            run.CompleteStop(); await f.Status(UploadSessionStatus.Stopped); Equal(0, f.Backend.ActiveCount);
        });
        await test("Session: Dispose during Running / backoff cancels workers and rejects commands", async () =>
        {
            foreach (var backoff in new[] { false, true })
            {
                await using var f = new Fixture(); var run = await f.StartRunning();
                if (backoff) { run.Exit(); await f.Status(UploadSessionStatus.Restarting); }
                await f.Session.DisposeAsync();
                await Throws<ObjectDisposedException>(() => f.Session.StartAsync());
                await Throws<ObjectDisposedException>(() => f.Session.RestartAsync());
                await f.Session.DisposeAsync();
                f.Clock.Advance(TimeSpan.FromHours(1));
                Equal(0, f.Clock.PendingCount); Equal(1, f.Backend.StartCount); Equal(0, f.Backend.ActiveCount);
            }
        });
        await test("Session: Dispose waits for pending start and delayed old observer", async () =>
        {
            await using var f = new Fixture(); await f.Session.StartAsync(); var call = await f.Backend.NextStartAsync();
            var disposing = f.Session.DisposeAsync().AsTask();
            Check(!disposing.IsCompleted, "Dispose abandoned backend start.");
            var run = call.Succeed(); await disposing; Equal(1, run.StopCalls); Equal(0, f.Backend.ActiveCount);

            await using var g = new Fixture(); var delayed = g.Backend.NewRun();
            delayed.IgnoreObservationCancellation = true; delayed.DelayExitNotification = true;
            await g.StartRunning(delayed); await delayed.ObservationEntered.Task;
            var end = g.Session.DisposeAsync().AsTask(); await delayed.DisposeEntered.Task;
            await g.Status(UploadSessionStatus.Stopped); // Cleanup is finished; only the old observer remains.
            Check(!end.IsCompleted, "Unobserved old exit task was abandoned.");
            delayed.Exit(); await end;
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeUploadBackend Backend { get; } = new();
        public FakeSessionClock Clock { get; } = new();
        public UploadSessionConfiguration Configuration { get; } = new(Guid.NewGuid(), "test-folder");
        public UploadSession Session { get; }
        public bool ExpectDisposeFailure { get; init; }
        public Fixture() => Session = new(Configuration, Backend, Clock);
        public async Task<FakeProcessRun> StartRunning(FakeProcessRun? run = null)
        {
            await Session.StartAsync(); var call = await Backend.NextStartAsync();
            await Status(UploadSessionStatus.Starting);
            var result = call.Succeed(run); await Running(); return result;
        }
        public async Task Running() { await Status(UploadSessionStatus.Running); await Session.SynchronizeAsync(); }
        public Task Status(UploadSessionStatus status) => Until(s => s.Status == status);
        public async Task Until(Func<SessionSnapshot, bool> predicate)
        {
            // Watchdog for deadlock detection only. All retry/stable time is driven by FakeSessionClock.
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!predicate(Session.Snapshot))
            {
                try { await Session.Changes.ReadAsync(watchdog.Token); }
                catch (OperationCanceledException) { throw new Exception($"State did not arrive. Current: {Session.Snapshot}"); }
            }
            Check(Backend.MaximumActiveCount <= 1, "Session created overlapping active runs.");
            if (Session.Snapshot.Status is UploadSessionStatus.Stopped or UploadSessionStatus.Error)
                Equal(0, Backend.ActiveCount);
        }
        public async ValueTask DisposeAsync()
        {
            var disposal = Session.DisposeAsync().AsTask();
            try
            {
                if (ExpectDisposeFailure) await Throws<InvalidOperationException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(15)));
                else await disposal.WaitAsync(TimeSpan.FromSeconds(15));
                // Assert before emergency fake cleanup, so teardown cannot mask an ownership leak.
                Equal(0, Backend.ActiveCount);
                Check(Backend.MaximumActiveCount <= 1, "Session created overlapping active runs.");
                Equal(0, Clock.PendingCount);
            }
            finally
            {
                Backend.ReleaseAll(); Clock.ReleaseAll();
                try { await disposal.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch when (disposal.IsCompleted) { /* Already observed above (including expected quarantine). */ }
            }
        }
    }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
}
