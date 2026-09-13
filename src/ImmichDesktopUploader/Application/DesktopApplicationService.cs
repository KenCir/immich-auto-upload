using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Persistence;

namespace ImmichDesktopUploader.Application;

public sealed record DesktopSnapshot(long Sequence, AppSettings? Settings, bool CredentialsConfigured,
    bool CanRestoreBackup, AppFailure? Failure, UploadManagerSnapshot? Manager);

public interface IDesktopApplication : IAsyncDisposable
{
    DesktopSnapshot Snapshot { get; }
    event Action<DesktopSnapshot>? Changed;
    Task InitializeAsync();
    Task SaveAsync(AppSettings settings, ImmichConnectionSettings? newCredentials = null);
    Task RestoreBackupAsync();
    Task RestartAsync(Guid id);
    Task PauseAsync();
    Task ResumeAsync();
}

// Composition/lifetime bridge for a desktop client. The timer reads in-memory state only.
public sealed class DesktopApplicationService : IDesktopApplication
{
    private readonly SettingsService settings;
    private readonly AppCoordinator coordinator;
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
        Func<ImmichConnectionSettings, IUploadSessionFactory>? factory = null, AppDiagnostics? diagnostics = null)
    {
        settings = new(paths, diagnostics);
        coordinator = new(settings, new CredentialService(paths, diagnostics), factory, diagnostics);
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

    public Task SaveAsync(AppSettings draft, ImmichConnectionSettings? newCredentials = null) => RunAsync(async () =>
    {
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
            await action().ConfigureAwait(false);
            Publish(Snapshot.Settings, Snapshot.CredentialsConfigured, Snapshot.CanRestoreBackup, Snapshot.Failure);
        }
        finally { gate.Release(); }
    }
    private void Publish(AppSettings? current, bool credentials, bool recovery, AppFailure? failure)
    {
        var next = new DesktopSnapshot(++sequence, current, credentials, recovery, failure, coordinator.Snapshot);
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
            closing = true; lifetime.Cancel(); disposal = DisposeCoreAsync(); return new(disposal);
        }
    }
    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (poller is not null) await poller.ConfigureAwait(false);
            Changed = null; await coordinator.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); lifetime.Dispose(); }
    }
}
