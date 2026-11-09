# Run the existing console suite after a Release solution build. Never pass E2E options.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force TestResults | Out-Null
    $summaryPath = 'TestResults/summary.md'
    'Tests did not complete; inspect tests.log and the test step for the failure reason.' | Set-Content $summaryPath
    $runner = './tests/ImmichDesktopUploader.Tests/bin/Release/net10.0/ImmichDesktopUploader.Tests.exe'
    if (!(Test-Path $runner)) { throw 'Release test executable missing. Build the solution first; no tests ran.' }
    & $runner 2>&1 | Tee-Object -FilePath TestResults/tests.log
    $testExitCode = $LASTEXITCODE
    $log = Get-Content TestResults/tests.log -Raw
    $result = [regex]::Matches($log, '(?m)^RESULT: (\d+) passed, (\d+) failed\r?$')
    if ($result.Count -ne 1) { throw 'Test runner did not produce exactly one final result; test completion is unverified.' }
    $passed = [int]$result[0].Groups[1].Value
    $failed = [int]$result[0].Groups[2].Value
    # The existing runner counts its optional CLI-not-installed return as PASS.
    # Correct only the report; preserve the suite and its exit code unchanged.
    $skipped = [regex]::Matches($log, '(?m)^Immich CLI実機検証: skipped\r?$').Count
    $passed -= $skipped
    $executed = $passed + $failed
    @(
        "Executed: $executed; passed: $passed; failed: $failed; skipped: $skipped."
        "Console runner exit code: $testExitCode."
        'Optional CLI check: only version/help/process cleanup; skipped if Immich CLI is absent (see tests.log).'
        'Excluded: real-server/credential E2E, real uploads, GUI smoke and long-running stability tests.'
    ) | Set-Content $summaryPath
    if ($testExitCode -ne 0) { exit $testExitCode }
    if ($failed -ne 0 -or $executed -eq 0 -or $passed -lt 0) { throw 'No executed tests or inconsistent/failed test results.' }
} finally { Pop-Location }
