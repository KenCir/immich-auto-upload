using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Tests.Sessions;
using static ImmichDesktopUploader.Tests.Management.PersistenceTests;

namespace ImmichDesktopUploader.Tests.Management;

internal static class UploadManagerTests
{
    public static async Task RunAllAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Manager: zero / enabled / disabled / multiple / isolated Error", async () =>
        {
            var factory = new ManagerFactory(); await using var empty = new UploadManager(Settings(), factory);
            await empty.StartAllAsync(); PersistenceTests.Equal(0, empty.Snapshot.Folders.Length);
            var a = Folder("A"); var b = Folder("B") with { Enabled = false }; var c = Folder("C");
            factory.StartErrors.Add(c.Id);
            await empty.ApplySettingsAsync(Settings(a, b, c));
            PersistenceTests.Equal(UploadSessionStatus.Running, factory.Sessions[a.Id].Snapshot.Status);
            PersistenceTests.Equal(0, factory.Sessions[b.Id].Starts);
            PersistenceTests.Equal(UploadSessionStatus.Error, factory.Sessions[c.Id].Snapshot.Status);
            PersistenceTests.Equal(3, empty.Snapshot.Folders.Length);
        });
        await test("Manager: no-op / one-folder diff / identity / disabled edits defer start", async () =>
        {
            var a = Folder("A"); var b = Folder("B"); var factory = new ManagerFactory();
            await using var manager = new UploadManager(Settings(a, b), factory); await manager.StartAllAsync();
            await manager.ApplySettingsAsync(Settings(a with { IgnorePatterns = [.. a.IgnorePatterns] }, b));
            PersistenceTests.Equal(0, factory.Sessions[a.Id].Applies); PersistenceTests.Equal(0, factory.Sessions[b.Id].Applies);
            var original = factory.Sessions[a.Id];
            a = a with { Path = Folder("Moved").Path, AlbumName = "Edited", IgnorePatterns = ["*.tmp"] };
            await manager.ApplySettingsAsync(Settings(a, b)); PersistenceTests.Equal(1, original.Applies); PersistenceTests.Equal(0, factory.Sessions[b.Id].Applies);
            PersistenceTests.Check(ReferenceEquals(original, factory.Sessions[a.Id]), "Path change replaced identity.");
            a = a with { Enabled = false }; await manager.ApplySettingsAsync(Settings(a, b));
            PersistenceTests.Equal(SessionStopReason.Disabled, original.Snapshot.StopReason);
            a = a with { AlbumName = "While disabled" }; await manager.ApplySettingsAsync(Settings(a, b));
            PersistenceTests.Equal(1, original.Applies); PersistenceTests.Equal(UploadSessionStatus.Stopped, original.Snapshot.Status);
            a = a with { Enabled = true }; await manager.ApplySettingsAsync(Settings(a, b));
            PersistenceTests.Equal(2, original.Applies); PersistenceTests.Equal("While disabled", original.Configuration.AlbumName);
            PersistenceTests.Equal(1, factory.Sessions[b.Id].Starts);
        });
        await test("Manager: add disabled / remove / disposal before same-ID reuse", async () =>
        {
            var folder = Folder(); var factory = new ManagerFactory();
            await using var manager = new UploadManager(Settings(), factory); await manager.StartAllAsync();
            await manager.ApplySettingsAsync(Settings(folder with { Enabled = false })); var first = factory.Sessions[folder.Id];
            PersistenceTests.Equal(0, first.Starts);
            await manager.ApplySettingsAsync(Settings()); PersistenceTests.Check(first.Disposed, "Removed session not disposed.");
            PersistenceTests.Equal(SessionStopReason.Removed, first.Snapshot.StopReason); PersistenceTests.Equal(0, manager.Snapshot.Folders.Length);
            await manager.ApplySettingsAsync(Settings(folder)); PersistenceTests.Equal(1, factory.Sessions[folder.Id].Starts);
        });
        await test("Manager: Pause/Resume keeps Enabled and preexisting Error / manual Restart", async () =>
        {
            var a = Folder("A"); var b = Folder("B"); var factory = new ManagerFactory(); factory.StartErrors.Add(b.Id);
            await using var manager = new UploadManager(Settings(a, b), factory); await manager.StartAllAsync();
            await manager.PauseAllAsync(); await manager.PauseAllAsync();
            PersistenceTests.Check(manager.Snapshot.IsPaused && manager.Snapshot.Folders.All(f => f.Folder.Enabled), "Pause changed settings.");
            PersistenceTests.Equal(SessionStopReason.Paused, factory.Sessions[a.Id].Snapshot.StopReason);
            PersistenceTests.Equal(UploadSessionStatus.Error, factory.Sessions[b.Id].Snapshot.Status);
            a = a with { AlbumName = "Paused edit" }; await manager.ApplySettingsAsync(Settings(a, b));
            PersistenceTests.Equal(0, factory.Sessions[a.Id].Applies);
            await manager.ResumeAllAsync(); PersistenceTests.Equal(1, factory.Sessions[a.Id].Applies);
            PersistenceTests.Equal(1, factory.Sessions[b.Id].Starts); PersistenceTests.Equal(UploadSessionStatus.Error, factory.Sessions[b.Id].Snapshot.Status);
            await manager.RestartAsync(b.Id); PersistenceTests.Equal(1, factory.Sessions[b.Id].Restarts);
            await manager.PauseAllAsync(); await manager.ResumeAllAsync(); PersistenceTests.Equal(2, factory.Sessions[b.Id].Starts);
        });
        await test("Manager: StopAll aggregates failures / Dispose observes every session", async () =>
        {
            var a = Folder("A"); var b = Folder("B"); var factory = new ManagerFactory();
            var manager = new UploadManager(Settings(a, b), factory); await manager.StartAllAsync();
            factory.Sessions[a.Id].FailStop = true;
            var result = await manager.StopAllAsync(); PersistenceTests.Check(!result.Success && result.FailedFolderIds.SequenceEqual([a.Id]), "Cleanup failure hidden.");
            PersistenceTests.Equal(UploadSessionStatus.Stopped, factory.Sessions[b.Id].Snapshot.Status);
            factory.Sessions[b.Id].FailDispose = true;
            await Fails(() => manager.DisposeAsync().AsTask(), AppFailure.CleanupFailed);
            PersistenceTests.Check(factory.All.All(s => s.Disposed), "Dispose abandoned a session."); PersistenceTests.Equal(0, manager.Snapshot.Folders.Length);
            await Disposed(() => manager.StartAllAsync());
        });
        await test("Manager: concurrent Apply serialized while removal waits / shutdown fence", async () =>
        {
            var a = Folder("A"); var b = Folder("B"); var factory = new ManagerFactory();
            var manager = new UploadManager(Settings(a), factory); await manager.StartAllAsync();
            var first = factory.Sessions[a.Id]; first.StopGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var removing = manager.ApplySettingsAsync(Settings()); await first.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var adding = manager.ApplySettingsAsync(Settings(a, b));
            PersistenceTests.Check(!adding.IsCompleted, "Concurrent Apply bypassed cleanup.");
            var dispose = manager.DisposeAsync().AsTask(); await Disposed(() => manager.StartAllAsync());
            first.StopGate.TrySetResult(); await removing; await Disposed(() => adding); await dispose;
            PersistenceTests.Equal(1, factory.All.Count); PersistenceTests.Check(first.Disposed, "Shutdown leaked removed session.");
        });
        await test("Manager: concurrent successful Apply order / stopped manager edits", async () =>
        {
            var a = Folder(); var factory = new ManagerFactory(); await using var manager = new UploadManager(Settings(a), factory);
            await manager.StartAllAsync();
            var first = manager.ApplySettingsAsync(Settings(a with { AlbumName = "one" }));
            var second = manager.ApplySettingsAsync(Settings(a with { AlbumName = "two" }));
            await Task.WhenAll(first, second); PersistenceTests.Equal("two", factory.Sessions[a.Id].Configuration.AlbumName);
            PersistenceTests.Check((await manager.StopAllAsync()).Success, "Stop failed.");
            await manager.ApplySettingsAsync(Settings(a with { AlbumName = "three" }));
            PersistenceTests.Equal(UploadSessionStatus.Stopped, factory.Sessions[a.Id].Snapshot.Status);
            await manager.StartAllAsync(); PersistenceTests.Equal("three", factory.Sessions[a.Id].Configuration.AlbumName);
        });
        await test("Manager: failed removal quarantines identity", async () =>
        {
            var a = Folder(); var factory = new ManagerFactory(); var manager = new UploadManager(Settings(a), factory);
            await manager.StartAllAsync(); factory.Sessions[a.Id].FailStop = true;
            await Fails(() => manager.ApplySettingsAsync(Settings()), AppFailure.CleanupFailed);
            PersistenceTests.Equal(1, manager.Snapshot.Folders.Length); PersistenceTests.Equal(1, factory.All.Count);
            await Fails(() => manager.DisposeAsync().AsTask(), AppFailure.CleanupFailed);
        });
        await test("Manager: production Session removal during pending Start cleans late run", async () =>
        {
            var backend = new FakeUploadBackend(); var factory = new RealSessionFactory(backend, new FakeSessionClock());
            var a = Folder(); var manager = new UploadManager(Settings(a), factory);
            try
            {
                await manager.StartAllAsync(); var start = await backend.NextStartAsync();
                var remove = manager.ApplySettingsAsync(Settings());
                await Until(() => start.Token.IsCancellationRequested);
                PersistenceTests.Check(!remove.IsCompleted, "Removal completed before pending ownership resolved.");
                start.Succeed(); await remove.WaitAsync(TimeSpan.FromSeconds(5)); PersistenceTests.Equal(0, backend.ActiveCount);
                await manager.DisposeAsync(); PersistenceTests.Equal(0, manager.Snapshot.Folders.Length);
            }
            finally { backend.ReleaseAll(); await manager.DisposeAsync(); }
        });
        await test("Manager: production Session Apply during retry cancels old generation", async () =>
        {
            var backend = new FakeUploadBackend(); var clock = new FakeSessionClock(); var factory = new RealSessionFactory(backend, clock);
            var a = Folder(); var manager = new UploadManager(Settings(a), factory);
            try
            {
                await manager.StartAllAsync(); (await backend.NextStartAsync()).Fail(BackendFailureKind.Retryable);
                await Until(() => factory.Session!.Snapshot.Status == UploadSessionStatus.Restarting);
                await manager.ApplySettingsAsync(Settings(a with { AlbumName = "retry edit" }));
                var changed = await backend.NextStartAsync(); PersistenceTests.Equal("retry edit", changed.Request.Configuration.AlbumName); changed.Succeed();
                await Until(() => factory.Session!.Snapshot.Status == UploadSessionStatus.Running);
                await factory.Session!.SynchronizeAsync(); clock.Advance(TimeSpan.FromSeconds(10)); await factory.Session.SynchronizeAsync();
                PersistenceTests.Equal(2, backend.StartCount); await manager.DisposeAsync(); PersistenceTests.Equal(0, backend.ActiveCount);
            }
            finally { backend.ReleaseAll(); await manager.DisposeAsync(); }
        });
    }
    internal static async Task Disposed(Func<Task> action)
    { try { await action(); } catch (ObjectDisposedException) { return; } throw new Exception("Expected disposal rejection."); }
    internal static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(1, timeout.Token); }
    private sealed class RealSessionFactory(IUploadBackend backend, ISessionClock clock) : IUploadSessionFactory
    {
        public UploadSession? Session;
        public IManagedUploadSession Create(UploadSessionConfiguration configuration) => Session = new(configuration, backend, clock);
    }
}
