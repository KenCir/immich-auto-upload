using Microsoft.UI.Xaml;
using ImmichDesktopUploader.Infrastructure.Logging;
using ImmichDesktopUploader.Infrastructure.Persistence;
namespace ImmichDesktopUploader;
public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? window;
    private readonly Infrastructure.Windows.SingleInstanceService instance;
    private readonly string[] arguments;
    private readonly AppStoragePaths paths;
    private readonly FileLogging logging;
    internal App(Infrastructure.Windows.SingleInstanceService instance, string[] arguments, AppStoragePaths paths, FileLogging logging, CrashDiagnostics crash)
    {
        this.instance = instance; this.arguments = arguments; this.paths = paths; this.logging = logging;
        UnhandledException += (_, args) => crash.Record("WinUiUnhandledException", args.Exception, true);
        InitializeComponent();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Smoke runs always get an isolated, newly created directory; never load personal uploads.
        var sampleFolders = arguments.Contains("--smoke-test-folders");
        var ready = arguments.Contains("--smoke-test-ready");
        var smoke = arguments.Contains("--smoke-test") || sampleFolders || ready;
        if (smoke) await Views.SmokeTestProfile.CreateAsync(sampleFolders, ready, paths);
        window = new MainWindow(paths, smoke, instance.Dispose, logging,
            smoke ? () => Views.SmokeTestProfile.ExitCheckpointAsync(paths) : null);
        await window.InitializeResidentAsync(arguments.Contains("--background"));
        instance.Bind(() => window.DispatcherQueue.TryEnqueue(window.OpenResident));
    }
}
