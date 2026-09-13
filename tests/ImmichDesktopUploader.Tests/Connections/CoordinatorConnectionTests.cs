using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Tests.Management;
using ImmichDesktopUploader.Tests.Sessions;
using P = ImmichDesktopUploader.Tests.Management.PersistenceTests;

namespace ImmichDesktopUploader.Tests.Connections;

internal static class CoordinatorConnectionTests
{
    public static async Task RunAllAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Connection coordinator: failed initial probe cannot block sessions; Pause keeps monitor alive", async () =>
        {
            var folder = P.Folder(); var factory = new ManagerFactory(); var probe = new ControlledProbe();
            var clock = new FakeSessionClock(); var created = 0;
            await using var coordinator = new AppCoordinator(new MemorySettings(P.Settings(folder)),
                new MemoryCredentials(new(P.Url, "test-secret")), _ => factory,
                probeFactory: _ => { created++; return probe; }, probeClock: clock);
            await coordinator.StartAsync(); var first = await probe.Next();
            P.Equal(1, factory.Sessions[folder.Id].Starts);
            first.Result.SetResult(false);
            await ConnectionMonitorTests.Until(() => clock.Delays.Any(d => d.Duration.TotalSeconds == 30 && !d.Task.IsCompleted));
            P.Equal(ConnectionStatus.Unavailable, coordinator.Connection.Status);
            P.Equal(UploadSessionStatus.Running, coordinator.Snapshot!.Folders[0].Session.Status);
            await coordinator.StartAsync(); P.Equal(1, created);
            await coordinator.PauseAllAsync(); clock.Advance(TimeSpan.FromSeconds(30));
            (await probe.Next()).Result.SetResult(true);
            await ConnectionMonitorTests.Until(() => coordinator.Connection.Status == ConnectionStatus.Reachable);
            P.Equal(0, factory.Sessions[folder.Id].Restarts);
            P.Check(coordinator.Snapshot!.IsPaused, "Probe changed user pause intent.");
        });
        foreach (var change in new[] { "key", "url" })
        {
            await test("Connection coordinator: " + change + " change retires old generation before new probe", async () =>
            {
                var settings = P.Settings(P.Folder()); var factory = new ManagerFactory();
                var old = new ControlledProbe(true); var replacement = new ControlledProbe(); var clock = new FakeSessionClock();
                var created = 0;
                var coordinator = new AppCoordinator(new MemorySettings(settings), new MemoryCredentials(new(P.Url, "old-secret")),
                    _ => factory, probeFactory: _ => ++created == 1 ? old : replacement, probeClock: clock);
                await coordinator.StartAsync(); var pending = await old.Next();
                try
                {
                    await coordinator.UpdateAsync(settings with { StartWithWindows = true });
                    P.Equal(1, created); P.Equal(1L, coordinator.Connection.ConnectionGeneration);
                    var url = change == "url" ? "https://new.example.invalid/api" : P.Url;
                    await coordinator.UpdateAsync(settings with { ServerUrl = url }, new ImmichConnectionSettings(url, "new-secret"));
                    P.Check(pending.Token.IsCancellationRequested, "Settings did not cancel old probe.");
                    P.Equal(0, replacement.Count);
                    P.Equal(2L, coordinator.Connection.ConnectionGeneration);
                    pending.Result.SetResult(true); var current = await replacement.Next(); current.Result.SetResult(false);
                    await ConnectionMonitorTests.Until(() => coordinator.Connection.Status == ConnectionStatus.Unavailable);
                    P.Check(coordinator.Connection.LastSucceededAt is null, "Old credentials published success.");
                    P.Equal(2, factory.All.Count);
                    P.Check(factory.All[0].Disposed, "Old manager survived replacement.");
                }
                finally { pending.Result.TrySetResult(false); await coordinator.DisposeAsync(); }
            });
        }
        await test("Connection coordinator: shutdown cancels probe and fences late success", async () =>
        {
            var folder = P.Folder(); var factory = new ManagerFactory(); var probe = new ControlledProbe(true);
            var coordinator = new AppCoordinator(new MemorySettings(P.Settings(folder)), new MemoryCredentials(new(P.Url, "secret")),
                _ => factory, probeFactory: _ => probe, probeClock: new FakeSessionClock());
            await coordinator.StartAsync(); var pending = await probe.Next();
            var disposal = coordinator.DisposeAsync().AsTask();
            P.Check(pending.Token.IsCancellationRequested && !disposal.IsCompleted, "Coordinator abandoned probe cleanup.");
            pending.Result.SetResult(true); await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            P.Equal(0, factory.Sessions[folder.Id].Restarts);
            P.Check(factory.Sessions[folder.Id].Disposed && coordinator.Snapshot is null, "Manager cleanup incomplete.");
        });
    }
}
