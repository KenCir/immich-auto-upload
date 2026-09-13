#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using ImmichDesktopUploader.Application;

namespace ImmichDesktopUploader.Infrastructure.Windows;

// Constructed before XAML, persistence, or the upload composition root.
internal sealed class SingleInstanceService : IDisposable
{
    private readonly AppInstance instance;
    private readonly object sync = new();
    private Action? open;
    private bool pendingOpen, closing;
    private AppDiagnostics? diagnostics;
    public void AttachDiagnostics(AppDiagnostics value)
    {
        lock (sync)
        {
            diagnostics = value; diagnostics.Emit(AppEventKind.PrimaryInstance);
            if (pendingOpen) diagnostics.Emit(AppEventKind.ActivationRedirected);
        }
    }
    public bool IsPrimary => instance.IsCurrent;
    public SingleInstanceService(string key)
    {
        instance = AppInstance.FindOrRegisterForKey(key);
        if (IsPrimary) instance.Activated += OnActivated;
    }
    public void Redirect()
    {
        AllowSetForegroundWindow(instance.ProcessId);
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var completed = new ManualResetEvent(false);
        var redirect = Task.Run(async () =>
        {
            try { await instance.RedirectActivationToAsync(args); }
            finally { completed.Set(); }
        });
        // Pump COM calls on the secondary STA while redirect completes on an MTA worker.
        Marshal.ThrowExceptionForHR(CoWaitForMultipleObjects(0, uint.MaxValue, 1,
            [completed.SafeWaitHandle.DangerousGetHandle()], out _));
        redirect.GetAwaiter().GetResult();
    }
    private void OnActivated(object? sender, AppActivationArguments args)
    {
        var background = args.Data is ILaunchActivatedEventArgs launch &&
            launch.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("--background");
        if (background) { diagnostics?.Emit(AppEventKind.BackgroundActivation); return; }
        lock (sync)
        {
            if (closing) return;
            diagnostics?.Emit(AppEventKind.ActivationRedirected);
            if (open is null) pendingOpen = true;
            else open();
        }
    }
    public void Bind(Action dispatchOpen)
    {
        lock (sync)
        {
            if (closing) return;
            open = dispatchOpen;
            if (pendingOpen) { pendingOpen = false; open(); }
        }
    }
    public void Dispose()
    {
        lock (sync) { closing = true; open = null; }
        if (IsPrimary) instance.Activated -= OnActivated;
        // Keep the registration until process exit, including throughout async cleanup.
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("ole32.dll")] private static extern int CoWaitForMultipleObjects(uint flags, uint timeout,
        uint count, nint[] handles, out uint index);
}
#endif
