param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $repoRoot "src/ImmichDesktopUploader/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64/ImmichDesktopUploader.exe"
$appProcess = Start-Process -FilePath $appPath -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $appProcess.Refresh()
    } while (-not $appProcess.HasExited -and $appProcess.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($appProcess.HasExited) { throw "App exited during startup: $($appProcess.ExitCode)" }
    if ($appProcess.MainWindowHandle -eq 0) { throw 'No WinUI window appeared before timeout.' }
    if (-not $appProcess.Responding) { throw 'WinUI window is not responding.' }
    Write-Output "WinUI startup passed: $($appProcess.MainWindowTitle), PID=$($appProcess.Id)"
    $null = $appProcess.CloseMainWindow()
    if (-not $appProcess.WaitForExit(5000)) { throw 'Window did not close within 5 seconds.' }
    if ($appProcess.ExitCode -ne 0) { throw "App exit code: $($appProcess.ExitCode)" }
    Write-Output 'WinUI close passed: exit code 0'
} finally {
    if (-not $appProcess.HasExited) { $appProcess.Kill(); $appProcess.WaitForExit() }
    $appProcess.Dispose()
}
