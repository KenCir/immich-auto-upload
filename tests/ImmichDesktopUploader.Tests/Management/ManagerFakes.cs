using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Persistence;

namespace ImmichDesktopUploader.Tests.Management;

internal sealed class ManagerFactory : IUploadSessionFactory
{
    public Dictionary<Guid, ManagerSession> Sessions { get; } = [];
    public HashSet<Guid> StartErrors { get; } = [];
    public List<ManagerSession> All { get; } = [];
    public IManagedUploadSession Create(UploadSessionConfiguration configuration)
    {
        var session = new ManagerSession(configuration) { FailStart = StartErrors.Contains(configuration.FolderId) };
        if (Sessions.TryGetValue(configuration.FolderId, out var old) && !old.Disposed) throw new Exception("Identity reused before disposal.");
        Sessions[configuration.FolderId] = session; All.Add(session); return session;
    }
}
internal sealed class ManagerSession(UploadSessionConfiguration configuration) : IManagedUploadSession
{
    private SessionSnapshot snapshot = new(configuration.FolderId, UploadSessionStatus.Stopped, 0, null, 0, null, null, null, null);
    public SessionSnapshot Snapshot => Volatile.Read(ref snapshot);
    public void Publish(SessionSnapshot value) => Volatile.Write(ref snapshot, value);
    public UploadSessionConfiguration Configuration { get; private set; } = configuration;
    public int Starts, Restarts, Applies, Stops, Disposals;
    public bool Disposed, FailStart, FailStop, FailDispose;
    public TaskCompletionSource? StopGate;
    public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Status(UploadSessionStatus status) => Volatile.Write(ref snapshot, Snapshot with { Status = status });
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (Snapshot.Status != UploadSessionStatus.Stopped) return Task.CompletedTask;
        Starts++; Status(FailStart ? UploadSessionStatus.Error : UploadSessionStatus.Running); return Task.CompletedTask;
    }
    public Task RestartAsync(CancellationToken cancellationToken = default)
    { ObjectDisposedException.ThrowIf(Disposed, this); Restarts++; Status(UploadSessionStatus.Running); return Task.CompletedTask; }
    public Task ApplyConfigurationAsync(UploadSessionConfiguration replacement, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (replacement.FolderId != Configuration.FolderId) throw new Exception("Changed identity.");
        Configuration = replacement; Applies++; Status(UploadSessionStatus.Running); return Task.CompletedTask;
    }
    public async Task StopAsync(SessionStopReason reason = SessionStopReason.UserRequested, CancellationToken cancellationToken = default)
    {
        Stops++; snapshot = Snapshot with { StopReason = reason };
        StopEntered.TrySetResult();
        if (StopGate is not null) await StopGate.Task;
        if (FailStop) throw new InvalidOperationException("Synthetic cleanup failure");
        Status(UploadSessionStatus.Stopped);
    }
    public ValueTask DisposeAsync()
    { Disposals++; Disposed = true; Status(UploadSessionStatus.Stopped); return FailDispose ? ValueTask.FromException(new Exception("Synthetic disposal failure")) : ValueTask.CompletedTask; }
}

internal sealed class MemorySettings(AppSettings initial) : ISettingsService
{
    public AppSettings Value = initial;
    public bool FailSave;
    public AppFailure? LoadFailure;
    public TaskCompletionSource? SaveGate;
    public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<SettingsLoadResult> LoadAsync(CancellationToken token = default) => Task.FromResult(
        LoadFailure is null ? new SettingsLoadResult(Value, null, null) : new SettingsLoadResult(null, LoadFailure, null));
    public async Task SaveAsync(AppSettings settings, CancellationToken token = default)
    {
        SaveEntered.TrySetResult(); if (SaveGate is not null) await SaveGate.Task;
        if (FailSave) throw new AppOperationException(AppFailure.StorageFailure);
        Value = settings;
    }
}
internal sealed class MemoryCredentials(ImmichConnectionSettings initial) : ICredentialService
{
    public ImmichConnectionSettings Value = initial;
    public bool FailSave;
    public AppFailure? LoadFailure;
    public Task<ImmichConnectionSettings> LoadAsync(string expectedServerUrl, CancellationToken token = default)
    {
        if (LoadFailure is not null) throw new AppOperationException(LoadFailure.Value);
        if (expectedServerUrl != Value.ServerUrl) throw new AppOperationException(AppFailure.CredentialMismatch);
        return Task.FromResult(Value);
    }
    public Task SaveAsync(ImmichConnectionSettings connection, CancellationToken token = default)
    { if (FailSave) throw new AppOperationException(AppFailure.StorageFailure); Value = connection; return Task.CompletedTask; }
}
