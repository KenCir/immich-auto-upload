param(
    [Parameter(Mandatory=$true)][string]$ApprovedServer,
    [Parameter(Mandatory=$true)][string]$DedicatedFolder,
    [ValidateSet('Debug','Release')][string]$Configuration='Debug'
)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -ApprovedServer $ApprovedServer -DedicatedFolder $DedicatedFolder -Configuration $Configuration
    exit $LASTEXITCODE
}
# Explicit, user-approved real-server test only. Never run from the default suite.
# It temporarily registers ONE existing dedicated folder and restores the original
# settings bytes and absent Run value, including on failure. Credentials are untouched.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot 'Smoke-Resident.ps1')
$repo=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $repo "src/ImmichDesktopUploader/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64/ImmichDesktopUploader.exe"
$profile=Join-Path $env:LOCALAPPDATA 'ImmichDesktopUploader'
$settingsPath=Join-Path $profile 'settings.json'
$backupPath=Join-Path $profile 'settings.json.bak'
$settingsBytes=[IO.File]::ReadAllBytes($settingsPath)
$settings=[Text.Encoding]::UTF8.GetString($settingsBytes) | ConvertFrom-Json
if ($settings.schemaVersion -ne 1 -or $settings.folders.Count -ne 0 -or $settings.startWithWindows) { throw 'Expected the reviewed empty profile with startup off; no changes made.' }
if (([Uri]$settings.serverUrl).GetLeftPart([UriPartial]::Authority) -ne ([Uri]$ApprovedServer).GetLeftPart([UriPartial]::Authority)) { throw 'Approved authority does not match saved settings.' }
if (Get-Process ImmichDesktopUploader -ErrorAction SilentlyContinue) { throw 'Close the existing app before this isolated test.' }
$DedicatedFolder=[IO.Path]::GetFullPath($DedicatedFolder)
$expectedRoot=Join-Path $env:TEMP 'ImmichPhase8Connection-44294b528f314bed8279cd43567f36ec\dedicated-images'
if ($DedicatedFolder -ne $expectedRoot) { throw 'This run is restricted to the exact user-approved dedicated folder.' }
$existingFiles=@(Get-ChildItem -LiteralPath $DedicatedFolder -File)
if ($existingFiles.Count -gt 5 -or @($existingFiles | Where-Object {$_.Extension -ne '.png'}).Count -gt 0 -or @(Get-ChildItem -LiteralPath $DedicatedFolder -Directory).Count -gt 0) { throw 'Dedicated folder safety check failed.' }
$runSubkey='Software\Microsoft\Windows\CurrentVersion\Run'
function Read-RunValue {
    $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runSubkey)
    try { if($key){return $key.GetValue('ImmichDesktopUploader',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)} } finally {if($key){$key.Dispose()}}
}
if ($null -ne (Read-RunValue)) { throw 'Expected no existing startup registration; no changes made.' }
$originalBackup=if(Test-Path -LiteralPath $backupPath){[IO.File]::ReadAllBytes($backupPath)}else{$null}
$evidence=Join-Path $repo ('.build\phase8-real-gui-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($evidence)
[IO.File]::WriteAllBytes((Join-Path $evidence 'settings-original.json'),$settingsBytes)
if($originalBackup){[IO.File]::WriteAllBytes((Join-Path $evidence 'settings-backup-original.json'),$originalBackup)}
$folderId=[Guid]::NewGuid().ToString()
$album='ImmichDesktopUploader-E2E-20260913-085302'
$primary=$null
$handle=[IntPtr]::Zero
$window=$null
$logs=Join-Path $profile 'logs'
function Find-Name([string]$name) {
    $condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Click-Name([string]$name) {
    Wait-Resident { $control=Find-Name $name; $control -and $control.Current.IsEnabled } "Control not available: $name"
    (Find-Name $name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Session-Events {
    foreach($file in Get-ChildItem -LiteralPath $logs -Filter '*.log') {
        $stream=New-Object IO.FileStream($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader=New-Object IO.StreamReader($stream)
        try { while(-not $reader.EndOfStream) {
            $line=$reader.ReadLine()
            try {$entry=$line|ConvertFrom-Json; if($entry.Properties.FolderId -eq $folderId){$entry}} catch { }
        } } finally {$reader.Dispose()}
    }
}
function Latest-Running {
    return @(Session-Events | Where-Object {$_.Properties.EventName -eq 'SessionRunning'}) | Select-Object -Last 1
}
function Upload-Count {
    $text=(@(Session-Events | Where-Object {$_.Properties.EventName -eq 'CliOutput'} | ForEach-Object {$_.Properties.OutputText}) -join '')
    return [regex]::Matches($text,'Successfully uploaded 1 new asset').Count
}
function Wait-Upload([int]$before) {
    $until=[DateTime]::UtcNow.AddSeconds(45)
    do {if((Upload-Count) -gt $before){return}; Start-Sleep -Milliseconds 250} while([DateTime]::UtcNow -lt $until)
    throw 'No successful new-asset CLI output before timeout.'
}
function Write-TestPng([string]$name) {
    $path=Join-Path $DedicatedFolder $name
    if(Test-Path -LiteralPath $path){throw 'Test image already exists; refusing to overwrite.'}
    $bitmap=New-Object System.Drawing.Bitmap(32,32)
    $random=New-Object Random
    try {
        for($x=0;$x -lt 32;$x++) {
            $color=[Drawing.Color]::FromArgb($random.Next(256),$random.Next(256),$random.Next(256))
            for($y=0;$y -lt 32;$y++){$bitmap.SetPixel($x,$y,$color)}
        }
        $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
    } finally {$bitmap.Dispose()}
}
function Start-Primary([bool]$background) {
    $script:primary=if($background){Start-Process $exe -ArgumentList '--background' -WindowStyle Hidden -PassThru}else{Start-Process $exe -WindowStyle Hidden -PassThru}
    Wait-Resident { $script:handle=[ResidentNative]::Window($primary.Id); $handle -ne [IntPtr]::Zero } 'Primary window handle missing'
    Wait-Resident { [ResidentNative]::HasIcon($handle) } 'Real tray icon not registered'
    $script:window=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
}
function Exit-Primary {
    Invoke-TrayMenu $primary $handle 'Exit'
    if(-not $primary.WaitForExit(20000) -or $primary.ExitCode -ne 0){throw 'Graceful primary exit failed.'}
    if([ResidentNative]::HasIcon($handle)){throw 'Tray icon survived Exit.'}
    $primary.Dispose(); $script:primary=$null
}
try {
    $settings.folders=@([pscustomobject]@{id=$folderId;path=$DedicatedFolder;enabled=$true;recursive=$true;albumName=$album;ignorePatterns=@();concurrency=2})
    $tempSettings=$settingsPath+'.phase8-'+[Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($tempSettings,($settings|ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
    [IO.File]::Replace($tempSettings,$settingsPath,(Join-Path $evidence 'settings-before-test.json'))
    Start-Primary $false
    Wait-Resident { [ResidentNative]::IsWindowVisible($handle) } 'Normal launch did not show window'
    Wait-Resident { $null -ne (Latest-Running) } 'Real Session did not reach Running'
    Write-Output 'PASS A: normal app, saved credentials, dedicated enabled folder and real CLI Running'
    $before=Upload-Count
    $null=[ResidentNative]::PostMessage($handle,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
    Wait-Resident { -not [ResidentNative]::IsWindowVisible($handle) } 'Close did not hide'
    Write-TestPng 'phase8-gui-background.png'
    Wait-Upload $before
    if(-not [ResidentNative]::HasIcon($handle)){throw 'Tray icon disappeared while hidden.'}
    Invoke-TrayMenu $primary $handle 'Open'
    Wait-Resident { [ResidentNative]::IsWindowVisible($handle) } 'Tray Open did not restore window'
    Write-Output 'PASS B: hidden real upload, tray retained, same MainWindow restored'
    Invoke-TrayMenu $primary $handle 'Pause All'
    Wait-Resident { (Get-Control $window 'ManagerState').Current.Name -eq 'Paused' } 'Pause not reflected'
    $before=Upload-Count
    Write-TestPng 'phase8-gui-paused.png'
    Start-Sleep -Seconds 15
    if((Upload-Count) -ne $before){throw 'Upload occurred while paused.'}
    Invoke-TrayMenu $primary $handle 'Resume All'
    Wait-Upload $before
    Write-Output 'PASS C: no upload while paused; pending image uploaded after Resume'
    Click-Name 'Disable'
    Wait-Resident { $null -ne (Find-Name 'Enable') } 'Disable did not complete'
    $before=Upload-Count
    Write-TestPng 'phase8-gui-disabled.png'
    Start-Sleep -Seconds 15
    if((Upload-Count) -ne $before){throw 'Upload occurred while disabled.'}
    Click-Name 'Enable'
    Wait-Upload $before
    Write-Output 'PASS D: no upload while disabled; pending image uploaded after Enable'
    $previous=(Latest-Running).Properties
    Click-Name 'Restart'
    Wait-Resident { $latest=Latest-Running; $latest -and $latest.Properties.RunGeneration -gt $previous.RunGeneration } 'Restart generation did not advance'
    if(Get-Process -Id $previous.LauncherPid -ErrorAction SilentlyContinue){throw 'Old launcher survived restart.'}
    Write-Output 'PASS E: Restart advanced generation and removed old launcher'
    (Get-Control $window 'SettingsButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Resident { $null -ne (Get-Control $window 'StartWithWindows') } 'Startup settings missing'
    (Get-Control $window 'StartWithWindows').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Click-Name 'Save'
    $expectedCommand='"'+$exe+'" --background'
    Wait-Resident { (Read-RunValue) -eq $expectedCommand } 'Run registration differs from exact expected command'
    Write-Output ('Run command: '+(Read-RunValue))
    $runningBefore=@(Session-Events | Where-Object {$_.Properties.EventName -eq 'SessionRunning'}).Count
    Exit-Primary
    # Execute the exact reviewed Run command components; do not evaluate registry text as code.
    Start-Primary $true
    if([ResidentNative]::IsWindowVisible($handle)){throw 'Run command background launch showed window.'}
    Wait-Resident { @(Session-Events | Where-Object {$_.Properties.EventName -eq 'SessionRunning'}).Count -gt $runningBefore } 'Background startup did not start the real Session'
    $secondary=Start-Process $exe -WindowStyle Hidden -PassThru
    try {if(-not $secondary.WaitForExit(15000) -or $secondary.ExitCode -ne 0){throw 'Secondary redirect failed.'}}finally{$secondary.Dispose()}
    Wait-Resident { [ResidentNative]::IsWindowVisible($handle) } 'Normal secondary did not show primary'
    if([ResidentNative]::Window($primary.Id) -ne $handle){throw 'Secondary changed window identity.'}
    if(@(Get-Process ImmichDesktopUploader -ErrorAction SilentlyContinue).Count -ne 1){throw 'Secondary left another app instance.'}
    Write-Output 'PASS G: actual HKCU Run command manually launched background app; normal secondary restored same window'
    Exit-Primary
    Write-Output 'PASS H: real Tray Exit completed and icon removed'
} finally {
    if($primary) {
        try {
            if(-not $primary.HasExited){Invoke-TrayMenu $primary $handle 'Exit'; [void]$primary.WaitForExit(20000)}
            if(-not $primary.HasExited){$primary.Kill();$primary.WaitForExit();Write-Output 'FAIL: forced test-process cleanup was required'}
        } finally {$primary.Dispose()}
    }
    # Restore the reviewed original state byte-for-byte, without touching credentials.
    $restore=$settingsPath+'.restore-'+[Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllBytes($restore,$settingsBytes)
    [IO.File]::Replace($restore,$settingsPath,(Join-Path $evidence 'settings-before-restore.json'))
    if($null -ne $originalBackup){[IO.File]::WriteAllBytes($backupPath,$originalBackup)}elseif(Test-Path -LiteralPath $backupPath){Remove-Item -LiteralPath $backupPath}
    $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runSubkey,$true)
    try{if($key){$key.DeleteValue('ImmichDesktopUploader',$false)}}finally{if($key){$key.Dispose()}}
    if([Convert]::ToBase64String([IO.File]::ReadAllBytes($settingsPath)) -ne [Convert]::ToBase64String($settingsBytes) -or $null -ne (Read-RunValue)){throw 'Original state restoration did not verify.'}
    Write-Output ('Original settings and absent Run registration restored. Evidence: '+$evidence)
}
