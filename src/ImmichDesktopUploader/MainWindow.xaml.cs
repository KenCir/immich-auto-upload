using ImmichDesktopUploader.Application;
using ImmichDesktopUploader.Infrastructure.Persistence;
using ImmichDesktopUploader.Infrastructure.Windows;
using ImmichDesktopUploader.ViewModels;
using ImmichDesktopUploader.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Microsoft.Extensions.Logging;
using ImmichDesktopUploader.Infrastructure.Logging;

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
    private readonly FileLogging logging;
    private readonly ILogger logger;
    public MainWindow(AppStoragePaths paths, bool smoke, Action shutdownFence, FileLogging logging, Func<Task>? testExitCheckpoint = null)
    {
        InitializeComponent(); this.paths = paths; this.shutdownFence = shutdownFence; this.testExitCheckpoint = testExitCheckpoint;
        this.logging = logging; logger = logging.Factory.CreateLogger("Desktop");
        var diagnostics = new AppDiagnostics(logging.Factory.CreateLogger("ApplicationEvents"), logging.Secrets.Register);
        model = new(new DesktopApplicationService(paths, diagnostics: diagnostics,
            startup: new StartupService(Environment.ProcessPath!, smoke ? new SmokeTestProfile.StartupStore() : null, diagnostics),
            probeFactory: smoke ? _ => new SmokeTestProfile.Probe(paths) : null,
            probeClock: smoke ? new SmokeTestProfile.ProbeClock() : null,
            loggingStatus: () => new(logging.Health.FileUnavailable, logging.Health.DroppedEvents)), new QueueDispatcher(DispatcherQueue), this, diagnostics);
        Root.DataContext = model;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 820));
        AppWindow.Closing += OnClosing;
        ITrayService tray;
        try { tray = new TrayService(WinRT.Interop.WindowNative.GetWindowHandle(this), diagnostics); }
        catch { diagnostics.Emit(AppEventKind.TrayFailed); tray = new UnavailableTrayService(); }
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
        logger.LogInformation("{EventName}", "ShutdownStarted");
        var pendingSave = activeDialog switch { FolderEditorDialog f => f.PendingSave, SettingsDialog s => s.PendingSave, _ => Task.CompletedTask };
        activeDialog?.Hide();
        // Fence the application now, before waiting for an already accepted atomic save.
        var cleanup = model.DisposeAsync().AsTask();
        await pendingSave;
        await cleanup;
        if (testExitCheckpoint is not null) await testExitCheckpoint();
        logger.LogInformation("{EventName}", "ShutdownCompleted");
        await logging.DisposeAsync();
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
        logger.LogInformation("{EventName}", "WindowShown");
    }
    public void Hide() { AppWindow.Hide(); logger.LogInformation("{EventName}", "WindowHidden"); }
    public void DisableInteraction()
    {
        closing = true; shutdownFence(); InteractionContainer.IsEnabled = false;
        logger.LogInformation("{EventName}", "GracefulExitRequested");
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
    public async Task OpenLogsFolderAsync()
    {
        await Task.Run(() => Directory.CreateDirectory(logging.LogsPath));
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(logging.LogsPath);
        await Windows.System.Launcher.LaunchFolderAsync(folder);
    }
    public async Task ShowDiagnosticsAsync(DiagnosticSummary summary)
    {
        var text = $"App version: {summary.AppVersion}\nImmich CLI version (last observed): {summary.Cli.Version ?? "Not checked"}\n" +
            $"Launcher: {summary.Cli.LauncherPath ?? "Not checked"}\nServer URL: {summary.ServerUrl}\n" +
            $"Credentials configured: {summary.CredentialConfigured}\nFolders: {summary.Folders}\nRunning: {summary.RunningSessions}\n" +
            $"Error sessions: {summary.ErrorSessions}\nPaused: {summary.Paused}\n{UiText.Connection(summary.Connection.Status)}\n" +
            $"Last connection check: {summary.Connection.LastCheckedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—"}\n" +
            $"Logs: {summary.LogsPath}\nLogging unavailable: {summary.Logging.FileUnavailable}\nDropped log events: {summary.Logging.DroppedEvents}";
        var content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(content, "DiagnosticSummary");
        await ShowDialogAsync(new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Diagnostics", CloseButtonText = "Close diagnostics",
            Content = new ScrollViewer { Content = content, MaxHeight = 480 } });
    }
}
