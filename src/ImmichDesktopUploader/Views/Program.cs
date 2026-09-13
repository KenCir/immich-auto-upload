using ImmichDesktopUploader.Infrastructure.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.Logging;
using ImmichDesktopUploader.Infrastructure.Logging;
using ImmichDesktopUploader.Infrastructure.Persistence;

namespace ImmichDesktopUploader;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Explicit smoke runs never redirect into a user's personal running instance.
        var smokeKey = args.FirstOrDefault(a => a.StartsWith("--smoke-instance=", StringComparison.Ordinal));
        var smoke = args.Any(a => a is "--smoke-test" or "--smoke-test-folders" or "--smoke-test-ready");
        var key = smoke ? "ImmichDesktopUploader.Smoke." +
            (Guid.TryParse(smokeKey?["--smoke-instance=".Length..], out var group) ? group : Guid.NewGuid()).ToString("N") : "ImmichDesktopUploader";
        using var instance = new SingleInstanceService(key);
        if (!instance.IsPrimary)
        {
            try { instance.Redirect(); return 0; }
            catch (Exception error) { return error.HResult != 0 ? error.HResult : 1; } // Never initialize a second manager on redirect failure.
        }
        // Only the elected primary creates the long-lived file sink or its storage profile.
        var paths = smoke ? new AppStoragePaths(Path.Combine(Path.GetTempPath(), "ImmichGuiSmoke-" + Guid.NewGuid().ToString("N"))) : new AppStoragePaths();
        var logging = new FileLogging(Path.Combine(paths.DirectoryPath, "logs"));
        using var crash = new CrashDiagnostics(logging);
        var logger = logging.Factory.CreateLogger("Application");
        instance.AttachDiagnostics(new Application.AppDiagnostics(logger, logging.Secrets.Register));
        logger.LogInformation("{EventName} Primary={IsPrimary} Background={Background} AppVersion={AppVersion}",
            "AppStarted", true, args.Contains("--background"), typeof(App).Assembly.GetName().Version?.ToString());
        // A synchronous entry point is essential: async Main can lose STA/UI Automation support.
        try
        {
            Microsoft.UI.Xaml.Application.Start(parameters =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App(instance, args, paths, logging, crash);
            });
        }
        finally { logging.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return 0;
    }
}
