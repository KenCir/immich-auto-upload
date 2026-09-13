using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Tests.Sessions;
using P = ImmichDesktopUploader.Tests.Management.PersistenceTests;

namespace ImmichDesktopUploader.Tests.Connections;

internal static class SessionRecoveryTests
{
    private sealed class Factory(FakeUploadBackend backend, FakeSessionClock clock) : IUploadSessionFactory
    {
        public UploadSession Session { get; private set; } = null!;
        public IManagedUploadSession Create(UploadSessionConfiguration configuration) => Session = new(configuration, backend, clock);
    }
    public static async Task RunAllAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Recovery actual Session: exhaustion, reset to zero and original 2/5/10 retry cycle", async () =>
        {
            var backend = new FakeUploadBackend(); var clock = new FakeSessionClock(); var factory = new Factory(backend, clock);
            var manager = new UploadManager(P.Settings(P.Folder()), factory, connectionGeneration: 1);
            try
            {
                await manager.StartAllAsync(); var run = (await backend.NextStartAsync()).Succeed();
                await ConnectionMonitorTests.Until(() => factory.Session.Snapshot.Status == UploadSessionStatus.Running);
                var outage = new ConnectionSnapshot(ConnectionStatus.Unavailable, clock.UtcNow, null, clock.UtcNow,
                    1, "Connection check failed.", 1, ConnectionStatus.Unavailable, 1, clock.UtcNow);
                await manager.ObserveConnectionAsync(outage);
                P.Equal(1, backend.StartCount); P.Equal(0, run.StopCalls);
                await Exhaust(run);
                P.Check(factory.Session.Snapshot.RetryExhausted, "Retry exhaustion metadata missing.");
                await manager.ObserveConnectionAsync(outage with { Sequence = 2 });
                P.Check(manager.Snapshot.Folders[0].RecoveryCandidate is not null, "Real Session not registered.");
                var edge = outage with { Sequence = 3, Status = ConnectionStatus.Reachable,
                    LastCompletedStatus = ConnectionStatus.Reachable, RecoveryEdge = true, LastCheckedAt = clock.UtcNow };
                await manager.ObserveConnectionAsync(edge);
                run = (await backend.NextStartAsync()).Succeed();
                await ConnectionMonitorTests.Until(() => factory.Session.Snapshot.Status == UploadSessionStatus.Running);
                P.Equal(0, factory.Session.Snapshot.RetryCount);
                P.Check(!factory.Session.Snapshot.RetryExhausted, "New cycle retained exhaustion flag.");
                await Exhaust(run);
                await manager.ObserveConnectionAsync(edge with { Sequence = 4, RecoveryEdge = false });
                await factory.Session.SynchronizeAsync(); P.Equal(8, backend.StartCount);
                P.Equal(UploadSessionStatus.Error, factory.Session.Snapshot.Status);
                P.Equal(1, backend.MaximumActiveCount); P.Equal(0, backend.ActiveCount);

                async Task Exhaust(FakeProcessRun current)
                {
                    foreach (var delay in new[] { 2, 5, 10 })
                    {
                        current.Exit(17);
                        await ConnectionMonitorTests.Until(() => factory.Session.Snapshot.Status == UploadSessionStatus.Restarting);
                        await factory.Session.SynchronizeAsync();
                        P.Equal(TimeSpan.FromSeconds(delay), clock.Delays.Last().Duration);
                        clock.Advance(TimeSpan.FromSeconds(delay));
                        current = (await backend.NextStartAsync()).Succeed();
                        await ConnectionMonitorTests.Until(() => factory.Session.Snapshot.Status == UploadSessionStatus.Running);
                    }
                    current.Exit(17);
                    await ConnectionMonitorTests.Until(() => factory.Session.Snapshot.Status == UploadSessionStatus.Error);
                    P.Equal(3, factory.Session.Snapshot.RetryCount);
                }
            }
            finally { backend.ReleaseAll(); clock.ReleaseAll(); await manager.DisposeAsync(); }
        });
    }
}
