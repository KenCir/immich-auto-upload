# Runs only from SmokeTest-WinUI.ps1 in Windows PowerShell (UIAutomation support).
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ResidentNative {
 public delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
 [StructLayout(LayoutKind.Sequential)] public struct IconId { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [StructLayout(LayoutKind.Sequential)] public struct MenuBar { public uint Size; public Rect Rect; public IntPtr Menu,Window; public uint Focus; }
 [DllImport("user32.dll")] static extern bool GetMenuBarInfo(IntPtr h, int obj, int item, ref MenuBar info);
 [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int count, uint flags);
 [DllImport("user32.dll")] static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
 [DllImport("user32.dll")] static extern bool GetMenuItemRect(IntPtr window, IntPtr menu, uint item, out Rect rect);
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
 [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref Point point);
 [ComImport, Guid("618736e0-3c3d-11cf-810c-00aa00389b71"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
 public interface MenuAccessible { [DispId(-5018)] void DoDefaultAction([MarshalAs(UnmanagedType.Struct)] object child); }
 [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr h, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out MenuAccessible accessible);
 [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconId id, out Rect rect);
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct NotifyData {
  public uint Size; public IntPtr Window; public uint Id,Flags,Callback; public IntPtr Icon;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
  public uint State,StateMask;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
  public uint Version;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string InfoTitle;
  public uint InfoFlags; public Guid Guid; public IntPtr Balloon;
 }
 [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconW",CharSet=CharSet.Unicode)] static extern bool Notify(uint m,ref NotifyData d);
 public static IntPtr Window(uint pid) {
  IntPtr found=IntPtr.Zero;
  EnumWindows((h,p)=> { uint owner; GetWindowThreadProcessId(h,out owner); var text=new StringBuilder(256); GetWindowText(h,text,256);
   if(owner==pid && text.ToString()=="Immich Desktop Uploader") found=h; return true; },IntPtr.Zero); return found;
 }
 public static bool HasIcon(IntPtr hwnd) { var id=new IconId { Size=(uint)Marshal.SizeOf(typeof(IconId)), Window=hwnd, Id=1 }; Rect r; return Shell_NotifyIconGetRect(ref id,out r)==0; }
 public static IntPtr Menu(uint pid) {
  IntPtr found=IntPtr.Zero;
  EnumWindows((h,p)=> { uint owner; GetWindowThreadProcessId(h,out owner); var text=new StringBuilder(256); GetClassName(h,text,256);
   if(owner==pid && text.ToString()=="#32768") found=h; return true; },IntPtr.Zero); return found;
 }
 public static bool InvokeMenu(uint pid, string name) {
  var h=Menu(pid); if(h==IntPtr.Zero) return false;
  var info=new MenuBar { Size=(uint)Marshal.SizeOf(typeof(MenuBar)) };
  if(!GetMenuBarInfo(h,-4,0,ref info)) return false;
  for(uint i=0; i<GetMenuItemCount(info.Menu); i++) {
   uint state=GetMenuState(info.Menu,i,0x400); if((state & 0x800)!=0) continue;
   var text=new StringBuilder(256); GetMenuString(info.Menu,i,text,256,0x400);
   if(text.ToString()==name) {
    if((state & 3)!=0) return false;
    var iid=new Guid("618736e0-3c3d-11cf-810c-00aa00389b71"); MenuAccessible accessible;
    if(AccessibleObjectFromWindow(h,0xfffffffc,ref iid,out accessible)!=0) return false;
    try { accessible.DoDefaultAction((int)i+1); }
    catch(COMException e) {
     if(e.ErrorCode != unchecked((int)0x80020003)) throw;
     // Posted mouse selection intermittently cancelled without executing the command.
     // Navigate the verified popup with keyboard
     // messages instead; no global keyboard injection or user cursor movement.
     PostMessage(h,0x100,new IntPtr(0x24),IntPtr.Zero); // Home
     PostMessage(h,0x101,new IntPtr(0x24),IntPtr.Zero);
     for(uint preceding=0; preceding<i; preceding++) {
      uint precedingState=GetMenuState(info.Menu,preceding,0x400);
      if((precedingState & (0x800 | 3))!=0) continue;
      PostMessage(h,0x100,new IntPtr(0x28),IntPtr.Zero); // Down
      PostMessage(h,0x101,new IntPtr(0x28),IntPtr.Zero);
     }
     PostMessage(h,0x100,new IntPtr(0x0d),IntPtr.Zero); // Enter
     PostMessage(h,0x101,new IntPtr(0x0d),IntPtr.Zero);
    }
    finally { Marshal.ReleaseComObject(accessible); }
    return true;
   }
  }
  return false;
 }
 public static void RemoveTestIcon(IntPtr hwnd) { var d=new NotifyData { Size=(uint)Marshal.SizeOf(typeof(NotifyData)),Window=hwnd,Id=1,Tip="",Info="",InfoTitle="" }; Notify(2,ref d); }
}
'@
function Wait-Resident($condition, [string]$failure) {
    $until = [DateTime]::UtcNow.AddSeconds(20)
    do { if (& $condition) { return }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $until)
    throw $failure
}
function Get-Control($window, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Invoke-TrayMenu($process, $handle, [string]$name) {
    # Deliver the real tray context-menu notification to this test HWND only.
    $null = [ResidentNative]::PostMessage($handle, 0x8048, [IntPtr]::Zero, [IntPtr]0x7b)
    # Inspect the real HMENU and invoke its own MSAA menu action.
    # Some WinUI/Windows combinations expose this native popup as an empty UIA Pane.
    Wait-Resident { [ResidentNative]::InvokeMenu($process.Id, $name) } "Tray menu item missing/disabled: $name"
}
function Invoke-ResidentChecks([string]$appPath) {
    $key = '--smoke-instance=' + [Guid]::NewGuid().ToString('N')
    $before = @(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'ImmichGuiSmoke-*' | ForEach-Object FullName)
    $primary = Start-Process $appPath -ArgumentList @('--smoke-test-ready','--background',$key) -WindowStyle Hidden -PassThru
    try {
        Wait-Resident { [ResidentNative]::Window($primary.Id) -ne [IntPtr]::Zero } 'Background HWND missing'
        $handle = [ResidentNative]::Window($primary.Id)
        Wait-Resident { [ResidentNative]::HasIcon($handle) } 'Tray icon not registered'
        if ([ResidentNative]::IsWindowVisible($handle)) { throw 'Background launch showed window' }
        $secondary = Start-Process $appPath -ArgumentList @('--smoke-test-ready','--background',$key) -WindowStyle Hidden -PassThru
        try { if (-not $secondary.WaitForExit(15000) -or $secondary.ExitCode -ne 0) { throw "Background redirect failed: $($secondary.ExitCode)" } }
        finally { if (-not $secondary.HasExited) { $secondary.Kill(); $secondary.WaitForExit() }; $secondary.Dispose() }
        if ([ResidentNative]::IsWindowVisible($handle)) { throw 'Background secondary unexpectedly opened window' }
        $secondary = Start-Process $appPath -ArgumentList @('--smoke-test-ready',$key) -WindowStyle Hidden -PassThru
        try { if (-not $secondary.WaitForExit(15000) -or $secondary.ExitCode -ne 0) { throw 'Normal redirect failed' } }
        finally { if (-not $secondary.HasExited) { $secondary.Kill(); $secondary.WaitForExit() }; $secondary.Dispose() }
        Wait-Resident { [ResidentNative]::IsWindowVisible($handle) } 'Normal secondary did not open primary'
        if ([ResidentNative]::Window($primary.Id) -ne $handle) { throw 'Window identity changed' }
        $created = @(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'ImmichGuiSmoke-*' | Where-Object { $_.FullName -notin $before })
        if ($created.Count -ne 1) { throw 'Secondary initialized another smoke storage profile' }
        $probeCountPath = Join-Path $created[0].FullName 'probe-count'
        Wait-Resident { (Test-Path -LiteralPath $probeCountPath) -and [int](Get-Content -LiteralPath $probeCountPath -Raw) -ge 2 } 'Primary monitor did not repeat probes'
        $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        Wait-Resident { (Get-Control $window 'ConnectionState').Current.Name -eq 'Connection check succeeded' } 'Connection check GUI missing'
        Wait-Resident { (Get-Control $window 'ManagerState').Current.Name -eq 'Running requested' } 'Ready manager missing'
        Wait-Resident { (Get-Control $window 'DiagnosticsButton').Current.IsEnabled } 'Diagnostics not available'
        (Get-Control $window 'DiagnosticsButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-Resident { $null -ne (Get-Control $window 'DiagnosticSummary') } 'Diagnostic summary missing'
        $diagnosticText = (Get-Control $window 'DiagnosticSummary').Current.Name
        if ($diagnosticText -notlike '*App version:*' -or $diagnosticText -notlike '*Logs:*' -or $diagnosticText -like '*isolated-smoke-key*') { throw 'Incorrect or unsafe diagnostic summary' }
        $diagnosticCloseCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Close diagnostics')
        $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$diagnosticCloseCondition).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-Resident { $null -eq (Get-Control $window 'DiagnosticSummary') } 'Diagnostics did not close'
        # Exercise the actual GUI command and verify the Shell's resulting folder.
        # Only close a newly created Explorer window owned by this isolated check.
        $shell = New-Object -ComObject Shell.Application
        $priorExplorerHandles = @($shell.Windows() | ForEach-Object { $_.HWND })
        $expectedLogs = Join-Path $created[0].FullName 'logs'
        $script:openedLogsWindow = $null
        try {
            (Get-Control $window 'OpenLogsButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Wait-Resident {
                foreach ($explorerWindow in $shell.Windows()) {
                    try {
                        if ($explorerWindow.Document.Folder.Self.Path -eq $expectedLogs) {
                            $script:openedLogsWindow = $explorerWindow
                            return $true
                        }
                    } catch { }
                }
                return $false
            } 'Open logs did not open the isolated logs folder in Explorer'
            Write-Output 'PASS Logging: Open logs GUI command opened the exact folder in Explorer'
        } finally {
            if ($script:openedLogsWindow -and $script:openedLogsWindow.HWND -notin $priorExplorerHandles) {
                $script:openedLogsWindow.Quit()
            }
            $script:openedLogsWindow = $null
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        }
        # Repeated real FolderPicker cancellation observes the reported native crash
        # without selecting or registering any user photo folder.
        for ($pickerAttempt = 0; $pickerAttempt -lt 3; $pickerAttempt++) {
            Wait-Resident { (Get-Control $window 'AddFolderButton').Current.IsEnabled } 'Add folder remained busy'
            (Get-Control $window 'AddFolderButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            $script:observedPicker = $null
            try { Wait-Resident {
                $byProcess = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$primary.Id)
                foreach ($candidate in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$byProcess)) {
                    if ($candidate.Current.ClassName -eq '#32770') { $script:observedPicker = $candidate; return $true }
                    $byDialogClass = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty,'#32770')
                    $childDialog = $candidate.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$byDialogClass)
                    if ($childDialog) { $script:observedPicker = $childDialog; return $true }
                }
                return $false
            } 'Native FolderPicker did not appear' } catch {
                Write-Output "Picker observation primary PID=$($primary.Id), exited=$($primary.HasExited)"
                throw
            }
            $cancelPicker = Get-Control $script:observedPicker '2'
            if (-not $cancelPicker) { throw 'FolderPicker Cancel button was not found' }
            $cancelPattern = $null
            if ($cancelPicker.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$cancelPattern)) {
                $cancelPattern.Invoke()
            } else {
                # The native common dialog exposes IDCANCEL but may omit InvokePattern.
                # Send its standard cancel command to the observed dialog only.
                $pickerHandle = [IntPtr]$script:observedPicker.Current.NativeWindowHandle
                if ($pickerHandle -eq [IntPtr]::Zero) { throw 'FolderPicker native handle missing' }
                $null = [ResidentNative]::PostMessage($pickerHandle, 0x111, [IntPtr]2, [IntPtr]::Zero)
            }
            Wait-Resident { (Get-Control $window 'AddFolderButton').Current.IsEnabled } 'FolderPicker cancellation did not complete'
            if ([ResidentNative]::Window($primary.Id) -ne $handle) { throw 'MainWindow changed after FolderPicker' }
        }
        $script:observedPicker = $null
        Write-Output 'PASS Observation: FolderPicker opened/cancelled three times with the same MainWindow; no native crash observed'
        foreach ($desired in @($true,$false)) {
            Wait-Resident { (Get-Control $window 'SettingsButton').Current.IsEnabled } 'Settings operation still busy'
            (Get-Control $window 'SettingsButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Wait-Resident { $null -ne (Get-Control $window 'StartWithWindows') } 'Startup checkbox missing'
            $toggle = Get-Control $window 'StartWithWindows'
            if (-not $toggle.Current.IsEnabled) { throw 'Startup checkbox is disabled' }
            $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            $bySave = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Save')
            $save = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$bySave)
            $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Wait-Resident { $null -eq (Get-Control $window 'StartWithWindows') } 'Startup Settings Save failed'
            $saved = Get-Content -LiteralPath (Join-Path $created[0].FullName 'settings.json') -Raw | ConvertFrom-Json
            if ($saved.startWithWindows -ne $desired) { throw 'GUI startup desired state not saved' }
        }
        Wait-Resident { (Get-Control $window 'SettingsButton').Current.IsEnabled } 'Settings save still busy'
        Start-Sleep -Milliseconds 600
        Invoke-TrayMenu $primary $handle 'Pause All'
        Wait-Resident { (Get-Control $window 'ManagerState').Current.Name -eq 'Paused' } 'Tray pause not reflected in GUI'
        Start-Sleep -Milliseconds 600
        Invoke-TrayMenu $primary $handle 'Resume All'
        Wait-Resident { (Get-Control $window 'ManagerState').Current.Name -eq 'Running requested' } 'Tray resume not reflected in GUI'
        $null = [ResidentNative]::PostMessage($handle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
        Wait-Resident { -not [ResidentNative]::IsWindowVisible($handle) } 'Close did not hide'
        $probeBeforeHide = [int](Get-Content -LiteralPath $probeCountPath -Raw)
        Wait-Resident { [int](Get-Content -LiteralPath $probeCountPath -Raw) -gt $probeBeforeHide } 'Hidden window stopped connection probes'
        if ([ResidentNative]::IsWindowVisible($handle)) { throw 'Connection probe showed hidden window' }
        Invoke-TrayMenu $primary $handle 'Open'
        Wait-Resident { [ResidentNative]::IsWindowVisible($handle) } 'Tray Open failed'
        # Simulate Explorer loss for this icon alone; never restart the user's Explorer.
        [ResidentNative]::RemoveTestIcon($handle)
        if ([ResidentNative]::HasIcon($handle)) { throw 'Test icon removal failed' }
        $taskbar = [ResidentNative]::RegisterWindowMessage('TaskbarCreated')
        1..3 | ForEach-Object { $null = [ResidentNative]::PostMessage($handle, $taskbar, [IntPtr]::Zero, [IntPtr]::Zero) }
        Wait-Resident { [ResidentNative]::HasIcon($handle) } 'TaskbarCreated did not restore icon'
        $null = [ResidentNative]::PostMessage($handle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
        Wait-Resident { -not [ResidentNative]::IsWindowVisible($handle) } 'Close before Exit did not hide'
        $hold = Join-Path $created[0].FullName 'hold-exit'
        [IO.File]::WriteAllText($hold,'hold')
        Invoke-TrayMenu $primary $handle 'Exit'
        Wait-Resident { Test-Path -LiteralPath (Join-Path $created[0].FullName 'exit-fenced') } 'Exit fence checkpoint missing'
        $secondary = Start-Process $appPath -ArgumentList @('--smoke-test-ready',$key) -WindowStyle Hidden -PassThru
        try { if (-not $secondary.WaitForExit(10000) -or $secondary.ExitCode -ne 0) { throw 'Redirect during shutdown failed' } }
        finally { if (-not $secondary.HasExited) { $secondary.Kill(); $secondary.WaitForExit() }; $secondary.Dispose() }
        if ([ResidentNative]::IsWindowVisible($handle) -or $primary.HasExited) { throw 'Shutdown activation reopened window or escaped cleanup wait' }
        $after = @(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'ImmichGuiSmoke-*' | Where-Object { $_.FullName -notin $before })
        if ($after.Count -ne 1) { throw 'Secondary during shutdown created another profile' }
        Remove-Item -LiteralPath $hold
        if (-not $primary.WaitForExit(15000) -or $primary.ExitCode -ne 0) { throw 'Tray Exit failed' }
        if ([ResidentNative]::HasIcon($handle)) { throw 'Tray icon remained after Exit' }
        $logFiles = @(Get-ChildItem -LiteralPath (Join-Path $created[0].FullName 'logs') -Filter '*.log')
        $logEvents = @($logFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName | ForEach-Object { ($_ | ConvertFrom-Json).Properties.EventName } })
        foreach ($required in @('AppStarted','PrimaryInstance','TrayCreated','TrayPauseResume','WindowHidden','WindowShown','ShutdownCompleted')) {
            if ($required -notin $logEvents) { throw "Missing flushed application event: $required" }
        }
        Write-Output 'PASS Logging: native Diagnostics view, application/tray events and shutdown flush'
        $probeAfterExit = Get-Content -LiteralPath $probeCountPath -Raw
        Start-Sleep -Milliseconds 500
        if ((Get-Content -LiteralPath $probeCountPath -Raw) -ne $probeAfterExit) { throw 'Probe continued after Exit' }
        Write-Output 'PASS Connection: primary-only repeated probes, separate GUI state, hidden monitoring, no probes after Exit'
        Write-Output 'PASS Resident: background, primary/secondary, same window, native tray Open/Pause/Resume/Exit, TaskbarCreated, startup GUI save, secondary during fenced shutdown, icon cleanup'
    } finally {
        if (-not $primary.HasExited) { $primary.Kill(); $primary.WaitForExit() }; $primary.Dispose()
    }
    # Race Run-style launch against normal launch. Registration winner alone initializes.
    $key = '--smoke-instance=' + [Guid]::NewGuid().ToString('N')
    $before = @(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'ImmichGuiSmoke-*' | ForEach-Object FullName)
    $a = Start-Process $appPath -ArgumentList @('--smoke-test-ready','--background',$key) -WindowStyle Hidden -PassThru
    $b = Start-Process $appPath -ArgumentList @('--smoke-test-ready',$key) -WindowStyle Hidden -PassThru
    try {
        Wait-Resident { $a.Refresh(); $b.Refresh(); $a.HasExited -xor $b.HasExited } 'Simultaneous startup did not elect one primary'
        $winner = if ($a.HasExited) { $b } else { $a }
        $loser = if ($a.HasExited) { $a } else { $b }
        if ($loser.ExitCode -ne 0) { throw 'Race loser failed redirect' }
        Wait-Resident { [ResidentNative]::Window($winner.Id) -ne [IntPtr]::Zero } 'Race primary missing'
        $handle = [ResidentNative]::Window($winner.Id)
        Wait-Resident { [ResidentNative]::IsWindowVisible($handle) -and [ResidentNative]::HasIcon($handle) } 'Normal activation lost during initialization'
        $created = @(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'ImmichGuiSmoke-*' | Where-Object { $_.FullName -notin $before })
        if ($created.Count -ne 1) { throw 'Race initialized multiple profiles' }
        Invoke-TrayMenu $winner $handle 'Exit'
        if (-not $winner.WaitForExit(15000) -or $winner.ExitCode -ne 0) { throw 'Race primary Exit failed' }
        Write-Output 'PASS Resident: simultaneous startup, single initialization, activation delivered before window binding'
    } finally {
        foreach ($process in @($a,$b)) { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }; $process.Dispose() }
    }
}
