using Microsoft.UI.Xaml;
namespace ImmichDesktopUploader;
public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Smoke runs always get an isolated, newly created directory; never load personal uploads.
        var arguments = Environment.GetCommandLineArgs();
        var sampleFolders = arguments.Contains("--smoke-test-folders");
        var smoke = arguments.Contains("--smoke-test") || sampleFolders;
        var paths = smoke ? await Views.SmokeTestProfile.CreateAsync(sampleFolders) : new Infrastructure.Persistence.AppStoragePaths();
        window = new MainWindow(paths);
        window.Activate();
    }
}
