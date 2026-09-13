param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Configuration $Configuration
    exit $LASTEXITCODE
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$repoRoot = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $repoRoot "src/ImmichDesktopUploader/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64/ImmichDesktopUploader.exe"
foreach ($profile in @('--smoke-test', '--smoke-test-folders')) {
$appProcess = Start-Process -FilePath $appPath -ArgumentList $profile -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $appProcess.Refresh()
    } while (-not $appProcess.HasExited -and $appProcess.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($appProcess.HasExited) { throw "App exited during startup: $($appProcess.ExitCode)" }
    if ($appProcess.MainWindowHandle -eq 0) { throw 'No WinUI window appeared before timeout.' }
    if (-not $appProcess.Responding) { throw 'WinUI window is not responding.' }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)
    foreach ($id in @('MainViewTitle', 'ServerUrl', 'CredentialState', 'SettingsButton', 'FolderList', 'AddFolderButton')) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $element = $null
        do {
            $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if (-not $element) { Start-Sleep -Milliseconds 100 }
        } while (-not $element -and [DateTime]::UtcNow -lt $deadline)
        if (-not $element) { throw "MainView control missing: $id" }
    }
    $credentialCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CredentialState')
    $credential = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $credentialCondition)
    if ($credential.Current.Name -notlike '*Credentials missing*') { throw 'Initial ViewModel binding is not visible.' }
    if ($profile -eq '--smoke-test-folders') {
        $rowCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'FolderDisplayName')
        do {
            $rows = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
            if ($rows.Count -lt 2) { Start-Sleep -Milliseconds 100 }
        } while ($rows.Count -lt 2 -and [DateTime]::UtcNow -lt $deadline)
        if ($rows.Count -ne 2) { throw 'Expected two isolated sample folder cards.' }
        if ($rows[0].Current.Name -ne 'Sample A') { throw 'Folder card projection is not bound.' }
        Write-Output 'Two disabled sample folder cards loaded; no credentials or uploads.'
    }
    $settingsCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsButton')
    $settingsButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $settingsCondition)
    while (-not $settingsButton.Current.IsEnabled -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $settingsButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $passwordCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ApiKeyInput')
    $passwordInput = $null
    do {
        $passwordInput = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $passwordCondition)
        if (-not $passwordInput) { Start-Sleep -Milliseconds 100 }
    } while (-not $passwordInput -and [DateTime]::UtcNow -lt $deadline)
    if (-not $passwordInput -or -not $passwordInput.Current.IsPassword) { throw 'Settings secret input did not load as a password control.' }
    Write-Output "MainView binding and Settings PasswordBox passed ($profile)."
    Write-Output "WinUI startup passed: $($appProcess.MainWindowTitle), PID=$($appProcess.Id)"
    $null = $appProcess.CloseMainWindow()
    if (-not $appProcess.WaitForExit(15000)) { throw 'Window did not close within 15 seconds.' }
    if ($appProcess.ExitCode -ne 0) { throw "App exit code: $($appProcess.ExitCode)" }
    Write-Output 'WinUI close passed: exit code 0'
} finally {
    if (-not $appProcess.HasExited) { $appProcess.Kill(); $appProcess.WaitForExit() }
    $appProcess.Dispose()
}
}
