using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Persistence;
using static ImmichDesktopUploader.Tests.Management.PersistenceTests;

namespace ImmichDesktopUploader.Tests.Management;

internal static class CoordinatorTests
{
    private static ImmichConnectionSettings Connection(string url = Url, string key = "fake-phase4-key") => new(url, key);
    public static async Task RunAllAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Coordinator: invalid settings/credentials cause zero session creation", async () =>
        {
            foreach (var failure in new[] { AppFailure.MissingCredentials, AppFailure.InvalidCredentials, AppFailure.CredentialMismatch })
            {
                var settings = new MemorySettings(Settings(Folder())); var credentials = new MemoryCredentials(Connection()) { LoadFailure = failure };
                var creates = 0; await using var app = new AppCoordinator(settings, credentials, _ => { creates++; return new ManagerFactory(); });
                await Fails(() => app.StartAsync(), failure); PersistenceTests.Equal(0, creates); PersistenceTests.Check(app.Snapshot is null, "Manager created on invalid credentials.");
            }
            var corrupt = new MemorySettings(Settings()) { LoadFailure = AppFailure.CorruptSettings };
            await using var broken = new AppCoordinator(corrupt, new MemoryCredentials(Connection()), _ => throw new Exception("Must not create"));
            await Fails(() => broken.StartAsync(), AppFailure.CorruptSettings);
            var futureCredentials = new MemoryCredentials(Connection());
            await using var future = new AppCoordinator(new MemorySettings(Settings()) { LoadFailure = AppFailure.UnsupportedSchema }, futureCredentials);
            await Fails(() => future.UpdateAsync(Settings(), Connection(key: "replacement")), AppFailure.UnsupportedSchema);
            PersistenceTests.Check(futureCredentials.Value.Matches(Connection()), "Future settings failure changed credentials.");
        });
        await test("Coordinator: normal folder save applies only diff / persistence failure preserves runtime", async () =>
        {
            var a = Folder("A"); var b = Folder("B"); var settings = new MemorySettings(Settings(a, b));
            var credentials = new MemoryCredentials(Connection()); var factory = new ManagerFactory();
            await using var app = new AppCoordinator(settings, credentials, _ => factory); await app.StartAsync();
            await app.UpdateAsync(Settings(a with { AlbumName = "saved" }, b)); PersistenceTests.Equal(1, factory.Sessions[a.Id].Applies); PersistenceTests.Equal(0, factory.Sessions[b.Id].Applies);
            settings.FailSave = true;
            await Fails(() => app.UpdateAsync(Settings(a with { AlbumName = "failed" }, b)), AppFailure.StorageFailure);
            PersistenceTests.Equal("saved", factory.Sessions[a.Id].Configuration.AlbumName); PersistenceTests.Equal(0, factory.Sessions[a.Id].Stops);
            credentials.FailSave = true;
            await Fails(() => app.UpdateAsync(Settings(a, b), Connection(key: "changed")), AppFailure.StorageFailure);
            PersistenceTests.Equal("saved", factory.Sessions[a.Id].Configuration.AlbumName);
        });
        await test("Coordinator: URL/key change disposes old context before new shared factory", async () =>
        {
            var a = Folder("A"); var b = Folder("B"); var settings = new MemorySettings(Settings(a, b));
            var credentials = new MemoryCredentials(Connection()); var contexts = new List<ManagerFactory>();
            await using var app = new AppCoordinator(settings, credentials, _ =>
            {
                PersistenceTests.Check(contexts.All(f => f.All.All(s => s.Disposed)), "New backend created before old cleanup.");
                var factory = new ManagerFactory(); contexts.Add(factory); return factory;
            });
            await app.StartAsync(); PersistenceTests.Equal(1, contexts.Count); PersistenceTests.Equal(2, contexts[0].All.Count);
            var other = Settings(a, b) with { ServerUrl = "https://other.invalid/api" };
            await app.UpdateAsync(other, Connection(other.ServerUrl)); PersistenceTests.Equal(2, contexts.Count);
            await app.UpdateAsync(other, Connection(other.ServerUrl, "new-key")); PersistenceTests.Equal(3, contexts.Count);
            PersistenceTests.Check(contexts[2].All.All(s => s.Starts == 1), "Enabled sessions did not restart.");
            await app.DisposeAsync(); PersistenceTests.Check(contexts.All(f => f.All.All(s => s.Disposed)), "Context leak.");
        });
        await test("Coordinator: credential-first partial save remains detectable; old runtime preserved", async () =>
        {
            var a = Folder(); var settings = new MemorySettings(Settings(a)) { FailSave = true };
            var credentials = new MemoryCredentials(Connection()); var factory = new ManagerFactory();
            await using var app = new AppCoordinator(settings, credentials, _ => factory); await app.StartAsync();
            var other = Settings(a) with { ServerUrl = "https://other.invalid/api" };
            await Fails(() => app.UpdateAsync(other, Connection(other.ServerUrl)), AppFailure.StorageFailure);
            PersistenceTests.Equal(Url, settings.Value.ServerUrl); PersistenceTests.Equal(other.ServerUrl, credentials.Value.ServerUrl);
            PersistenceTests.Equal(UploadSessionStatus.Running, factory.Sessions[a.Id].Snapshot.Status); PersistenceTests.Equal(0, factory.Sessions[a.Id].Stops);
            await using var restarted = new AppCoordinator(settings, credentials, _ => throw new Exception("Unsafe startup"));
            await Fails(() => restarted.StartAsync(), AppFailure.CredentialMismatch);
        });
        await test("Coordinator: pause and Error hold survive connection replacement", async () =>
        {
            var a = Folder("A"); var b = Folder("B"); var settings = new MemorySettings(Settings(a, b));
            var credentials = new MemoryCredentials(Connection()); var contexts = new List<ManagerFactory>();
            await using var app = new AppCoordinator(settings, credentials, _ =>
            { var f = new ManagerFactory(); if (contexts.Count == 0) f.StartErrors.Add(b.Id); contexts.Add(f); return f; });
            await app.StartAsync(); await app.PauseAllAsync();
            await app.UpdateAsync(settings.Value, Connection(key: "rotated"));
            PersistenceTests.Check(app.Snapshot!.IsPaused, "Pause lost."); PersistenceTests.Check(contexts[1].All.All(s => s.Starts == 0), "Started while paused.");
            await app.UpdateAsync(settings.Value, Connection(key: "rotated-again"));
            await app.ResumeAllAsync(); PersistenceTests.Equal(1, contexts[2].Sessions[a.Id].Starts); PersistenceTests.Equal(0, contexts[2].Sessions[b.Id].Starts);
            await app.RestartAsync(b.Id); PersistenceTests.Equal(1, contexts[2].Sessions[b.Id].Restarts);
        });
        await test("Coordinator: shutdown during save prevents later runtime apply", async () =>
        {
            var a = Folder(); var settings = new MemorySettings(Settings(a)) { SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var factory = new ManagerFactory(); var app = new AppCoordinator(settings, new MemoryCredentials(Connection()), _ => factory);
            await app.StartAsync(); var update = app.UpdateAsync(Settings(a with { AlbumName = "late" }));
            await settings.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var disposal = app.DisposeAsync().AsTask();
            await UploadManagerTests.Disposed(() => app.StartAsync()); settings.SaveGate.TrySetResult();
            await UploadManagerTests.Disposed(() => update); await disposal;
            PersistenceTests.Equal(0, factory.Sessions[a.Id].Applies); PersistenceTests.Check(factory.Sessions[a.Id].Disposed, "Shutdown leak.");
        });
        await test("Coordinator: real filesystem+DPAPI startup / mismatch gates fake runtime", async () =>
        {
            using var f = new StorageFixture(); var settings = new SettingsService(f.Paths); var credentials = new CredentialService(f.Paths);
            var a = Folder(); await settings.SaveAsync(Settings(a)); await credentials.SaveAsync(Connection());
            var factory = new ManagerFactory(); await using var app = new AppCoordinator(settings, credentials, _ => factory);
            await app.StartAsync(); PersistenceTests.Equal(1, factory.Sessions[a.Id].Starts);
            await app.UpdateAsync(Settings(a with { Concurrency = 3 })); PersistenceTests.Equal(3, factory.Sessions[a.Id].Configuration.Concurrency);
            await credentials.SaveAsync(Connection("https://other.invalid/api"));
            await using var invalid = new AppCoordinator(settings, credentials, _ => throw new Exception("Should not start"));
            await Fails(() => invalid.StartAsync(), AppFailure.CredentialMismatch);
        });
        await test("Coordinator: cleanup failure forbids new connection context", async () =>
        {
            var a = Folder(); var settings = new MemorySettings(Settings(a)); var factory = new ManagerFactory(); var contexts = 0;
            var app = new AppCoordinator(settings, new MemoryCredentials(Connection()), _ => { contexts++; return factory; });
            await app.StartAsync(); factory.Sessions[a.Id].FailDispose = true;
            await Fails(() => app.UpdateAsync(settings.Value, Connection(key: "rotated")), AppFailure.CleanupFailed);
            PersistenceTests.Equal(1, contexts);
            await Fails(() => app.DisposeAsync().AsTask(), AppFailure.CleanupFailed);
        });
    }
}
