using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Persistence;
using ImmichDesktopUploader.Infrastructure.Windows;
using ImmichDesktopUploader.ViewModels;
using ImmichDesktopUploader.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ImmichDesktopUploader;

public sealed partial class MainWindow : Window, IDesktopDialogs, IResidentWindow
{
    private readonly MainViewModel model;
    private readonly AppStoragePaths paths;
    private readonly ResidentLifetime resident;
    private readonly Action shutdownFence;
    private readonly Func<Task>? testExitCheckpoint;
    private bool closing, closeApproved;
    private ContentDialog? activeDialog;
    public MainWindow(AppStoragePaths paths, bool smoke, Action shutdownFence, Func<Task>? testExitCheckpoint = null)
    {
        InitializeComponent(); this.paths = paths; this.shutdownFence = shutdownFence; this.testExitCheckpoint = testExitCheckpoint;
        var diagnostics = new AppDiagnostics();
        model = new(new DesktopApplicationService(paths, diagnostics: diagnostics,
            startup: new StartupService(Environment.ProcessPath!, smoke ? new SmokeTestProfile.StartupStore() : null)), new QueueDispatcher(DispatcherQueue), this, diagnostics);
        Root.DataContext = model;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 820));
        AppWindow.Closing += OnClosing;
        ITrayService tray;
        try { tray = new TrayService(WinRT.Interop.WindowNative.GetWindowHandle(this)); }
        catch { tray = new UnavailableTrayService(); }
        resident = new(this, tray, ShutdownAsync, () => model.PauseResumeCommand.ExecuteAsync());
        model.PropertyChanged += OnModelChanged;
    }
    public async Task InitializeResidentAsync(bool background)
    {
        await model.InitializeAsync();
        resident.Update(model.IsPaused, model.PauseResumeCommand.CanExecute(null));
        resident.Initialize(background, model.NeedsAttention);
    }
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) =>
        resident.Update(model.IsPaused, model.PauseResumeCommand.CanExecute(null));
    public void OpenResident() => resident.Open();
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeApproved) return;
        args.Cancel = true;
        resident.CloseRequested();
    }
    private async Task ShutdownAsync()
    {
        var pendingSave = activeDialog switch { FolderEditorDialog f => f.PendingSave, SettingsDialog s => s.PendingSave, _ => Task.CompletedTask };
        activeDialog?.Hide();
        // Fence the application now, before waiting for an already accepted atomic save.
        var cleanup = model.DisposeAsync().AsTask();
        await pendingSave;
        await cleanup;
        if (testExitCheckpoint is not null) await testExitCheckpoint();
    }
    private async void OnExit(object sender, RoutedEventArgs args)
    {
        try { await resident.ExitAsync(); }
        catch { /* ResidentLifetime keeps the failure visible and operations fenced. */ }
    }
    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        Activate(); TrayService.RestoreAndForeground(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }
    public void Hide() => AppWindow.Hide();
    public void DisableInteraction()
    {
        closing = true; shutdownFence(); InteractionContainer.IsEnabled = false;
        model.PropertyChanged -= OnModelChanged;
    }
    public void ReportLifetimeError(string safeMessage)
    { LifetimeError.Message = safeMessage; LifetimeError.IsOpen = true; }
    public void FinishExit()
    {
        closeApproved = true; AppWindow.Closing -= OnClosing; Close();
        Microsoft.UI.Xaml.Application.Current.Exit();
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
