using System.Diagnostics;
using ImmichDesktopUploader.Infrastructure.Immich;
using P = ImmichDesktopUploader.Tests.Management.PersistenceTests;
using Microsoft.Win32;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Persistence;
using ImmichDesktopUploader.Infrastructure.Windows;
using ImmichDesktopUploader.Tests.Management;
using ImmichDesktopUploader.Tests.Gui;
using ImmichDesktopUploader.Tests.Sessions;
using ImmichDesktopUploader.ViewModels;
using static ImmichDesktopUploader.Tests.Management.PersistenceTests;

namespace ImmichDesktopUploader.Tests.Residency;

internal static class ResidentTests
{
    public static async Task RunAllAsync(string host, Func<string, Func<Task>, Task> test)
    {
        await test("Resident: Exit during blocked folder removal fences another Session retry immediately", async () =>
        {
            using var storage = new StorageFixture(); var a = Folder("A"); var b = Folder("B");
            await new SettingsService(storage.Paths).SaveAsync(Settings(a, b));
            await new CredentialService(storage.Paths).SaveAsync(new(Url, "resident-race-only"));
            var backend = new FakeUploadBackend(); var clock = new FakeSessionClock(); var factory = new SessionFactory(backend, clock);
            var service = new DesktopApplicationService(storage.Paths, _ => factory);
            FakeProcessRun? first = null;
            try
            {
                await service.InitializeAsync();
                var call1 = await backend.NextStartAsync(); var call2 = await backend.NextStartAsync();
                // Independent session actors need not reach the backend in folder order.
                first = (call1.Request.Configuration.FolderId == a.Id ? call1 : call2).Succeed();
                var second = (call1.Request.Configuration.FolderId == b.Id ? call1 : call2).Succeed();
                await Until(() => factory.Sessions.Values.All(s => s.Snapshot.Status == UploadSessionStatus.Running));
                first.HoldStop = true; var save = service.SaveAsync(Settings(b));
                await first.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                second.Exit(17); await Until(() => clock.Delays.Any(d => d.Duration == TimeSpan.FromSeconds(2)));
                var lifetime = new ResidentLifetime(new WindowFake(), new TrayFake(), () => service.DisposeAsync().AsTask(), service.PauseAsync);
                var exit = lifetime.ExitAsync(); clock.Advance(TimeSpan.FromSeconds(2));
                await factory.Sessions[b.Id].SynchronizeAsync();
                P.Equal(2, backend.StartCount);
                first.CompleteStop();
                try { await save; } catch (ObjectDisposedException) { }
                await exit.WaitAsync(TimeSpan.FromSeconds(10)); P.Equal(0, backend.ActiveCount);
            }
            finally { first?.CompleteStop(); backend.ReleaseAll(); clock.ReleaseAll(); await service.DisposeAsync(); }
        });
        await test("Resident: Exit during real Session Starting/retry/Restart/Pause leaves no new generation", async () =>
        {
            foreach (var phase in new[] { "Starting", "retry", "Restart", "Pause" })
            {
                using var storage = new StorageFixture(); var folder = Folder();
                await new SettingsService(storage.Paths).SaveAsync(Settings(folder));
                await new CredentialService(storage.Paths).SaveAsync(new(Url, "resident-race-only"));
                var backend = new FakeUploadBackend(); var clock = new FakeSessionClock(); var factory = new SessionFactory(backend, clock);
                var service = new DesktopApplicationService(storage.Paths, _ => factory);
                var tray = new TrayFake(); var window = new WindowFake();
                var lifetime = new ResidentLifetime(window, tray, () => service.DisposeAsync().AsTask(), service.PauseAsync);
                try
                {
                    await service.InitializeAsync(); var pending = await backend.NextStartAsync();
                    FakeProcessRun? run = null; Task? pause = null;
                    if (phase != "Starting")
                    {
                        run = pending.Succeed(); await Until(() => factory.Session!.Snapshot.Status == UploadSessionStatus.Running);
                        if (phase == "retry")
                        { run.Exit(17); await Until(() => clock.Delays.Any(d => d.Duration == TimeSpan.FromSeconds(2))); }
                        else
                        {
                            run.HoldStop = true;
                            if (phase == "Restart") await service.RestartAsync(folder.Id);
                            else pause = service.PauseAsync();
                            await run.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        }
                    }
                    var exit = lifetime.ExitAsync();
                    await UploadManagerTests.Disposed(service.ResumeAsync);
                    if (phase == "Starting")
                    {
                        await Until(() => pending.Token.IsCancellationRequested);
                        pending.Succeed(); // Late success must be adopted and cleaned.
                    }
                    else if (phase != "retry") run!.CompleteStop();
                    if (pause is not null) { try { await pause; } catch (ObjectDisposedException) { } }
                    await exit.WaitAsync(TimeSpan.FromSeconds(10));
                    clock.Advance(TimeSpan.FromHours(1));
                    P.Equal(1, backend.StartCount); P.Equal(0, backend.ActiveCount);
                    P.Check(window.Exited && tray.Disposed, "Exit not completed.");
                }
                finally { backend.ReleaseAll(); clock.ReleaseAll(); await service.DisposeAsync(); }
            }
        });
        await test("Resident: Windows session ending enters the same asynchronous cleanup", async () =>
        {
            var gate = new TaskCompletionSource(); var tray = new TrayFake(); var window = new WindowFake();
            var lifetime = new ResidentLifetime(window, tray, () => gate.Task, () => Task.CompletedTask);
            tray.Raise(TrayAction.SessionEnding);
            P.Check(window.Disabled && !window.Exited, "Session ending blocked or bypassed cleanup.");
            gate.SetResult(); await lifetime.ExitAsync(); P.Check(window.Exited, "Session ending cleanup lost.");
        });
        await test("Resident: normal/background/attention/failure startup and close never dispose", async () =>
        {
            foreach (var background in new[] { false, true })
            foreach (var attention in new[] { false, true })
            foreach (var available in new[] { false, true })
            {
                var window = new WindowFake(); var tray = new TrayFake { Available = available }; var disposals = 0;
                var lifetime = new ResidentLifetime(window, tray, () => { disposals++; return Task.CompletedTask; }, () => Task.CompletedTask);
                lifetime.Initialize(background, attention);
                P.Equal(!background || attention || !available, window.Visible);
                lifetime.CloseRequested(); P.Equal(!available, window.Visible); P.Equal(0, disposals);
                tray.Raise(TrayAction.Open); P.Check(window.Visible, "Open failed.");
                await lifetime.ExitAsync(); P.Equal(1, disposals); P.Check(tray.Disposed && window.Exited, "Exit incomplete.");
            }
        });
        await test("Resident: repeated hidden Exit fences actions and awaits cleanup before tray removal", async () =>
        {
            var window = new WindowFake(); var tray = new TrayFake(); var gate = new TaskCompletionSource(); var calls = 0;
            var lifetime = new ResidentLifetime(window, tray, () => { calls++; return gate.Task; }, () => throw new Exception("Pause after fence"));
            lifetime.Initialize(true, false); lifetime.Update(false, true);
            var exit = lifetime.ExitAsync(); P.Check(ReferenceEquals(exit, lifetime.ExitAsync()), "Exit task not shared.");
            P.Check(window.Disabled && tray.Exiting && !tray.Disposed && !window.Exited, "Ordering violated.");
            tray.Raise(TrayAction.Open); tray.Raise(TrayAction.PauseResume); lifetime.CloseRequested();
            P.Check(!window.Visible, "Activation during shutdown reopened window."); P.Equal(1, calls);
            gate.SetResult(); await exit; P.Check(tray.Disposed && window.Exited, "Cleanup completion lost.");
        });
        await test("Resident: tray recovery failure surfaces window; cleanup failure never claims exit", async () =>
        {
            var window = new WindowFake(); var tray = new TrayFake();
            var lifetime = new ResidentLifetime(window, tray, () => Task.FromException(new AppOperationException(AppFailure.CleanupFailed)), () => Task.CompletedTask);
            lifetime.Initialize(true, false); tray.Raise(TrayAction.Unavailable);
            P.Check(window.Visible && window.Errors > 0, "Lost tray stranded hidden window.");
            await Fails(() => lifetime.ExitAsync(), AppFailure.CleanupFailed);
            P.Check(!window.Exited && !tray.Disposed && window.Disabled, "Unconfirmed cleanup exited."); tray.Dispose();
        });
        await test("Resident: tray Pause/Resume share application state with GUI", async () =>
        {
            var app = new FakeDesktop(); app.Publish(new(1, Settings(), true, false, null, new(false, true, [], [])));
            var queue = new QueuedDispatcher(); var model = new MainViewModel(app, queue, new FakeDialogs());
            var tray = new TrayFake(); var window = new WindowFake();
            var lifetime = new ResidentLifetime(window, tray, () => model.DisposeAsync().AsTask(), () => model.PauseResumeCommand.ExecuteAsync());
            model.PropertyChanged += (_, _) => lifetime.Update(model.IsPaused, model.PauseResumeCommand.CanExecute(null));
            lifetime.Initialize(false, false); lifetime.Update(false, true);
            tray.Raise(TrayAction.PauseResume); await model.PauseResumeCommand.Execution; queue.Drain();
            P.Equal(1, app.Pauses); P.Equal("Resume All", model.PauseLabel); P.Check(tray.Paused, "Tray label stale.");
            lifetime.Update(true, true); tray.Raise(TrayAction.PauseResume); await model.PauseResumeCommand.Execution; queue.Drain();
            P.Equal(1, app.Resumes); P.Check(!tray.Paused, "Tray label not resumed."); await lifetime.ExitAsync();
        });
        await test("Startup: exact quoted current path, stale detection, safe failures", () =>
        {
            var store = new RegistryFake(); var service = new StartupService(@"C:\Apps\写真 Uploader\app.exe", store);
            P.Equal(@"""C:\Apps\写真 Uploader\app.exe"" --background", service.Command);
            P.Check(service.Inspect().Matches(false), "Initial registration.");
            service.Register(); P.Check(service.Inspect().IsRegistered, "Registration not found.");
            store.Value = @"""C:\old\app.exe"" --background";
            P.Equal(StartupRegistrationState.DifferentCommand, service.Inspect().State);
            service.Register(); P.Equal(service.Command, store.Value); service.Unregister(); P.Check(service.Inspect().Matches(false), "Unregister failed.");
            store.Fail = true; P.Equal(StartupRegistrationState.Unavailable, service.Inspect().State);
            P.Throws(service.Register, AppFailure.StartupFailure); P.Throws(service.Unregister, AppFailure.StartupFailure);
            P.Throws(() => StartupService.BuildCommand("relative.exe"), AppFailure.StartupFailure);
            return Task.CompletedTask;
        });
        await test("Startup: actual HKCU disposable branch registration and cleanup", () =>
        {
            if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
            var key = @"Software\ImmichDesktopUploader.Tests\" + Guid.NewGuid().ToString("N");
            try
            {
                var service = new StartupService(host, new HkcuStartupStore(key));
                service.Register(); P.Check(service.Inspect().IsRegistered, "HKCU write failed.");
                using (var actual = Registry.CurrentUser.OpenSubKey(key)) P.Equal(service.Command, actual!.GetValue("ImmichDesktopUploader"));
                service.Unregister(); P.Check(service.Inspect().Matches(false), "HKCU delete failed.");
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
            using var absent = Registry.CurrentUser.OpenSubKey(key); P.Check(absent is null, "Test key leaked.");
            return Task.CompletedTask;
        });
        await test("Startup settings: no automatic repair / false-true-false / failure draft / retry", async () =>
        {
            using var storage = new StorageFixture(); var factory = new ManagerFactory();
            await new SettingsService(storage.Paths).SaveAsync(Settings());
            await new CredentialService(storage.Paths).SaveAsync(new(Url, "startup-test-only"));
            var store = new RegistryFake { Value = "stale" }; var startup = new StartupService(host, store);
            await using var service = new DesktopApplicationService(storage.Paths, _ => factory, startup: startup);
            await service.InitializeAsync(); P.Equal("stale", store.Value);
            P.Check(!service.Snapshot.Startup!.Matches(false), "Mismatch hidden.");
            await service.SaveAsync(Settings()); P.Equal("stale", store.Value); // Folder saves must not repair registration.
            using var draft = new SettingsViewModel(Settings(), true, service.SaveSettingsAsync, service.Snapshot.Startup,
                () => (service.Snapshot.Startup, service.Snapshot.Settings!.StartWithWindows));
            draft.StartWithWindows = true; store.Fail = true;
            await draft.SaveCommand.ExecuteAsync(); P.Check(!draft.Saved && draft.StartWithWindows && draft.Error.Length > 0, "Draft lost on registry failure.");
            P.Check(!(await new SettingsService(storage.Paths).LoadAsync()).Settings!.StartWithWindows, "Settings saved despite registry failure.");
            store.Fail = false; await draft.SaveCommand.ExecuteAsync();
            P.Check(draft.Saved && startup.Inspect().IsRegistered && service.Snapshot.Settings!.StartWithWindows, "Save retry failed.");
            await service.SaveSettingsAsync(Settings()); P.Check(startup.Inspect().Matches(false), "false did not unregister.");
        });
        await test("Startup settings: post-registry save failure exposes mismatch without changing runtime", async () =>
        {
            using var storage = new StorageFixture(); var factory = new ManagerFactory(); var folder = Folder();
            await new SettingsService(storage.Paths).SaveAsync(Settings(folder));
            await new CredentialService(storage.Paths).SaveAsync(new(Url, "startup-test-only"));
            var store = new RegistryFake(); var startup = new StartupService(host, store);
            await using var service = new DesktopApplicationService(storage.Paths, _ => factory, startup: startup);
            await service.InitializeAsync();
            // Fail specifically at the save boundary, after preflight reads and registry change.
            FileStream? fileLock = null;
            store.OnWrite = () => fileLock = new FileStream(storage.Paths.SettingsPath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try { await Fails(() => service.SaveSettingsAsync(Settings(folder) with { StartWithWindows = true }), AppFailure.StorageFailure); }
            finally { fileLock?.Dispose(); store.OnWrite = null; }
            P.Check(startup.Inspect().IsRegistered && !service.Snapshot.Settings!.StartWithWindows, "Partial state not retained/detected.");
            P.Check(!service.Snapshot.Startup!.Matches(false), "Mismatch absent from immediate failure snapshot.");
            P.Equal(0, factory.Sessions[folder.Id].Applies); P.Equal(0, factory.Sessions[folder.Id].Stops);
            await service.SaveSettingsAsync(Settings(folder) with { StartWithWindows = true });
            P.Check(service.Snapshot.Startup!.Matches(true), "Retry did not reconcile.");
        });
        await test("Resident shutdown: disposal fences manager while accepted save is still pending", async () =>
        {
            var folder = Folder(); var settings = new MemorySettings(Settings(folder)) { SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var factory = new ManagerFactory(); var app = new AppCoordinator(settings, new MemoryCredentials(new(Url, "fake")), _ => factory);
            await app.StartAsync(); var update = app.UpdateAsync(Settings(folder with { AlbumName = "saved-after-exit" }));
            await settings.SaveEntered.Task; var tray = new TrayFake(); var window = new WindowFake();
            var lifetime = new ResidentLifetime(window, tray, () => app.DisposeAsync().AsTask(), () => app.PauseAllAsync());
            var exit = lifetime.ExitAsync(); await factory.Sessions[folder.Id].StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            P.Check(!exit.IsCompleted && !tray.Disposed, "Exit did not wait for persistence.");
            settings.SaveGate.SetResult(); await UploadManagerTests.Disposed(() => update); await exit;
            P.Equal("saved-after-exit", settings.Value.Folders[0].AlbumName); P.Equal(0, factory.Sessions[folder.Id].Applies);
            P.Check(factory.All.All(s => s.Disposed), "Sessions leaked.");
        });
        await test("Resident: close keeps actual Job tree alive; hidden Exit cleans all descendants", async () =>
        {
            using var storage = new StorageFixture(); var folder = UploadFolderSettings.Create(storage.Paths.DirectoryPath);
            await new SettingsService(storage.Paths).SaveAsync(Settings(folder));
            await new CredentialService(storage.Paths).SaveAsync(new(Url, "resident-test-only"));
            var backend = new NativeBackend(host);
            await using var service = new DesktopApplicationService(storage.Paths, _ => new NativeFactory(backend));
            var tray = new TrayFake(); var window = new WindowFake();
            var lifetime = new ResidentLifetime(window, tray, () => service.DisposeAsync().AsTask(), () => service.PauseAsync());
            try
            {
                await service.InitializeAsync(); await Until(() => backend.Run?.GetActiveProcessIds().Count >= 2);
                var pids = backend.Run!.GetActiveProcessIds().ToArray();
                lifetime.Initialize(false, false); lifetime.CloseRequested();
                P.Check(!window.Visible && backend.Run.GetActiveProcessIds().Count >= 2, "Close killed CLI.");
                await lifetime.ExitAsync();
                foreach (var pid in pids)
                { try { using var p = Process.GetProcessById(pid); P.Check(p.HasExited, "Resident exit left descendant."); } catch (ArgumentException) { } }
            }
            finally { await service.DisposeAsync(); }
        });
    }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private sealed class RegistryFake : IStartupRegistryStore
    {
        public string? Value; public bool Fail; public Action? OnWrite;
        public string? Read() { if (Fail) throw new UnauthorizedAccessException(); return Value; }
        public void Write(string command) { if (Fail) throw new UnauthorizedAccessException(); Value = command; OnWrite?.Invoke(); }
        public void Delete() { if (Fail) throw new UnauthorizedAccessException(); Value = null; }
    }
    private sealed class TrayFake : ITrayService
    {
        public bool Available = true, Disposed, Exiting, Paused;
        public event Action<TrayAction>? Invoked;
        public void Raise(TrayAction action) => Invoked?.Invoke(action);
        public bool EnsureIcon() => Available;
        public void Update(bool paused, bool canPause, bool exiting) { Paused = paused; Exiting = exiting; }
        public void Dispose() => Disposed = true;
    }
    private sealed class WindowFake : IResidentWindow
    {
        public bool Visible, Disabled, Exited; public int Errors;
        public void ShowAndActivate() => Visible = true;
        public void Hide() => Visible = false;
        public void DisableInteraction() => Disabled = true;
        public void ReportLifetimeError(string message) => Errors++;
        public void FinishExit() => Exited = true;
    }
    private sealed class NativeFactory(IUploadBackend backend) : IUploadSessionFactory
    { public IManagedUploadSession Create(UploadSessionConfiguration c) => new UploadSession(c, backend); }
    private sealed class SessionFactory(IUploadBackend backend, ISessionClock clock) : IUploadSessionFactory
    {
        public UploadSession? Session;
        public Dictionary<Guid, UploadSession> Sessions { get; } = [];
        public IManagedUploadSession Create(UploadSessionConfiguration c)
        { Session = new UploadSession(c, backend, clock); Sessions.Add(c.FolderId, Session); return Session; }
    }
    private sealed class NativeBackend(string host) : IUploadBackend
    {
        public WindowsProcessRun? Run;
        public async Task<IProcessRun> StartAsync(UploadRunRequest request, CancellationToken token)
        {
            var run = (WindowsProcessRun)await new WindowsProcessRunner().StartAsync(LauncherCommandBuilder.Build(host, ["tree", "1"]), token);
            Volatile.Write(ref Run, run); return run;
        }
    }
}
