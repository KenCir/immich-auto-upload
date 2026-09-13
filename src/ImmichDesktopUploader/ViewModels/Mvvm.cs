using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Immich;

namespace ImmichDesktopUploader.ViewModels;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
}

public sealed class AsyncCommand(Func<Task> execute, Action<Exception> failed, Func<bool>? canExecute = null) : ObservableModel, ICommand
{
    private bool busy;
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) Refresh(); } }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !IsBusy && (canExecute?.Invoke() ?? true);
    [System.Text.Json.Serialization.JsonIgnore] public Task Execution { get; private set; } = Task.CompletedTask;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public Task ExecuteAsync()
    {
        if (!CanExecute(null)) return Task.CompletedTask;
        IsBusy = true;
        return Execution = RunAsync();
    }
    private async Task RunAsync()
    {
        try { await execute(); }
        catch (Exception e) { failed(e); }
        finally { IsBusy = false; }
    }
    public async void Execute(object? parameter) => await ExecuteAsync();
}

public interface IUiDispatcher { bool Enqueue(Action action); }
public interface IDesktopDialogs
{
    Task<string?> PickFolderAsync();
    Task EditFolderAsync(FolderEditorViewModel draft);
    Task EditSettingsAsync(SettingsViewModel draft);
    Task<bool> ConfirmRemoveAsync(string name);
    Task<bool> ConfirmRestoreAsync();
    Task OpenSettingsFolderAsync();
}

public static class UiText
{
    public static string Status(UploadSessionStatus status) => status switch
    {
        UploadSessionStatus.Stopped => "Stopped", UploadSessionStatus.Starting => "Starting…",
        UploadSessionStatus.Running => "Running", UploadSessionStatus.Restarting => "Restarting…",
        UploadSessionStatus.Stopping => "Stopping…", _ => "Error"
    };
    public static string Failure(AppFailure? failure) => failure switch
    {
        null => "", AppFailure.MissingSettings => "初期設定が必要です。SettingsからサーバーURLとAPI Keyを設定してください。",
        AppFailure.CorruptSettings or AppFailure.InvalidSettings => "設定ファイルが不正です。自動アップロードを開始できません。バックアップまたは設定フォルダを確認してください。",
        AppFailure.UnsupportedSchema => "この設定は未対応のバージョンです。上書きせず、対応するアプリで開いてください。",
        AppFailure.MissingCredentials => "API Keyが未設定です。Settingsから入力してください。",
        AppFailure.InvalidCredentials => "保存済みAPI Keyを復号できません。Settingsから再入力してください。",
        AppFailure.CredentialMismatch => "サーバーURLと保存済み資格情報が一致しません。SettingsからAPI Keyを再入力してください。",
        AppFailure.StorageFailure => "保存または読み込みに失敗しました。アクセス権・空き容量・ファイルの使用状況を確認してください。",
        AppFailure.CleanupFailed => "プロセスの終了を確認できませんでした。新しい起動は保留されています。",
        AppFailure.DisabledOrPaused => "無効または一時停止中です。有効化・Resume後に操作してください。",
        _ => "操作を完了できませんでした。設定を確認してください。"
    };
    public static string Error(Exception error) => error switch
    {
        DraftValidationException e => e.Message,
        AppOperationException e => Failure(e.Failure),
        UploadBackendException e when e.ErrorCode == BackendErrorCode.InvalidServerUrl => "Server URLはuserinfo・query・fragmentのないHTTP/HTTPSの絶対URLにしてください。",
        UploadBackendException e when e.ErrorCode == BackendErrorCode.MissingApiKey => "有効なAPI Keyを入力してください。",
        _ => "操作を完了できませんでした。入力内容または保存先を確認してください。"
    };
}
internal sealed class DraftValidationException(string safeMessage) : Exception(safeMessage);
