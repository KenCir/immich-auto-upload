using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Persistence;
using ImmichDesktopUploader.ViewModels;
using ImmichDesktopUploader.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ImmichDesktopUploader;

public sealed partial class MainWindow : Window, IDesktopDialogs
{
    private readonly MainViewModel model;
    private readonly AppStoragePaths paths;
    private bool loaded, closing, closeApproved;
    private ContentDialog? activeDialog;
    public MainWindow(AppStoragePaths paths)
    {
        InitializeComponent(); this.paths = paths;
        var diagnostics = new AppDiagnostics();
        model = new(new DesktopApplicationService(paths, diagnostics: diagnostics), new QueueDispatcher(DispatcherQueue), this, diagnostics);
        Root.DataContext = model;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 820));
        AppWindow.Closing += OnClosing;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (loaded) return; loaded = true;
        await model.InitializeAsync();
    }
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeApproved) return;
        args.Cancel = true;
        if (closing) return; closing = true;
        Root.IsHitTestVisible = false;
        try
        {
            var pendingSave = activeDialog switch { FolderEditorDialog f => f.PendingSave, SettingsDialog s => s.PendingSave, _ => Task.CompletedTask };
            activeDialog?.Hide();
            await pendingSave;
            await model.DisposeAsync();
            closeApproved = true; AppWindow.Closing -= OnClosing; Close();
        }
        catch
        {
            // Keep the window visible on unconfirmed cleanup. Never claim a clean exit.
            Root.IsHitTestVisible = true;
            var error = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "終了処理を確認できませんでした",
                Content = "CLIプロセスの終了を確認できませんでした。プロセスの状態を確認してください。", CloseButtonText = "OK" };
            await error.ShowAsync();
        }
    }
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (closing) return ContentDialogResult.None;
        activeDialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally { if (ReferenceEquals(activeDialog, dialog)) activeDialog = null; }
    }
    public async Task EditFolderAsync(FolderEditorViewModel draft) => await ShowDialogAsync(new FolderEditorDialog(draft) { XamlRoot = Root.XamlRoot });
    public async Task EditSettingsAsync(SettingsViewModel draft) => await ShowDialogAsync(new SettingsDialog(draft) { XamlRoot = Root.XamlRoot });
    public async Task<bool> ConfirmRemoveAsync(string name) => await ShowDialogAsync(new ContentDialog
    {
        XamlRoot = Root.XamlRoot, Title = "自動アップロードから削除",
        Content = $"「{name}」を自動アップロード設定から外しますか？\nローカルファイルやImmichの画像は削除しません。",
        PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
    }) == ContentDialogResult.Primary;
    public async Task<bool> ConfirmRestoreAsync() => await ShowDialogAsync(new ContentDialog
    {
        XamlRoot = Root.XamlRoot, Title = "バックアップを復元",
        Content = "有効なバックアップを復元します。資格情報が一致すれば、Enabledのフォルダは自動アップロードを再開します。",
        PrimaryButtonText = "Restore", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
    }) == ContentDialogResult.Primary;
    public async Task OpenSettingsFolderAsync()
    {
        Directory.CreateDirectory(paths.DirectoryPath);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(paths.DirectoryPath);
        await Windows.System.Launcher.LaunchFolderAsync(folder);
    }
}
