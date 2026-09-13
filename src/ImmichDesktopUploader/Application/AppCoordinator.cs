using System.Collections.Immutable;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.Infrastructure.Persistence;

namespace ImmichDesktopUploader.Application;

// Own the manager and the single connection monitor for the primary app lifetime.
public sealed class AppCoordinator : IAsyncDisposable
{
    private readonly ISettingsService settingsService;
    private readonly ICredentialService credentialService;
    private readonly Func<ImmichConnectionSettings, IUploadSessionFactory> factory;
    private readonly AppDiagnostics? diagnostics;
    private readonly Func<ImmichConnectionSettings, IConnectionProbe>? probeFactory;
    private readonly ISessionClock? probeClock;
    private readonly object ingress = new();
    private Task tail = Task.CompletedTask;
    private Task? disposal;
    private bool closing;
    private UploadManager? manager;
    private ImmichConnectionSettings? connection;
    private ConnectionMonitor? monitor;
    private long connectionGeneration;
    public UploadManagerSnapshot? Snapshot => Volatile.Read(ref manager)?.Snapshot;
    public ConnectionSnapshot Connection => Volatile.Read(ref monitor)?.Snapshot ?? ConnectionSnapshot.Initial;

    public AppCoordinator(ISettingsService settingsService, ICredentialService credentialService,
        Func<ImmichConnectionSettings, IUploadSessionFactory>? factory = null, AppDiagnostics? diagnostics = null,
        Func<ImmichConnectionSettings, IConnectionProbe>? probeFactory = null, ISessionClock? probeClock = null)
    {
        this.settingsService = settingsService; this.credentialService = credentialService;
        this.factory = factory ?? (c => new UploadSessionFactory(c)); this.diagnostics = diagnostics;
        // Existing isolated session-factory tests opt into probes explicitly; never contact
        // a server implicitly from a fake-runtime composition.
        this.probeFactory = probeFactory ?? (factory is null ? c => new ImmichServerInfoProbe(c) : null);
        this.probeClock = probeClock;
    }

    public Task StartAsync(CancellationToken token = default) => Submit(async () =>
    {
        if (manager is not null) return;
        var loaded = await settingsService.LoadAsync().ConfigureAwait(false);
        if (!loaded.Success) throw new AppOperationException(loaded.Failure!.Value);
        var valid = SettingsValidation.Validate(loaded.Settings!).Settings;
        var credentials = await credentialService.LoadAsync(valid.ServerUrl).ConfigureAwait(false);
        Verify(valid, credentials);
        UploadManager replacement;
        lock (ingress)
        {
            CheckOpen();
            replacement = new UploadManager(valid, factory(credentials), diagnostics, connectionGeneration: ++connectionGeneration);
            Volatile.Write(ref manager, replacement); connection = credentials;
        }
        await replacement.StartAllAsync().ConfigureAwait(false);
        lock (ingress) { CheckOpen(); ConfigureMonitor(credentials, connectionGeneration); }
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
            Task oldCleanup;
            long nextGeneration;
            lock (ingress)
            {
                CheckOpen(); nextGeneration = ++connectionGeneration;
                oldCleanup = old.DisposeAsync().AsTask();
                ConfigureMonitor(confirmed, nextGeneration);
            }
            await oldCleanup.ConfigureAwait(false);
            var held = state.Folders.Where(f => f.ResumeBlocked ||
                (state.IsPaused && f.Session.Status == UploadSessionStatus.Error)).Select(f => f.Folder.Id).ToImmutableHashSet();
            UploadManager replacement;
            lock (ingress)
            {
                CheckOpen();
                replacement = new UploadManager(validated, factory(confirmed), diagnostics, state.IsPaused, held, nextGeneration);
                Volatile.Write(ref manager, replacement); connection = confirmed;
            }
            if (state.RunningRequested) await replacement.StartAllAsync().ConfigureAwait(false);
            else await replacement.ApplySettingsAsync(validated).ConfigureAwait(false);
            OnConnection(Connection);
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
    private void ConfigureMonitor(ImmichConnectionSettings credentials, long generation)
    {
        if (probeFactory is null) return;
        if (monitor is null)
        {
            monitor = new ConnectionMonitor(probeClock, diagnostics);
            monitor.Changed += OnConnection;
        }
        monitor.Configure(probeFactory(credentials), generation);
    }
    private void OnConnection(ConnectionSnapshot snapshot)
    {
        lock (ingress)
        {
            if (closing || snapshot.ConnectionGeneration != connectionGeneration || manager is null) return;
            try { _ = ObserveAsync(manager.ObserveConnectionAsync(snapshot)); }
            catch (ObjectDisposedException) { /* Old manager is retiring; the new one receives the latest snapshot. */ }
        }
    }
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
            closing = true;
            var monitorCleanup = monitor?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            var cleanup = manager?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            disposal = DisposeCoreAsync(tail, Task.WhenAll(cleanup, monitorCleanup)); return new(disposal);
        }
    }
    private async Task DisposeCoreAsync(Task previous, Task cleanup)
    {
        await Task.Yield(); await previous.ConfigureAwait(false);
        await cleanup.ConfigureAwait(false);
        Volatile.Write(ref manager, null); connection = null;
    }
}
