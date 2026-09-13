using System.Collections.Immutable;
namespace ImmichDesktopUploader.Application;

public sealed record ManagedFolderSnapshot(UploadFolderSettings Folder, SessionSnapshot Session, bool ResumeBlocked,
    RecoveryCandidate? RecoveryCandidate = null);
public sealed record UploadManagerSnapshot(bool IsPaused, bool RunningRequested, ImmutableArray<ManagedFolderSnapshot> Folders,
    ImmutableArray<FolderOverlap> Warnings);
public sealed record ManagerOperationResult(ImmutableArray<Guid> FailedFolderIds)
{ public bool Success => FailedFolderIds.IsEmpty; }

public sealed class UploadManager : IAsyncDisposable
{
    private readonly object ingress = new();
    private readonly IUploadSessionFactory factory;
    private readonly AppDiagnostics? diagnostics;
    private readonly Dictionary<Guid, Entry> sessions = [];
    private Task tail = Task.CompletedTask;
    private Task? disposal;
    private bool closing, paused, running;
    private AppSettings settings;
    private readonly ImmutableHashSet<Guid> initialErrors;
    private readonly long connectionGeneration;
    private long connectionSequence, consumedOutage;
    private Published published = new(false, false, [], []);

    public UploadManager(AppSettings settings, IUploadSessionFactory factory, AppDiagnostics? diagnostics = null,
        bool initiallyPaused = false, ImmutableHashSet<Guid>? heldErrors = null, long connectionGeneration = 0)
    {
        this.settings = SettingsValidation.Validate(settings).Settings;
        this.factory = factory;
        this.diagnostics = diagnostics;
        paused = initiallyPaused;
        initialErrors = heldErrors ?? [];
        this.connectionGeneration = connectionGeneration;
        Publish();
    }

    public UploadManagerSnapshot Snapshot
    {
        get
        {
            var view = Volatile.Read(ref published);
            return new(view.Paused, view.Running, view.Folders.Select(f => new ManagedFolderSnapshot(f.Folder, f.Session.Snapshot, f.ResumeBlocked, f.Candidate)).ToImmutableArray(), view.Warnings);
        }
    }

    public Task StartAllAsync(CancellationToken token = default) => Submit(async () =>
    {
        running = true;
        EnsureSessions();
        foreach (var entry in sessions.Values) await StartEligibleAsync(entry).ConfigureAwait(false);
        diagnostics?.Emit(AppEventKind.ManagerStarted);
    }, token);

    public Task ApplySettingsAsync(AppSettings replacement, CancellationToken token = default)
    {
        var validated = SettingsValidation.Validate(replacement).Settings;
        return Submit(async () =>
        {
            if (validated.ServerUrl != settings.ServerUrl) throw new AppOperationException(AppFailure.CredentialMismatch);
            var desired = validated.Folders.ToDictionary(f => f.Id);
            foreach (var entry in sessions.Values.Where(e => !desired.ContainsKey(e.Folder.Id)).ToArray())
            {
                await RemoveAsync(entry).ConfigureAwait(false);
            }
            foreach (var folder in validated.Folders)
            {
                CheckOpen();
                if (!sessions.TryGetValue(folder.Id, out var entry))
                {
                    entry = Add(folder);
                    await StartEligibleAsync(entry).ConfigureAwait(false);
                    continue;
                }
                var changed = !SettingsValidation.SameRun(entry.Folder, folder);
                var wasEnabled = entry.Folder.Enabled;
                if (wasEnabled != folder.Enabled) diagnostics?.Emit(folder.Enabled ? AppEventKind.FolderEnabled : AppEventKind.FolderDisabled, folder.Id);
                if (changed || wasEnabled != folder.Enabled)
                {
                    InvalidateRecovery(entry, folder.Enabled ? RecoverySuppressionReason.SettingsChanged : RecoverySuppressionReason.Disabled);
                    entry.ConfigurationGeneration++;
                }
                entry.Folder = folder;
                if (changed) { entry.Dirty = true; diagnostics?.Emit(AppEventKind.SessionChanged, folder.Id); }
                if (wasEnabled && !folder.Enabled)
                    await entry.Session.StopAsync(SessionStopReason.Disabled).ConfigureAwait(false);
                else if (folder.Enabled && (changed || !wasEnabled))
                    await StartEligibleAsync(entry).ConfigureAwait(false);
            }
            settings = validated;
        }, token);
    }

    public Task PauseAllAsync(CancellationToken token = default) => Submit(async () =>
    {
        if (paused) return;
        paused = true;
        foreach (var entry in sessions.Values) InvalidateRecovery(entry, RecoverySuppressionReason.Paused);
        foreach (var entry in sessions.Values)
            entry.HoldError |= entry.Session.Snapshot.Status == UploadSessionStatus.Error;
        var result = await StopEntriesAsync(SessionStopReason.Paused, preserveErrors: true).ConfigureAwait(false);
        diagnostics?.Emit(AppEventKind.Paused);
        ThrowIfFailed(result);
    }, token);

    public Task ResumeAllAsync(CancellationToken token = default) => Submit(async () =>
    {
        if (!paused) return;
        paused = false;
        if (running) foreach (var entry in sessions.Values) await StartEligibleAsync(entry).ConfigureAwait(false);
        diagnostics?.Emit(AppEventKind.Resumed);
    }, token);

    public Task RestartAsync(Guid folderId, CancellationToken token = default) => Submit(async () =>
    {
        var entry = sessions[folderId];
        if (entry.Quarantined) throw new AppOperationException(AppFailure.CleanupFailed);
        if (!entry.Folder.Enabled || paused) throw new AppOperationException(AppFailure.DisabledOrPaused);
        CheckOpen();
        InvalidateRecovery(entry, RecoverySuppressionReason.UserStopped);
        diagnostics?.Emit(AppEventKind.FolderRestart, folderId);
        entry.HoldError = false;
        running = true;
        if (entry.Dirty)
        {
            await InvokeIfOpen(() => entry.Session.ApplyConfigurationAsync(entry.Folder.ToConfiguration())).ConfigureAwait(false);
            entry.Dirty = false;
        }
        else await InvokeIfOpen(() => entry.Session.RestartAsync()).ConfigureAwait(false);
    }, token);

    public async Task<ManagerOperationResult> StopAllAsync(CancellationToken token = default)
    {
        ManagerOperationResult? result = null;
        await Submit(async () =>
        {
            running = false;
            foreach (var entry in sessions.Values) InvalidateRecovery(entry, RecoverySuppressionReason.UserStopped);
            result = await StopEntriesAsync(SessionStopReason.UserRequested).ConfigureAwait(false);
            diagnostics?.Emit(AppEventKind.ManagerStopped);
        }, token).ConfigureAwait(false);
        return result!;
    }

    private void EnsureSessions()
    {
        foreach (var folder in settings.Folders)
            if (!sessions.ContainsKey(folder.Id)) Add(folder);
    }
    private Entry Add(UploadFolderSettings folder)
    {
        lock (ingress)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            var entry = new Entry(folder, factory.Create(folder.ToConfiguration())) { HoldError = initialErrors.Contains(folder.Id) };
            sessions.Add(folder.Id, entry);
            diagnostics?.Emit(AppEventKind.SessionAdded, folder.Id);
            return entry;
        }
    }
    private async Task StartEligibleAsync(Entry entry)
    {
        CheckOpen();
        if (!running || paused || !entry.Folder.Enabled || entry.HoldError || entry.Quarantined) return;
        if (entry.Dirty)
        {
            await InvokeIfOpen(() => entry.Session.ApplyConfigurationAsync(entry.Folder.ToConfiguration())).ConfigureAwait(false);
            entry.Dirty = false;
        }
        else await InvokeIfOpen(() => entry.Session.StartAsync()).ConfigureAwait(false);
    }
    private async Task RemoveAsync(Entry entry)
    {
        InvalidateRecovery(entry, RecoverySuppressionReason.Removed);
        var failed = false;
        try { await entry.Session.StopAsync(SessionStopReason.Removed).ConfigureAwait(false); }
        catch { failed = true; }
        try { await entry.Session.DisposeAsync().ConfigureAwait(false); }
        catch { failed = true; }
        if (failed)
        {
            entry.Quarantined = true; entry.HoldError = true;
            throw new AppOperationException(AppFailure.CleanupFailed); // Retain identity; never replace uncertain ownership.
        }
        lock (ingress) sessions.Remove(entry.Folder.Id);
        diagnostics?.Emit(AppEventKind.SessionRemoved, entry.Folder.Id);
    }

    private async Task<ManagerOperationResult> StopEntriesAsync(SessionStopReason reason, bool preserveErrors = false)
    {
        var tasks = sessions.Values.Select(async entry =>
        {
            if (preserveErrors && entry.HoldError) return (Guid?)null;
            try { await entry.Session.StopAsync(reason).ConfigureAwait(false); return (Guid?)null; }
            catch { return entry.Folder.Id; }
        }).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new(results.Where(id => id.HasValue).Select(id => id!.Value).ToImmutableArray());
    }
    private static void ThrowIfFailed(ManagerOperationResult result)
    { if (!result.Success) throw new AppOperationException(AppFailure.CleanupFailed); }

    // The same queue as Pause/Apply/Remove owns candidate registration and edge consumption.
    // Checking retains LastCompletedStatus, so it does not end an unavailable period.
    public Task ObserveConnectionAsync(ConnectionSnapshot connection, CancellationToken token = default) => Submit(async () =>
    {
        if (connection.ConnectionGeneration != connectionGeneration)
        { Suppressed(null, RecoverySuppressionReason.StaleConnection); return; }
        if (connection.Sequence <= connectionSequence)
        { Suppressed(null, RecoverySuppressionReason.StaleEvent); return; }
        connectionSequence = connection.Sequence;
        if (connection.RecoveryEdge && connection.OutageId <= consumedOutage)
        { Suppressed(null, RecoverySuppressionReason.AlreadyConsumed); return; }
        if (connection.LastCompletedStatus == ConnectionStatus.Unavailable || connection.RecoveryEdge)
        {
            // Also reconcile immediately before consuming an edge: no polling race can
            // miss an Error which arrived after the previous connection notification.
            foreach (var entry in sessions.Values)
            {
                var error = entry.Session.Snapshot;
                if (CanRecover(entry, error) && connection.OutageStartedAt is { } since &&
                    error.LastError!.Timestamp >= since &&
                    (!connection.RecoveryEdge || error.LastError.Timestamp <= connection.LastCheckedAt))
                {
                    var candidate = new RecoveryCandidate(entry.Folder.Id, connectionGeneration, entry.ConfigurationGeneration,
                        connection.OutageId, error.RunGeneration, error.LastError.Timestamp);
                    if (entry.Candidate != candidate)
                    { entry.Candidate = candidate; diagnostics?.Emit(AppEventKind.RecoveryCandidateRegistered, entry.Folder.Id, generation: connectionGeneration); }
                }
            }
        }
        if (!connection.RecoveryEdge) return;
        diagnostics?.ConnectionEvent(AppEventKind.RecoveryEdgeDetected, connection);
        consumedOutage = connection.OutageId;
        diagnostics?.Emit(AppEventKind.RecoveryConsumed, generation: connectionGeneration);
        var candidates = sessions.Values.Where(e => e.Candidate is not null).Select(e => (Entry: e, Candidate: e.Candidate!)).ToArray();
        // Consume before any await. Pause/Resume never replays a past edge.
        foreach (var entry in sessions.Values) entry.Candidate = null;
        if (paused || !running)
        { Suppressed(null, paused ? RecoverySuppressionReason.Paused : RecoverySuppressionReason.UserStopped); return; }
        foreach (var (entry, candidate) in candidates)
        {
            CheckOpen();
            var current = entry.Session.Snapshot;
            if (candidate.ConnectionGeneration != connectionGeneration || candidate.OutageId != connection.OutageId ||
                candidate.ConfigurationGeneration != entry.ConfigurationGeneration || candidate.RunGeneration != current.RunGeneration ||
                candidate.FailureAt != current.LastError?.Timestamp || !CanRecover(entry, current))
            { Suppressed(entry.Folder.Id, RecoverySuppressionReason.NotError); continue; }
            diagnostics?.Emit(AppEventKind.RecoveryAttempted, entry.Folder.Id, generation: connectionGeneration);
            await InvokeIfOpen(() => entry.Session.RestartAsync()).ConfigureAwait(false);
        }
    }, token);

    private bool CanRecover(Entry entry, SessionSnapshot state) => running && !paused && entry.Folder.Enabled &&
        !entry.HoldError && !entry.Quarantined && state.Status == UploadSessionStatus.Error && state.RetryExhausted &&
        state.LauncherPid is null && state.StopReason is null && state.RunGeneration > entry.RecoveryFloor &&
        state.LastError is { Kind: SessionErrorKind.StartFailed or SessionErrorKind.UnexpectedExit or SessionErrorKind.ObservationFailed or SessionErrorKind.OutputFailed };
    private void InvalidateRecovery(Entry entry, RecoverySuppressionReason reason)
    {
        if (entry.Candidate is not null) diagnostics?.Emit(AppEventKind.RecoveryCandidateDiscarded, entry.Folder.Id, generation: connectionGeneration, reason: reason);
        entry.Candidate = null; entry.RecoveryFloor = entry.Session.Snapshot.RunGeneration;
    }
    private void Suppressed(Guid? id, RecoverySuppressionReason reason) =>
        diagnostics?.Emit(AppEventKind.RecoverySuppressed, id, generation: connectionGeneration, reason: reason);

    private Task Submit(Func<Task> operation, CancellationToken token)
    {
        Task task;
        lock (ingress)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            task = RunQueuedAsync(tail, operation);
            // Observe every task, including callers that cancel only their own wait.
            tail = ObserveAsync(task);
        }
        return task.WaitAsync(token);
    }
    private async Task RunQueuedAsync(Task previous, Func<Task> operation)
    {
        await Task.Yield();
        await previous.ConfigureAwait(false);
        CheckOpen();
        try { await operation().ConfigureAwait(false); }
        finally { Publish(); }
    }
    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }
    private void CheckOpen() { lock (ingress) ObjectDisposedException.ThrowIf(closing, this); }
    private Task InvokeIfOpen(Func<Task> operation)
    { lock (ingress) { ObjectDisposedException.ThrowIf(closing, this); return operation(); } }
    private void Publish() => Volatile.Write(ref published, new(paused, running,
        sessions.Values.Select(e => new PublishedFolder(e.Folder, e.Session, e.HoldError, e.Candidate)).ToImmutableArray(),
        SettingsValidation.Validate(settings).Warnings));

    public ValueTask DisposeAsync()
    {
        lock (ingress)
        {
            if (disposal is not null) return new(disposal);
            closing = true;
            // Deliver Stop intent immediately, even when the operation queue is waiting
            // for another folder's cleanup. Session's existing intent fence cancels retry.
            var stops = sessions.Values.ToDictionary(e => e.Session, e => StopForShutdownAsync(e.Session));
            disposal = DisposeCoreAsync(tail, stops);
            return new(disposal);
        }
    }
    private static async Task StopForShutdownAsync(IManagedUploadSession session) =>
        await session.StopAsync(SessionStopReason.ApplicationShutdown).ConfigureAwait(false);
    private async Task DisposeCoreAsync(Task previous, Dictionary<IManagedUploadSession, Task> stops)
    {
        await Task.Yield();
        await previous.ConfigureAwait(false);
        // Observe also entries removed by the operation that was already in flight.
        await Task.WhenAll(stops.Values.Select(ObserveAsync)).ConfigureAwait(false);
        running = false;
        foreach (var entry in sessions.Values) InvalidateRecovery(entry, RecoverySuppressionReason.Shutdown);
        var failures = await Task.WhenAll(sessions.Values.Select(async entry =>
        {
            var failed = false;
            try { await stops[entry.Session].ConfigureAwait(false); } catch { failed = true; }
            try { await entry.Session.DisposeAsync().ConfigureAwait(false); } catch { failed = true; }
            return failed;
        })).ConfigureAwait(false);
        sessions.Clear(); Publish();
        diagnostics?.Emit(AppEventKind.ManagerDisposed);
        if (failures.Any(f => f)) throw new AppOperationException(AppFailure.CleanupFailed);
    }
    private sealed class Entry(UploadFolderSettings folder, IManagedUploadSession session)
    {
        public UploadFolderSettings Folder = folder;
        public IManagedUploadSession Session { get; } = session;
        public bool Dirty, HoldError, Quarantined;
        public long ConfigurationGeneration = 1, RecoveryFloor;
        public RecoveryCandidate? Candidate;
    }
    private sealed record PublishedFolder(UploadFolderSettings Folder, IManagedUploadSession Session, bool ResumeBlocked, RecoveryCandidate? Candidate);
    private sealed record Published(bool Paused, bool Running, ImmutableArray<PublishedFolder> Folders, ImmutableArray<FolderOverlap> Warnings);
}
