using System.Diagnostics;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Persistence;
using ImmichDesktopUploader.Infrastructure.Windows;
using ImmichDesktopUploader.ViewModels;
using P = ImmichDesktopUploader.Tests.Management.PersistenceTests;

namespace ImmichDesktopUploader.Tests.Gui;

internal static class DesktopServiceTests
{
    public static async Task RunAllAsync(string host, Func<string, Func<Task>, Task> test)
    {
        await test("GUI service: first-run setup / DPAPI / folder save failure / mismatch / recovery", async () =>
        {
            using var storage = new P.StorageFixture(); var factory = new Management.ManagerFactory();
            await using var service = new DesktopApplicationService(storage.Paths, _ => factory);
            await service.InitializeAsync(); P.Equal(AppFailure.MissingSettings, service.Snapshot.Failure);
            await service.SaveAsync(P.Settings(), new(P.Url, "ui-service-fake-key"));
            P.Check(service.Snapshot.CredentialsConfigured && service.Snapshot.Manager is not null, "First-run setup not started.");
            var folder = P.Folder(); await service.SaveAsync(P.Settings(folder)); P.Equal(1, factory.Sessions[folder.Id].Starts);
            using (var fileLock = new FileStream(storage.Paths.SettingsPath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await P.Fails(() => service.SaveAsync(P.Settings(folder with { Enabled = false })), AppFailure.StorageFailure);
            P.Check(service.Snapshot.Settings!.Folders[0].Enabled && factory.Sessions[folder.Id].Snapshot.Status == UploadSessionStatus.Running, "Save failure changed runtime.");
            await service.SaveAsync(P.Settings(folder with { Enabled = false })); P.Equal(UploadSessionStatus.Stopped, factory.Sessions[folder.Id].Snapshot.Status);
            await service.DisposeAsync(); P.Check(factory.All.All(s => s.Disposed), "Service retained sessions.");
            await File.WriteAllTextAsync(storage.Paths.SettingsPath, "{broken");
            await using var recovery = new DesktopApplicationService(storage.Paths, _ => new Management.ManagerFactory());
            await recovery.InitializeAsync(); P.Check(recovery.Snapshot.CanRestoreBackup, "Backup not exposed.");
            await recovery.RestoreBackupAsync(); P.Check(recovery.Snapshot.CredentialsConfigured, "Recovery did not reinitialize.");
        });
        await test("GUI lifetime: ViewModel -> coordinator disposal stops actual Job tree", async () =>
        {
            using var storage = new P.StorageFixture(); Directory.CreateDirectory(storage.Paths.DirectoryPath);
            var folder = UploadFolderSettings.Create(storage.Paths.DirectoryPath);
            await new SettingsService(storage.Paths).SaveAsync(P.Settings(folder));
            await new CredentialService(storage.Paths).SaveAsync(new(P.Url, "lifetime-only-fake-key"));
            var backend = new NativeBackend(host);
            var service = new DesktopApplicationService(storage.Paths, _ => new NativeFactory(backend));
            var viewModel = new MainViewModel(service, new QueuedDispatcher(), new FakeDialogs());
            try
            {
                await viewModel.InitializeAsync();
                await Until(() => backend.Run is not null && ((WindowsProcessRun)backend.Run).GetActiveProcessIds().Count >= 2);
                var pids = ((WindowsProcessRun)backend.Run!).GetActiveProcessIds().ToArray();
                await viewModel.DisposeAsync();
                foreach (var pid in pids)
                {
                    try { using var process = Process.GetProcessById(pid); P.Check(process.HasExited, "GUI ownership left a child alive."); }
                    catch (ArgumentException) { }
                }
            }
            finally { await viewModel.DisposeAsync(); }
        });
        await test("GUI service: immediate disposal fences initialization and observes polling", async () =>
        {
            using var storage = new P.StorageFixture(); var service = new DesktopApplicationService(storage.Paths);
            var initialization = service.InitializeAsync(); var disposal = service.DisposeAsync().AsTask();
            try { await initialization; } catch (ObjectDisposedException) { }
            await disposal.WaitAsync(TimeSpan.FromSeconds(5)); await service.DisposeAsync();
            await Management.UploadManagerTests.Disposed(() => service.InitializeAsync());
        });
    }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private sealed class NativeFactory(IUploadBackend backend) : IUploadSessionFactory
    { public IManagedUploadSession Create(UploadSessionConfiguration configuration) => new UploadSession(configuration, backend); }
    private sealed class NativeBackend(string host) : IUploadBackend
    {
        public IProcessRun? Run;
        public async Task<IProcessRun> StartAsync(UploadRunRequest request, CancellationToken cancellationToken)
        {
            var run = await new WindowsProcessRunner().StartAsync(LauncherCommandBuilder.Build(host, ["tree", "1"]), cancellationToken);
            Volatile.Write(ref Run, run); return run;
        }
    }
}
