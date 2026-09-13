using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Persistence;

namespace ImmichDesktopUploader.Application;

public sealed record DesktopSnapshot(long Sequence, AppSettings? Settings, bool CredentialsConfigured,
    bool CanRestoreBackup, AppFailure? Failure, UploadManagerSnapshot? Manager, StartupRegistration? Startup = null);

public interface IDesktopApplication : IAsyncDisposable
{
    DesktopSnapshot Snapshot { get; }
    event Action<DesktopSnapshot>? Changed;
    Task InitializeAsync();
    Task SaveAsync(AppSettings settings, ImmichConnectionSettings? newCredentials = null);
    Task SaveSettingsAsync(AppSettings settings, ImmichConnectionSettings? newCredentials = null) => SaveAsync(settings, newCredentials);
    Task RestoreBackupAsync();
    Task RestartAsync(Guid id);
    Task PauseAsync();
    Task ResumeAsync();
}

// Desktop composition/lifetime bridge. The timer projects manager state and the owned
// startup registry value; it never probes the server or changes session recovery policy.
public sealed class DesktopApplicationService : IDesktopApplication
{
    private readonly SettingsService settings;
    private readonly CredentialService credentials;
    private readonly AppCoordinator coordinator;
    private readonly IStartupService? startup;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalGate = new();
    private Task? poller, disposal;
    private bool closing;
    private long sequence;
    private DesktopSnapshot snapshot = new(0, null, false, false, null, null);
    public DesktopSnapshot Snapshot => Volatile.Read(ref snapshot);
    public event Action<DesktopSnapshot>? Changed;

    public DesktopApplicationService(AppStoragePaths paths,
        Func<ImmichConnectionSettings, IUploadSessionFactory>? factory = null, AppDiagnostics? diagnostics = null, IStartupService? startup = null)
    {
        settings = new(paths, diagnostics);
        credentials = new(paths, diagnostics);
        coordinator = new(settings, credentials, factory, diagnostics);
        this.startup = startup;
    }

    public Task InitializeAsync() => RunAsync(async () =>
    {
        var loaded = await settings.LoadAsync().ConfigureAwait(false);
        if (!loaded.Success)
        {
            Publish(null, false, loaded.RecoveryCandidate is not null && loaded.Failure != AppFailure.UnsupportedSchema, loaded.Failure);
        }
        else
        {
            try { await coordinator.StartAsync().ConfigureAwait(false); Publish(loaded.Settings, true, false, null); }
            catch (AppOperationException e) { Publish(loaded.Settings, false, false, e.Failure); }
        }
        poller ??= Task.Run(PollAsync);
    });

    public Task SaveAsync(AppSettings draft, ImmichConnectionSettings? newCredentials = null) => SaveCoreAsync(draft, newCredentials, false);
    public Task SaveSettingsAsync(AppSettings draft, ImmichConnectionSettings? newCredentials = null) => SaveCoreAsync(draft, newCredentials, true);
    private Task SaveCoreAsync(AppSettings draft, ImmichConnectionSettings? newCredentials, bool updateStartup) => RunAsync(async () =>
    {
        draft = SettingsValidation.Validate(draft).Settings;
        // Do not mutate startup registration for a corrupt/future primary or mismatched credential.
        var before = await settings.LoadAsync().ConfigureAwait(false);
        if (!before.Success && before.Failure != AppFailure.MissingSettings) throw new AppOperationException(before.Failure!.Value);
        if (newCredentials is not null && newCredentials.ServerUrl != draft.ServerUrl) throw new AppOperationException(AppFailure.CredentialMismatch);
        if (newCredentials is null) await credentials.LoadAsync(draft.ServerUrl).ConfigureAwait(false);
        if (updateStartup && startup is not null)
        {
            if (draft.StartWithWindows) startup.Register(); else startup.Unregister();
        }
        await coordinator.UpdateAsync(draft, newCredentials).ConfigureAwait(false);
        await coordinator.StartAsync().ConfigureAwait(false);
        var loaded = await settings.LoadAsync().ConfigureAwait(false);
        if (!loaded.Success) throw new AppOperationException(loaded.Failure!.Value);
        Publish(loaded.Settings, true, false, null);
    });
    public async Task RestoreBackupAsync()
    {
        await RunAsync(() => settings.RecoverBackupAsync()).ConfigureAwait(false);
        await InitializeAsync().ConfigureAwait(false);
    }
    public Task RestartAsync(Guid id) => RunAsync(() => coordinator.RestartAsync(id));
    public Task PauseAsync() => RunAsync(() => coordinator.PauseAllAsync());
    public Task ResumeAsync() => RunAsync(() => coordinator.ResumeAllAsync());

    private async Task RunAsync(Func<Task> action)
    {
        lock (disposalGate) ObjectDisposedException.ThrowIf(closing, this);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (disposalGate) ObjectDisposedException.ThrowIf(closing, this);
            try { await action().ConfigureAwait(false); }
            finally { Publish(Snapshot.Settings, Snapshot.CredentialsConfigured, Snapshot.CanRestoreBackup, Snapshot.Failure); }
        }
        finally { gate.Release(); }
    }
    private void Publish(AppSettings? current, bool credentials, bool recovery, AppFailure? failure)
    {
        var next = new DesktopSnapshot(++sequence, current, credentials, recovery, failure, coordinator.Snapshot, startup?.Inspect());
        Volatile.Write(ref snapshot, next);
        Changed?.Invoke(next);
    }
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try { Publish(Snapshot.Settings, Snapshot.CredentialsConfigured, Snapshot.CanRestoreBackup, Snapshot.Failure); }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposal is not null) return new(disposal);
            closing = true; lifetime.Cancel();
            var cleanup = coordinator.DisposeAsync().AsTask();
            disposal = DisposeCoreAsync(cleanup); return new(disposal);
        }
    }
    private async Task DisposeCoreAsync(Task cleanup)
    {
        await Task.Yield();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (poller is not null) await poller.ConfigureAwait(false);
            Changed = null; await cleanup.ConfigureAwait(false);
        }
        finally { gate.Release(); lifetime.Dispose(); }
    }
}
