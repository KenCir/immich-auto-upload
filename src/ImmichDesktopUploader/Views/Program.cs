using ImmichDesktopUploader.Infrastructure.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

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
        // A synchronous entry point is essential: async Main can lose STA/UI Automation support.
        Microsoft.UI.Xaml.Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(instance, args);
        });
        return 0;
    }
}
