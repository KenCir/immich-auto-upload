using System.Collections.Immutable;
namespace ImmichDesktopUploader.Application;

public sealed record ManagedFolderSnapshot(UploadFolderSettings Folder, SessionSnapshot Session, bool ResumeBlocked);
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
    private Published published = new(false, false, [], []);

    public UploadManager(AppSettings settings, IUploadSessionFactory factory, AppDiagnostics? diagnostics = null,
        bool initiallyPaused = false, ImmutableHashSet<Guid>? heldErrors = null)
    {
        this.settings = SettingsValidation.Validate(settings).Settings;
        this.factory = factory;
        this.diagnostics = diagnostics;
        paused = initiallyPaused;
        initialErrors = heldErrors ?? [];
        Publish();
    }

    public UploadManagerSnapshot Snapshot
    {
        get
        {
            var view = Volatile.Read(ref published);
            return new(view.Paused, view.Running, view.Folders.Select(f => new ManagedFolderSnapshot(f.Folder, f.Session.Snapshot, f.ResumeBlocked)).ToImmutableArray(), view.Warnings);
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
        sessions.Remove(entry.Folder.Id);
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
        sessions.Values.Select(e => new PublishedFolder(e.Folder, e.Session, e.HoldError)).ToImmutableArray(),
        SettingsValidation.Validate(settings).Warnings));

    public ValueTask DisposeAsync()
    {
        lock (ingress)
        {
            if (disposal is not null) return new(disposal);
            closing = true;
            disposal = DisposeCoreAsync(tail);
            return new(disposal);
        }
    }
    private async Task DisposeCoreAsync(Task previous)
    {
        await Task.Yield();
        await previous.ConfigureAwait(false);
        running = false;
        var failures = await Task.WhenAll(sessions.Values.Select(async entry =>
        {
            var failed = false;
            try { await entry.Session.StopAsync(SessionStopReason.ApplicationShutdown).ConfigureAwait(false); } catch { failed = true; }
            try { await entry.Session.DisposeAsync().ConfigureAwait(false); } catch { failed = true; }
            return failed;
        })).ConfigureAwait(false);
        sessions.Clear(); Publish();
        if (failures.Any(f => f)) throw new AppOperationException(AppFailure.CleanupFailed);
    }
    private sealed class Entry(UploadFolderSettings folder, IManagedUploadSession session)
    {
        public UploadFolderSettings Folder = folder;
        public IManagedUploadSession Session { get; } = session;
        public bool Dirty, HoldError, Quarantined;
    }
    private sealed record PublishedFolder(UploadFolderSettings Folder, IManagedUploadSession Session, bool ResumeBlocked);
    private sealed record Published(bool Paused, bool Running, ImmutableArray<PublishedFolder> Folders, ImmutableArray<FolderOverlap> Warnings);
}
