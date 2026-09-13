using Microsoft.Extensions.Logging;

namespace ImmichDesktopUploader.Infrastructure.Logging;

// Observes faults without marking them handled or changing the application's fatal policy.
public sealed class CrashDiagnostics : IDisposable
{
    private readonly FileLogging logging;
    private readonly ILogger logger;
    public CrashDiagnostics(FileLogging logging)
    {
        this.logging = logging; logger = logging.Factory.CreateLogger("CrashDiagnostics");
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }
    private void OnUnhandled(object sender, UnhandledExceptionEventArgs args) =>
        Record("AppDomainUnhandledException", args.ExceptionObject as Exception, args.IsTerminating);
    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args) =>
        Record("UnobservedTaskException", args.Exception, false);
    public void Record(string eventName, Exception? exception, bool fatal)
    {
        try
        {
            // Message / Data may include decrypted payloads. Type, HRESULT and stack are
            // sufficient to locate the failure; final file formatting still redacts secrets.
            logger.Log(fatal ? LogLevel.Critical : LogLevel.Error,
                "{EventName} Type={ExceptionType} HResult={HResult} Stack={StackTrace} Fatal={Fatal}",
                eventName, exception?.GetType().FullName, exception?.HResult, exception?.StackTrace, fatal);
            if (fatal) logging.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch { System.Diagnostics.Debug.WriteLine("Immich Desktop Uploader: crash diagnostic could not be written."); }
    }
    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }
}
