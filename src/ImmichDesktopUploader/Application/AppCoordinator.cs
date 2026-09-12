using System.Collections.Immutable;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Persistence;

namespace ImmichDesktopUploader.Application;

// Own this service for the app lifetime. No UI, registry, or network health checks.
public sealed class AppCoordinator : IAsyncDisposable
{
    private readonly ISettingsService settingsService;
    private readonly ICredentialService credentialService;
    private readonly Func<ImmichConnectionSettings, IUploadSessionFactory> factory;
    private readonly AppDiagnostics? diagnostics;
    private readonly object ingress = new();
    private Task tail = Task.CompletedTask;
    private Task? disposal;
    private bool closing;
    private UploadManager? manager;
    private ImmichConnectionSettings? connection;
    public UploadManagerSnapshot? Snapshot => Volatile.Read(ref manager)?.Snapshot;

    public AppCoordinator(ISettingsService settingsService, ICredentialService credentialService,
        Func<ImmichConnectionSettings, IUploadSessionFactory>? factory = null, AppDiagnostics? diagnostics = null)
    {
        this.settingsService = settingsService; this.credentialService = credentialService;
        this.factory = factory ?? (c => new UploadSessionFactory(c)); this.diagnostics = diagnostics;
    }

    public Task StartAsync(CancellationToken token = default) => Submit(async () =>
    {
        if (manager is not null) return;
        var loaded = await settingsService.LoadAsync().ConfigureAwait(false);
        if (!loaded.Success) throw new AppOperationException(loaded.Failure!.Value);
        var valid = SettingsValidation.Validate(loaded.Settings!).Settings;
        var credentials = await credentialService.LoadAsync(valid.ServerUrl).ConfigureAwait(false);
        Verify(valid, credentials);
        CheckOpen();
        var replacement = new UploadManager(valid, factory(credentials), diagnostics);
        Volatile.Write(ref manager, replacement); connection = credentials;
        await replacement.StartAllAsync().ConfigureAwait(false);
    }, token);

    public Task UpdateAsync(AppSettings draft, ImmichConnectionSettings? newCredentials = null, CancellationToken token = default)
    {
        var validated = SettingsValidation.Validate(draft).Settings;
        if (newCredentials is not null) Verify(validated, newCredentials);
        return Submit(async () =>
        {
            // Refuse a corrupt/future primary before changing credentials as well as before settings save.
            var existing = await settingsService.LoadAsync().ConfigureAwait(false);
            if (!existing.Success && existing.Failure != AppFailure.MissingSettings)
                throw new AppOperationException(existing.Failure!.Value);
            // Validate credentials before making any write; never pair a new URL with an old key implicitly.
            var expected = newCredentials ?? await credentialService.LoadAsync(validated.ServerUrl).ConfigureAwait(false);
            Verify(validated, expected);
            if (newCredentials is not null) await credentialService.SaveAsync(newCredentials).ConfigureAwait(false);
            await settingsService.SaveAsync(validated).ConfigureAwait(false);
            var persisted = await settingsService.LoadAsync().ConfigureAwait(false);
            if (!persisted.Success || !SameSettings(validated, persisted.Settings!))
                throw new AppOperationException(AppFailure.InvalidSettings);
            var confirmed = await credentialService.LoadAsync(validated.ServerUrl).ConfigureAwait(false);
            Verify(validated, confirmed);
            if (!expected.Matches(confirmed)) throw new AppOperationException(AppFailure.CredentialMismatch);
            CheckOpen();
            var old = manager;
            if (old is null) return; // Save first-run setup without implicitly opting into upload.
            if (connection!.Matches(confirmed))
            {
                await old.ApplySettingsAsync(validated).ConfigureAwait(false);
                return;
            }
            var state = old.Snapshot;
            // Dispose is the ownership fence. A failed cleanup prevents creation of the next backend context.
            await old.DisposeAsync().ConfigureAwait(false);
            CheckOpen();
            var held = state.Folders.Where(f => f.ResumeBlocked ||
                (state.IsPaused && f.Session.Status == UploadSessionStatus.Error)).Select(f => f.Folder.Id).ToImmutableHashSet();
            var replacement = new UploadManager(validated, factory(confirmed), diagnostics, state.IsPaused, held);
            Volatile.Write(ref manager, replacement); connection = confirmed;
            if (state.RunningRequested) await replacement.StartAllAsync().ConfigureAwait(false);
            else await replacement.ApplySettingsAsync(validated).ConfigureAwait(false);
        }, token);
    }

    public Task PauseAllAsync(CancellationToken token = default) => Submit(() => RequireManager().PauseAllAsync(), token);
    public Task ResumeAllAsync(CancellationToken token = default) => Submit(() => RequireManager().ResumeAllAsync(), token);
    public Task RestartAsync(Guid id, CancellationToken token = default) => Submit(() => RequireManager().RestartAsync(id), token);
    public Task StartAllAsync(CancellationToken token = default) => Submit(() => RequireManager().StartAllAsync(), token);
    public async Task<ManagerOperationResult> StopAllAsync(CancellationToken token = default)
    {
        ManagerOperationResult? result = null;
        await Submit(async () => result = await RequireManager().StopAllAsync().ConfigureAwait(false), token).ConfigureAwait(false);
        return result!;
    }
    private UploadManager RequireManager() => manager ?? throw new AppOperationException(AppFailure.MissingSettings);
    private static void Verify(AppSettings settings, ImmichConnectionSettings credentials)
    { if (settings.ServerUrl != credentials.ServerUrl) throw new AppOperationException(AppFailure.CredentialMismatch); }
    private static bool SameSettings(AppSettings a, AppSettings b) => a.SchemaVersion == b.SchemaVersion &&
        a.ServerUrl == b.ServerUrl && a.StartWithWindows == b.StartWithWindows && a.Folders.Length == b.Folders.Length &&
        a.Folders.Zip(b.Folders).All(p => p.First.Id == p.Second.Id && p.First.Enabled == p.Second.Enabled && SettingsValidation.SameRun(p.First, p.Second));

    private Task Submit(Func<Task> operation, CancellationToken token)
    {
        Task result;
        lock (ingress)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            result = RunAsync(tail, operation); tail = ObserveAsync(result);
        }
        return result.WaitAsync(token);
    }
    private async Task RunAsync(Task previous, Func<Task> operation)
    {
        await Task.Yield(); await previous.ConfigureAwait(false); CheckOpen();
        try { await operation().ConfigureAwait(false); }
        catch (AppOperationException e) { diagnostics?.Emit(AppEventKind.ConsistencyFailure, failure: e.Failure); throw; }
    }
    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }
    private void CheckOpen() { lock (ingress) ObjectDisposedException.ThrowIf(closing, this); }
    public ValueTask DisposeAsync()
    {
        lock (ingress)
        {
            if (disposal is not null) return new(disposal);
            closing = true; disposal = DisposeCoreAsync(tail); return new(disposal);
        }
    }
    private async Task DisposeCoreAsync(Task previous)
    {
        await Task.Yield(); await previous.ConfigureAwait(false);
        if (manager is not null) await manager.DisposeAsync().ConfigureAwait(false);
        Volatile.Write(ref manager, null); connection = null;
    }
}
