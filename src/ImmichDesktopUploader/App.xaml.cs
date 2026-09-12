using Microsoft.UI.Xaml;
namespace ImmichDesktopUploader;
public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
