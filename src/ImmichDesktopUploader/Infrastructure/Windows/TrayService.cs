using System.Runtime.InteropServices;
using ImmichDesktopUploader.Application;
namespace ImmichDesktopUploader.Infrastructure.Windows;

// All methods and Win32 callbacks run on the owner HWND's UI thread.
public sealed class TrayService : ITrayService
{
    private const uint CallbackMessage = 0x8000 + 72;
    private readonly nint window;
    private readonly SubclassProc callback;
    private readonly uint taskbarCreated;
    private NotifyData data;
    private bool disposed, paused, canPause, exiting;
    private readonly AppDiagnostics? diagnostics;
    public event Action<TrayAction>? Invoked;
    public TrayService(nint window, AppDiagnostics? diagnostics = null)
    {
        this.diagnostics = diagnostics;
        this.window = window; callback = WindowProc;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        data = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = window, Id = 1,
            Flags = 1 | 2 | 4 | 0x80, Callback = CallbackMessage, Icon = LoadIcon(0, (nint)32512), Tip = "Immich Desktop Uploader",
            Info = "", InfoTitle = "", Guid = Guid.Empty };
        if (!SetWindowSubclass(window, callback, 0x494d, 0)) throw new InvalidOperationException("Tray callback installation failed.");
        diagnostics?.Emit(AppEventKind.TrayCreated);
    }
    public bool EnsureIcon()
    {
        if (disposed || exiting) return false;
        if (ShellNotifyIcon(1, ref data)) return true;
        if (!ShellNotifyIcon(0, ref data)) { diagnostics?.Emit(AppEventKind.TrayFailed); return false; }
        data.Version = 4; ShellNotifyIcon(4, ref data); diagnostics?.Emit(AppEventKind.TrayInitialized); return true;
    }
    public void Update(bool paused, bool canPause, bool exiting)
    { this.paused = paused; this.canPause = canPause; this.exiting = exiting; }
    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        try
        {
            if (message == taskbarCreated && !disposed && !exiting)
            {
                diagnostics?.Emit(AppEventKind.ExplorerRestartDetected);
                if (!EnsureIcon()) Invoked?.Invoke(TrayAction.Unavailable);
                else diagnostics?.Emit(AppEventKind.TrayReregistered);
                return 0;
            }
            if (message == 0x11) return 1; // WM_QUERYENDSESSION: never delay/veto Windows logoff.
            if (message == 0x16 && wParam != 0) { Invoked?.Invoke(TrayAction.SessionEnding); return 0; }
            if (message == CallbackMessage && !disposed)
            {
                var notification = (uint)((long)lParam & 0xffff);
                if (notification is 0x400 or 0x401 or 0x203)
                { diagnostics?.Emit(AppEventKind.TrayOpen); Invoked?.Invoke(TrayAction.Open); }
                else if (notification is 0x7b or 0x205) ShowMenu();
                return 0;
            }
        }
        catch { /* Never let a managed exception cross the native window procedure. */ }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    private void ShowMenu()
    {
        var menu = CreatePopupMenu(); if (menu == 0) return;
        try
        {
            AppendMenu(menu, exiting ? 1u : 0, 1, "Open");
            AppendMenu(menu, exiting || !canPause ? 1u : 0, 2, paused ? "Resume All" : "Pause All");
            AppendMenu(menu, 0x800, 0, "");
            AppendMenu(menu, exiting ? 1u : 0, 3, "Exit");
            GetCursorPos(out var point); SetForegroundWindow(window);
            var command = TrackPopupMenuEx(menu, 0x100 | 0x2, point.X, point.Y, window, 0);
            diagnostics?.Emit(command switch { 1 => AppEventKind.TrayOpen, 2 => AppEventKind.TrayPauseResume, 3 => AppEventKind.TrayExit, _ => AppEventKind.TrayMenuCancelled });
            PostMessage(window, 0, 0, 0);
            if (command != 0 && !exiting) Invoked?.Invoke(command switch { 1 => TrayAction.Open, 2 => TrayAction.PauseResume, _ => TrayAction.Exit });
        }
        finally { DestroyMenu(menu); }
    }
    public static void RestoreAndForeground(nint hwnd)
    { ShowWindow(hwnd, IsIconic(hwnd) ? 9 : 5); SetForegroundWindow(hwnd); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        ShellNotifyIcon(2, ref data); RemoveWindowSubclass(window, callback, 0x494d); Invoked = null;
        // LoadIcon's shared system icon is not owned and must not be destroyed.
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint id, nuint reference);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyData data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint reference);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nuint wParam, nint lParam);
}

public sealed class UnavailableTrayService : ITrayService
{
    public event Action<TrayAction>? Invoked { add { } remove { } }
    public bool EnsureIcon() => false;
    public void Update(bool paused, bool canPause, bool exiting) { }
    public void Dispose() { }
}
