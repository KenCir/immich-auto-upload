using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;
using ImmichDesktopUploader.ViewModels;

namespace ImmichDesktopUploader.Tests.Gui;

internal sealed class FakeDesktop : IDesktopApplication
{
    private Action<DesktopSnapshot>? changed;
    public DesktopSnapshot Snapshot { get; private set; } = new(1, null, false, false, null, null);
    public int Subscribers => changed?.GetInvocationList().Length ?? 0;
    public event Action<DesktopSnapshot>? Changed { add => changed += value; remove => changed -= value; }
    public List<Guid> Restarts { get; } = [];
    public int Saves, Pauses, Resumes, Restores, Disposals;
    public AppFailure? SaveFailure;
    public TaskCompletionSource? SaveGate;
    public ImmichConnectionSettings? NewCredentials;
    public void Publish(DesktopSnapshot state) { Snapshot = state; changed?.Invoke(state); }
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task SaveAsync(AppSettings settings, ImmichConnectionSettings? newCredentials = null)
    {
        Saves++; if (SaveGate is not null) await SaveGate.Task;
        if (SaveFailure is not null) throw new AppOperationException(SaveFailure.Value);
        NewCredentials = newCredentials;
        Publish(Snapshot with { Sequence = Snapshot.Sequence + 1, Settings = settings, CredentialsConfigured = true, Failure = null });
    }
    public Task RestoreBackupAsync() { Restores++; return Task.CompletedTask; }
    public Task RestartAsync(Guid id) { Restarts.Add(id); return Task.CompletedTask; }
    public Task PauseAsync() { Pauses++; Publish(Snapshot with { Sequence = Snapshot.Sequence + 1, Manager = Snapshot.Manager! with { IsPaused = true } }); return Task.CompletedTask; }
    public Task ResumeAsync() { Resumes++; Publish(Snapshot with { Sequence = Snapshot.Sequence + 1, Manager = Snapshot.Manager! with { IsPaused = false } }); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}
internal sealed class QueuedDispatcher : IUiDispatcher
{
    private readonly object gate = new();
    private readonly List<Action> queue = [];
    public bool Enqueue(Action action) { lock (gate) queue.Add(action); return true; }
    public void Drain(bool reverse = false)
    {
        Action[] work; lock (gate) { work = queue.ToArray(); queue.Clear(); }
        foreach (var action in reverse ? work.Reverse() : work) action();
    }
}
internal sealed class FakeDialogs : IDesktopDialogs
{
    public string? Picked;
    public bool Confirm;
    public int Confirmations, PickerCalls, OpenCalls;
    public Func<FolderEditorViewModel, Task>? FolderAction;
    public Func<SettingsViewModel, Task>? SettingsAction;
    public Task<string?> PickFolderAsync() { PickerCalls++; return Task.FromResult(Picked); }
    public Task EditFolderAsync(FolderEditorViewModel draft) => FolderAction?.Invoke(draft) ?? Task.CompletedTask;
    public Task EditSettingsAsync(SettingsViewModel draft) => SettingsAction?.Invoke(draft) ?? Task.CompletedTask;
    public Task<bool> ConfirmRemoveAsync(string name) { Confirmations++; return Task.FromResult(Confirm); }
    public Task<bool> ConfirmRestoreAsync() { Confirmations++; return Task.FromResult(Confirm); }
    public Task OpenSettingsFolderAsync() { OpenCalls++; return Task.CompletedTask; }
}
