param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet build tests/ImmichDesktopUploader.Tests/ImmichDesktopUploader.Tests.csproj "-p:FoundationOnly=true" "-c:$Configuration" --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & "./tests/ImmichDesktopUploader.Tests/bin/$Configuration/net10.0/ImmichDesktopUploader.Tests.exe" --upload-e2e
    exit $LASTEXITCODE
} finally { Pop-Location }
