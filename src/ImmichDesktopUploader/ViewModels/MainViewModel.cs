using System.Collections.ObjectModel;
using System.Collections.Immutable;
using ImmichDesktopUploader.Application;

namespace ImmichDesktopUploader.ViewModels;

public sealed class MainViewModel : ObservableModel, IAsyncDisposable
{
    private readonly IDesktopApplication application;
    private readonly IUiDispatcher dispatcher;
    private readonly IDesktopDialogs dialogs;
    private readonly AppDiagnostics? diagnostics;
    private DesktopSnapshot state = new(0, null, false, false, null, null);
    private long sequence = -1;
    private bool busy, disposed;
    private Task? disposal;
    private string globalError = "";
    public ObservableCollection<FolderViewModel> Folders { get; } = [];
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) RefreshCommands(); } }
    public string ServerUrl => state.Settings?.ServerUrl ?? "未設定";
    public string CredentialText => state.CredentialsConfigured ? "Credentials configured" : "Credentials missing / unavailable";
    public string ManagerText => state.Manager is null ? "Automatic uploads are not running" : state.Manager.IsPaused ? "Paused" : state.Manager.RunningRequested ? "Running requested" : "Stopped";
    public string PauseLabel => state.Manager?.IsPaused == true ? "Resume All" : "Pause All";
    public bool IsPaused => state.Manager?.IsPaused == true;
    public bool NeedsAttention => state.Failure is not null || !state.CredentialsConfigured;
    public string StartupText => UiText.Startup(state.Startup, state.Settings?.StartWithWindows ?? false);
    public string ConnectionText => UiText.Connection(state.Connection?.Status ?? ConnectionStatus.Unknown);
    public string ConnectionCheckedText => "Last checked: " + (state.Connection?.LastCheckedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—");
    public bool CanRestore => state.CanRestoreBackup && !IsBusy;
    public string GlobalError { get => globalError; private set { if (Set(ref globalError, value)) Notify(nameof(HasGlobalError)); } }
    public bool HasGlobalError => GlobalError.Length != 0;
    public bool HasCliError => state.Manager?.Folders.Any(folder => folder.Session.Status == UploadSessionStatus.Error &&
        folder.Session.LastError?.BackendCode is BackendErrorCode.LauncherNotFound or BackendErrorCode.UnsupportedCli) == true;
    public string CliError => "Immich CLI を利用できません。PATH と CLI のバージョンを確認し、該当フォルダの Restart を実行してください。";
    public AsyncCommand AddCommand { get; }
    public AsyncCommand SettingsCommand { get; }
    public AsyncCommand PauseResumeCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public AsyncCommand OpenFolderCommand { get; }
    public AsyncCommand OpenLogsCommand { get; }
    public AsyncCommand DiagnosticsCommand { get; }
    public bool HasLoggingWarning => state.Diagnostics?.Logging is { FileUnavailable: true } or { DroppedEvents: > 0 };
    public string LoggingWarning => "ログの保存失敗または欠落が発生しています。アップロードの操作は引き続き利用できます。";

    public MainViewModel(IDesktopApplication application, IUiDispatcher dispatcher, IDesktopDialogs dialogs, AppDiagnostics? diagnostics = null)
    {
        this.application = application; this.dispatcher = dispatcher; this.dialogs = dialogs; this.diagnostics = diagnostics;
        AddCommand = new(() => BusyAsync(async () =>
        {
            var path = await dialogs.PickFolderAsync(); if (path is null) return;
            await dialogs.EditFolderAsync(CreateEditor(UploadFolderSettings.Create(path)));
        }), ShowError, () => !disposed && !IsBusy && state.Settings is not null && state.CredentialsConfigured);
        SettingsCommand = new(() => BusyAsync(async () =>
        {
            using var draft = new SettingsViewModel(state.Settings ?? new AppSettings(), state.CredentialsConfigured,
                async (settings, credentials) =>
                {
                    try { await application.SaveSettingsAsync(settings, credentials); }
                    catch { diagnostics?.Emit(AppEventKind.UiSettingsSaveFailed); throw; }
                    diagnostics?.Emit(AppEventKind.UiSettingsSaved); Apply(application.Snapshot);
                }, state.Startup, () => (application.Snapshot.Startup, application.Snapshot.Settings?.StartWithWindows ?? false));
            await dialogs.EditSettingsAsync(draft);
        }), ShowError, () => !disposed && !IsBusy);
        PauseResumeCommand = new(() => BusyAsync(async () =>
        {
            if (state.Manager?.IsPaused == true) await application.ResumeAsync(); else await application.PauseAsync();
            diagnostics?.Emit(AppEventKind.UiPauseResume); Apply(application.Snapshot);
        }), ShowError, () => !disposed && !IsBusy && state.Manager is not null);
        RestoreCommand = new(() => BusyAsync(async () =>
        { if (await dialogs.ConfirmRestoreAsync()) { await application.RestoreBackupAsync(); Apply(application.Snapshot); } }), ShowError,
            () => !disposed && CanRestore);
        OpenFolderCommand = new(() => dialogs.OpenSettingsFolderAsync(), ShowError, () => !disposed);
        OpenLogsCommand = new(() => dialogs.OpenLogsFolderAsync(), ShowError, () => !disposed);
        DiagnosticsCommand = new(() => state.Diagnostics is { } summary ? dialogs.ShowDiagnosticsAsync(summary) : Task.CompletedTask,
            ShowError, () => !disposed && state.Diagnostics is not null);
        application.Changed += OnSnapshot;
        Apply(application.Snapshot);
    }
    public Task InitializeAsync() => BusyAsync(async () =>
    {
        try
        {
            await application.InitializeAsync(); Apply(application.Snapshot);
            if (state.Failure is not null) diagnostics?.Emit(AppEventKind.UiInitializationFailed);
        }
        catch (Exception e) { ShowError(e); diagnostics?.Emit(AppEventKind.UiInitializationFailed); }
    });
    private void OnSnapshot(DesktopSnapshot next) => dispatcher.Enqueue(() => { if (!disposed) Apply(next); });
    private void Apply(DesktopSnapshot next)
    {
        if (disposed || next.Sequence <= sequence) return;
        var failureChanged = sequence < 0 || next.Failure != state.Failure;
        sequence = next.Sequence; state = next;
        if (failureChanged) GlobalError = UiText.Failure(next.Failure);
        var folders = next.Settings?.Folders ?? [];
        foreach (var removed in Folders.Where(row => !folders.Any(f => f.Id == row.Id)).ToArray())
        { removed.Dispose(); Folders.Remove(removed); }
        for (var index = 0; index < folders.Length; index++)
        {
            var folder = folders[index]; var row = Folders.FirstOrDefault(f => f.Id == folder.Id);
            if (row is null) { row = new(this, folder); Folders.Insert(index, row); }
            else if (Folders.IndexOf(row) != index) Folders.Move(Folders.IndexOf(row), index);
            row.Update(folder, next.Manager?.Folders.FirstOrDefault(f => f.Folder.Id == folder.Id)?.Session);
        }
        foreach (var property in new[] { nameof(ServerUrl), nameof(CredentialText), nameof(ManagerText), nameof(PauseLabel), nameof(CanRestore), nameof(StartupText), nameof(ConnectionText), nameof(ConnectionCheckedText), nameof(HasLoggingWarning), nameof(HasCliError) }) Notify(property);
        RefreshCommands();
    }
    private FolderEditorViewModel CreateEditor(UploadFolderSettings folder) => new(state.Settings!, folder, async replacement =>
    {
        var existing = state.Settings!;
        var added = !existing.Folders.Any(f => f.Id == replacement.Id);
        var folders = added ? existing.Folders.Add(replacement) : existing.Folders.Select(f => f.Id == replacement.Id ? replacement : f).ToImmutableArray();
        await application.SaveAsync(existing with { Folders = folders });
        diagnostics?.Emit(added ? AppEventKind.UiFolderAdded : AppEventKind.UiFolderEdited, replacement.Id);
        Apply(application.Snapshot);
    }, dialogs.PickFolderAsync);
    internal Task EditAsync(FolderViewModel row) => BusyAsync(() => dialogs.EditFolderAsync(CreateEditor(row.Settings)));
    internal Task RemoveAsync(FolderViewModel row) => BusyAsync(async () =>
    {
        if (!await dialogs.ConfirmRemoveAsync(row.DisplayName)) return;
        await application.SaveAsync(state.Settings! with { Folders = [.. state.Settings!.Folders.Where(f => f.Id != row.Id)] });
        diagnostics?.Emit(AppEventKind.UiFolderRemoved, row.Id); Apply(application.Snapshot);
    });
    internal Task ToggleAsync(FolderViewModel row) => BusyAsync(async () =>
    {
        await application.SaveAsync(state.Settings! with { Folders = [.. state.Settings!.Folders.Select(f => f.Id == row.Id ? f with { Enabled = !f.Enabled } : f)] });
        Apply(application.Snapshot);
    });
    internal Task RestartAsync(FolderViewModel row) => BusyAsync(async () =>
    { await application.RestartAsync(row.Id); diagnostics?.Emit(AppEventKind.UiRestart, row.Id); Apply(application.Snapshot); });
    internal bool CanOperate => !disposed && !IsBusy && state.CredentialsConfigured;
    internal bool CanRestart => CanOperate && state.Manager?.IsPaused != true;
    private async Task BusyAsync(Func<Task> action)
    {
        if (disposed || IsBusy) return;
        IsBusy = true;
        GlobalError = UiText.Failure(state.Failure);
        try { await action(); }
        finally { IsBusy = false; }
    }
    internal void ShowError(Exception error) { GlobalError = UiText.Error(error); diagnostics?.Emit(AppEventKind.UiActionFailed); diagnostics?.UiFailure(error); }
    private void RefreshCommands()
    {
        AddCommand?.Refresh(); SettingsCommand?.Refresh(); PauseResumeCommand?.Refresh(); RestoreCommand?.Refresh(); OpenFolderCommand?.Refresh();
        OpenLogsCommand?.Refresh(); DiagnosticsCommand?.Refresh();
        Notify(nameof(CanRestore)); foreach (var row in Folders) row.Refresh();
    }
    public ValueTask DisposeAsync()
    {
        if (disposal is not null) return new(disposal);
        disposed = true; application.Changed -= OnSnapshot;
        foreach (var row in Folders) row.Dispose(); RefreshCommands();
        disposal = application.DisposeAsync().AsTask();
        return new(disposal);
    }
}

public sealed class FolderViewModel : ObservableModel, IDisposable
{
    private readonly MainViewModel owner;
    private bool disposed;
    private SessionSnapshot? snapshot;
    internal UploadFolderSettings Settings { get; private set; }
    public Guid Id => Settings.Id;
    public string DisplayName => System.IO.Path.GetFileName(Settings.Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Settings.Path;
    public string Path => Settings.Path;
    public bool Enabled => Settings.Enabled;
    public string EnabledText => Enabled ? "Enabled" : "Disabled";
    public string ToggleLabel => Enabled ? "Disable" : "Enable";
    public string Album => "Album: " + (Settings.AlbumName ?? "—");
    public string Status => snapshot is { Status: UploadSessionStatus.Stopped, StopReason: SessionStopReason.Paused }
        ? "Paused" : UiText.Status(snapshot?.Status ?? UploadSessionStatus.Stopped);
    public string Retry => "Retry: " + (snapshot?.RetryCount ?? 0);
    public string Activity => "Last activity: " + (snapshot?.LastActivityAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—");
    public string Error => snapshot?.LastError is not { } error ? "" : (IsError ? "" : "Previous error: ") +
        (error.BackendCode switch
        {
            BackendErrorCode.LauncherNotFound => "Immich CLI was not found on PATH. Install the CLI and restart this folder.",
            BackendErrorCode.UnsupportedCli => "Immich CLI is incompatible. Check the CLI version and restart this folder.",
            BackendErrorCode.InvalidFolder => "The upload folder is unavailable. Check its path and access permissions.",
            _ => error.Summary
        }) + (IsError && snapshot.RetryExhausted ? " Automatic retries are exhausted. Check the connection and use Restart." :
            IsError && error.Retryable == false ? " Automatic retry is unavailable. Correct the configuration and use Restart." : "");
    public bool HasError => snapshot?.LastError is not null;
    public bool IsError => snapshot?.Status == UploadSessionStatus.Error;
    public AsyncCommand RestartCommand { get; }
    public AsyncCommand EditCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand ToggleCommand { get; }
    internal FolderViewModel(MainViewModel owner, UploadFolderSettings folder)
    {
        this.owner = owner; Settings = folder;
        RestartCommand = new(() => owner.RestartAsync(this), owner.ShowError, () => !disposed && Enabled && owner.CanRestart && snapshot?.Status is not (UploadSessionStatus.Starting or UploadSessionStatus.Restarting or UploadSessionStatus.Stopping));
        EditCommand = new(() => owner.EditAsync(this), owner.ShowError, () => !disposed && owner.CanOperate);
        RemoveCommand = new(() => owner.RemoveAsync(this), owner.ShowError, () => !disposed && owner.CanOperate);
        ToggleCommand = new(() => owner.ToggleAsync(this), owner.ShowError, () => !disposed && owner.CanOperate);
    }
    internal void Update(UploadFolderSettings folder, SessionSnapshot? next)
    {
        Settings = folder; snapshot = next;
        foreach (var property in new[] { nameof(DisplayName), nameof(Path), nameof(Enabled), nameof(EnabledText), nameof(ToggleLabel), nameof(Album), nameof(Status), nameof(Retry), nameof(Activity), nameof(Error), nameof(HasError), nameof(IsError) }) Notify(property);
        Refresh();
    }
    internal void Refresh() { RestartCommand.Refresh(); EditCommand.Refresh(); RemoveCommand.Refresh(); ToggleCommand.Refresh(); }
    public void Dispose() { disposed = true; Refresh(); }
}
