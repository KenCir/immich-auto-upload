using Microsoft.UI.Xaml;
namespace ImmichDesktopUploader;
public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? window;
    private readonly Infrastructure.Windows.SingleInstanceService instance;
    private readonly string[] arguments;
    internal App(Infrastructure.Windows.SingleInstanceService instance, string[] arguments)
    { this.instance = instance; this.arguments = arguments; InitializeComponent(); }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Smoke runs always get an isolated, newly created directory; never load personal uploads.
        var sampleFolders = arguments.Contains("--smoke-test-folders");
        var ready = arguments.Contains("--smoke-test-ready");
        var smoke = arguments.Contains("--smoke-test") || sampleFolders || ready;
        var paths = smoke ? await Views.SmokeTestProfile.CreateAsync(sampleFolders, ready) : new Infrastructure.Persistence.AppStoragePaths();
        window = new MainWindow(paths, smoke, instance.Dispose,
            smoke ? () => Views.SmokeTestProfile.ExitCheckpointAsync(paths) : null);
        await window.InitializeResidentAsync(arguments.Contains("--background"));
        instance.Bind(() => window.DispatcherQueue.TryEnqueue(window.OpenResident));
    }
}
