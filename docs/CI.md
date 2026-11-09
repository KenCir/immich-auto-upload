# GitHub Actions CI

`CI / Windows Release build and tests` runs on pushes to `main`, pull requests targeting
`main`, and manual dispatch. The remote default branch was verified as `main`.

## Configuration

- Windows GitHub-hosted `windows-2025`, x64; .NET SDK `10.0.x`, matching both
  `net10.0` and `net10.0-windows10.0.19041.0`. There is currently no `global.json`;
  the workflow uses it instead of the fallback SDK range if one is added.
- Restore and Release build the entire solution, including WinUI, the console test
  runner and ProcessTestHost. Existing PlatformTarget=x64, Windows App SDK,
  Windows SDK BuildTools, runtime requirement and package versions are unchanged.
- PackageReference and the existing NuGet.Config (nuget.org) remain authoritative.
  No lock files or central package management are introduced. The NuGet cache hashes
  project/build/NuGet/SDK/workflow inputs; restore always runs, including cache misses.
- No repository .editorconfig, custom analyzer/ruleset or formatting gate exists.
  Existing compiler analysis remains enabled; warnings are visible and are not
  suppressed or newly promoted to errors.
- Only `contents: read`; checkout does not persist credentials. Official GitHub
  Actions are pinned to full release SHAs with version comments. No secrets,
  server settings, write permissions, pull_request_target, deployment or release.
  Older runs of the same PR are canceled; push/manual runs are not canceled.

## Tests and local reproduction

```powershell
dotnet restore ImmichDesktopUploader.sln --nologo
dotnet build ImmichDesktopUploader.sln --configuration Release --no-restore --nologo
pwsh -NoProfile -File scripts/Test-CI.ps1
```

This is an existing custom console suite, **not** a `dotnet test` project. The CI
wrapper invokes the built Release executable with no arguments and preserves a
nonzero exit status. A missing final result, zero executed tests or reported failures
also fails CI. It does not rebuild with FoundationOnly, so the WinUI build stays covered.

Tests use isolated temporary storage, fake backends, ProcessTestHost, Windows Job
Objects, DPAPI and an isolated HKCU test key. They do not start the GUI application
or contact an Immich server. The optional real CLI test, when CLI is present, only
checks version/help and process cleanup; CI does not install Immich CLI. The existing
runner labels CLI absence as PASS; the wrapper reports both optional checks as skips and
subtracts them from successful/executed counts without changing any test assertions.

`TestResults/tests.log` and `summary.md` are retained as the
`windows-release-test-results` artifact for 14 days, including on test failure.
The job summary records counts or explains that tests did not complete. Cancellation
or runner failure may prevent artifact publication; absence is not evidence of success.

Real-server/credential checks, actual uploads, GUI smoke and long-running stability
scripts are deliberately excluded. No environment variables or credentials are needed
for the build or ordinary suite. Application runtime behavior is not manually tested
by CI; only existing automated checks and compilation cover it.

## First GitHub run

After review, push the commit/branch yourself and open a PR targeting `main`, or push
to `main` according to repository policy. Ensure Actions and the four official actions
(checkout, setup-dotnet, cache, upload-artifact) are allowed in repository settings.
Manual Run workflow is available once the workflow is on the default branch.
Inspect restore/build/test logs, job summary and the test artifact. Confirm a cold
cache run succeeds and that a subsequent eligible run restores a cache. Optionally
require `Windows Release build and tests` in branch protection after the first run.

GitHub-hosted execution, action downloads, cache service and artifact publication
must be verified on GitHub; local validation does not establish their success.

## Local validation (2026-11-09)

Windows x64 / .NET SDK 10.0.401: restore succeeded, including an empty dedicated
NuGet package directory with HTTP cache disabled. Release solution build succeeded
with 0 warnings and 0 errors. The existing runtime/package settings were unchanged.
The normal suite executed 139 tests: 139 passed, 0 failed, 0 skipped. Repeating with
Immich CLI directories removed from the child PATH (matching a runner without CLI)
executed 137 tests: 137 passed, 0 failed, 2 optional CLI checks skipped (corrected
reporting after identifying the second skip marker). No server
connection or GUI application launch was performed.

actionlint 1.7.12 validated workflow YAML and settings without errors. PowerShell AST
parsing, diff whitespace checks and review of the three added files passed; no real
credentials were added. Application source, existing tests and dependency versions
have no changes. Hosted verification is recorded below.

The CI test step uses a private temporary directory with an explicit, inheritable
current-user ACL. This isolates filesystem permission tests from the hosted image's
default temporary-directory ACL. The first hosted run failed the existing test's
ACL restoration equality assertion; no assertion or production code was changed.

## GitHub validation (2026-11-09)

[PR #2](https://github.com/KenCir/immich-auto-upload/pull/2) hosted
[run 42544105906](https://github.com/KenCir/immich-auto-upload/actions/runs/42544105906)
completed successfully on windows-2025 with .NET SDK 10.0.403. Restore from a cache
miss and the full Release build passed (0 warnings, 0 errors). The test artifact
reports 137 executed/passed, 0 failed, 2 skipped because Immich CLI is not installed.
Artifact upload and NuGet cache save also succeeded. The ACL restoration test passed
unchanged with the dedicated temporary directory. The initial failed run also
successfully published its failure log and summary.

Push-to-main and manual-dispatch triggers still require verification after merging;
this PR validation does not merge the change or run external/GUI/upload tests.
