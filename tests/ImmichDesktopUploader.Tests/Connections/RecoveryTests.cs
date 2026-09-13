using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Tests.Management;
using P = ImmichDesktopUploader.Tests.Management.PersistenceTests;

namespace ImmichDesktopUploader.Tests.Connections;

internal static class RecoveryTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private static ConnectionSnapshot Outage(long sequence = 1, long outage = 1, int since = 10) =>
        new(ConnectionStatus.Unavailable, Epoch.AddSeconds(since), null, Epoch.AddSeconds(since), 1,
            "Connection check failed.", sequence, ConnectionStatus.Unavailable, outage, Epoch.AddSeconds(since));
    private static ConnectionSnapshot Edge(long sequence = 2, long outage = 1, int since = 10, int at = 30) =>
        Outage(sequence, outage, since) with { Status = ConnectionStatus.Reachable,
            LastCompletedStatus = ConnectionStatus.Reachable, LastCheckedAt = Epoch.AddSeconds(at), RecoveryEdge = true };
    private static void Fail(ManagerSession session, int at = 20, long run = 4, bool exhausted = true) =>
        session.Publish(session.Snapshot with { Status = UploadSessionStatus.Error, RunGeneration = run,
            LauncherPid = null, RetryCount = 3, RetryExhausted = exhausted, StopReason = null,
            LastError = new(Epoch.AddSeconds(at), SessionErrorKind.UnexpectedExit, "Process exited.") });

    public static async Task RunAllAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Recovery: candidate consumed once; sustained success and pre-outage failure cannot restart", async () =>
        {
            var folder = P.Folder(); var factory = new ManagerFactory();
            await using var manager = new UploadManager(P.Settings(folder), factory, connectionGeneration: 1);
            await manager.StartAllAsync(); var session = factory.Sessions[folder.Id]; Fail(session);
            await manager.ObserveConnectionAsync(Outage());
            P.Check(manager.Snapshot.Folders[0].RecoveryCandidate is not null, "Candidate missing.");
            await manager.ObserveConnectionAsync(Edge()); P.Equal(1, session.Restarts);
            P.Check(manager.Snapshot.Folders[0].RecoveryCandidate is null, "Candidate not consumed.");
            Fail(session, 35, 8);
            await manager.ObserveConnectionAsync(Edge());
            await manager.ObserveConnectionAsync(Edge(3));
            await manager.ObserveConnectionAsync(Edge(4) with { RecoveryEdge = false });
            P.Equal(1, session.Restarts);
            await manager.ObserveConnectionAsync(Outage(5, 2, 40));
            await manager.ObserveConnectionAsync(Edge(6, 2, 40, 60));
            P.Equal(1, session.Restarts);
            Fail(session, 75, 12);
            await manager.ObserveConnectionAsync(Outage(7, 3, 70));
            await manager.ObserveConnectionAsync(Edge(8, 3, 70, 90));
            P.Equal(2, session.Restarts);
        });
        await test("Recovery: running, restarting, nonretryable, local cleanup and late errors are excluded", async () =>
        {
            var folder = P.Folder(); var factory = new ManagerFactory();
            await using var manager = new UploadManager(P.Settings(folder), factory, connectionGeneration: 1);
            await manager.StartAllAsync(); var session = factory.Sessions[folder.Id];
            long sequence = 0, outage = 0;
            foreach (var mode in new[] { "running", "restarting", "nonretryable", "cleanup", "before", "after", "manual" })
            {
                Fail(session);
                session.Publish(mode switch
                {
                    "running" => session.Snapshot with { Status = UploadSessionStatus.Running },
                    "restarting" => session.Snapshot with { Status = UploadSessionStatus.Restarting },
                    "nonretryable" => session.Snapshot with { RetryExhausted = false },
                    "cleanup" => session.Snapshot with { LastError = new(Epoch.AddSeconds(20), SessionErrorKind.CleanupFailed, "Cleanup failed.") },
                    "before" => session.Snapshot with { LastError = session.Snapshot.LastError! with { Timestamp = Epoch } },
                    "after" => session.Snapshot with { LastError = session.Snapshot.LastError! with { Timestamp = Epoch.AddSeconds(40) } },
                    _ => session.Snapshot with { StopReason = SessionStopReason.UserRequested }
                });
                // The edge itself must safely reconcile errors that appeared between probes.
                await manager.ObserveConnectionAsync(Edge(++sequence, ++outage));
                P.Equal(0, session.Restarts);
            }
        });
        foreach (var action in new[] { "pause", "disable", "remove", "config", "stop", "generation", "exit" })
        {
            await test("Recovery: queued edge cannot override " + action, async () =>
            {
                var folder = P.Folder(); var settings = P.Settings(folder); var factory = new ManagerFactory();
                await using var manager = new UploadManager(settings, factory, connectionGeneration: 1);
                await manager.StartAllAsync(); var session = factory.Sessions[folder.Id]; Fail(session);
                await manager.ObserveConnectionAsync(Outage());
                Task intent = action switch
                {
                    "pause" => manager.PauseAllAsync(),
                    "disable" => manager.ApplySettingsAsync(P.Settings(folder with { Enabled = false })),
                    "remove" => manager.ApplySettingsAsync(P.Settings()),
                    "config" => manager.ApplySettingsAsync(P.Settings(folder with { Concurrency = 3 })),
                    "stop" => manager.StopAllAsync(),
                    "exit" => manager.DisposeAsync().AsTask(),
                    _ => Task.CompletedTask
                };
                Task edge;
                try { edge = manager.ObserveConnectionAsync(action == "generation" ? Edge() with { ConnectionGeneration = 2 } : Edge()); }
                catch (ObjectDisposedException) when (action == "exit") { edge = Task.CompletedTask; }
                await intent; await edge;
                P.Equal(0, session.Restarts);
                if (action == "pause")
                {
                    await manager.ResumeAllAsync(); await manager.ObserveConnectionAsync(Edge(3));
                    P.Equal(0, session.Restarts);
                }
                if (action == "remove")
                {
                    await manager.ApplySettingsAsync(settings);
                    await manager.ObserveConnectionAsync(Edge(3));
                    P.Equal(0, factory.Sessions[folder.Id].Restarts);
                    P.Equal(2, factory.All.Count);
                }
            });
        }
    }
}
