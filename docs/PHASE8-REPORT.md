# Phase 8 実装・検証記録

2026-09-13。Phase 8の実装と専用画像での実GUI／実ネットワークE2Eを実施した。
実VRChatフォルダの確認は2026-09-13のユーザー指定により今回は見送り。確認手順は残す。
この変更を反映したPhase 8の実装・検証・文書化は完了。Windows実再ログインと1〜2時間の実測soakは未実施。
既知のメモリ読み取りクラッシュは原因未特定・未解決。コミットは行っていない。

## 実装

| 項目 | 内容 |
|---|---|
| Logging architecture | Microsoft.Extensions.Loggingを共通窓口とし、Serilog.Extensions.Logging 10.0.0 / Sinks.Async 2.1.0 / Sinks.File 7.0.0を使用。既存のsinkで非同期queue・rotation・retentionを扱い、独自loggerの実装を避けた |
| Log location / rotation | `%LocalAppData%\ImmichDesktopUploader\logs`。JSON Lines、日次＋約10 MB分割、最大30ファイル。単一の大きいイベントはsize limitを超える場合がある |
| Structured events | Timestamp / Level / EventNameに、対象に応じてFolderId / RunGeneration / LauncherPid / SessionStatus / RetryCount / ConnectionGeneration / ConnectionStatus等を付与 |
| CLI output | stdout / stderrをOutputSourceで区別しDebug記録。stderrを一律Errorにしない。pipeのDroppedOutputChunks / OutputDrained、収集中断時のTailMayBeIncomplete、loggerのDroppedLogEventsを区別 |
| Nonblocking behavior | Serilog async queueは2048件、blockWhenFull=false。遅いsinkでproducer500件が停止しないことを検証。既存のbounded pipe出力を維持し、UI / event loop / pipe readerでfile I/Oを待たない |
| Secret redaction | 出力境界の分割キー処理を維持し、file formatterでJSON文字列とproperty名を再redact。廃止したキーも終了まで保持。Settings全体、DPAPI blob、復号payloadは記録しない |
| Diagnostics UI | App / 最終観測CLI version、launcher、Server URL、資格情報有無、folder / Running / Error件数、Paused、接続snapshot、ログ保存先・状態。API Keyの表示なし |
| Open logs folder | GUIのOpen logs folder。ディレクトリ作成をworkerで行い、非同期Launcher.LaunchFolderAsyncでExplorerを開く。実GUI→正しいExplorerフォルダを検証 |
| Error UX | CLI不可を全体とfolderに表示。nonretryable / retry exhausted / previous error / disabled / pausedを区別。内部stackはUIに表示せず、UI失敗の型・HResult・stackはログに記録 |
| Crash diagnostics | AppDomain / TaskScheduler / WinUIの未処理例外を観測。Message / Dataを避けたmetadataを記録し、fatalは最大2秒のflushを試みる。Handled / SetObservedにしない |
| Shutdown | 明示Exitで操作をfence、AppCoordinatorと全Session/probeのcleanup、ShutdownCompleted、file logger flush、tray除去とapp exit。native crash時の末尾保存は保証しない |
| Logging failure | directory作成・書込み失敗やqueue欠落をGUI warningへ反映。file logger失敗でもmanagerのStart / Disposeが動くことを検証 |

App / tray / activation / startup / settings / credentials / manager / Session / probe / recovery の各イベントを記録する。
secondary側は長寿命loggerを作らず、primaryが受信activationを記録する。
Phase 1〜7のlauncher方式、environment isolation、Job Object、retry、recovery条件、state machine、settings semanticsは変更していない。
Sessionの固定backend error codeとretryable属性は表示・診断用に引き継ぐだけで、判定方針を変えていない。

接続と復旧の代表フィールド（全JSON recordではない）:

```json
{"EventName":"ConnectionChanged","ConnectionGeneration":1,"ConnectionStatus":"Unavailable"}
{"EventName":"RecoveryCandidateRegistered","ConnectionGeneration":1,"FolderId":"..."}
{"EventName":"RecoveryAttempted","ConnectionGeneration":1,"FolderId":"..."}
{"EventName":"RecoveryConsumed","ConnectionGeneration":1}
```

## 検証

| 検証 | 結果と範囲 |
|---|---|
| Automated tests / regression | Phase 1〜7の125件を含め139件成功、0件失敗。`scripts/Test.ps1`で実行 |
| Build | solution build 警告0 / エラー0 |
| Logging tests | file creation、small size rollover / retention、write failure、blocked sink / drop、structured / escaped / split secrets、exception payload除外、lifecycle / retry / monitor / recovery、fatal flush、Diagnostics command fence |
| Short stress | file loggingを有効にして100回Start / Stop＋10回Restart、110世代。同時run最大1、終了後run / timer 0。仮想時間による短時間検証 |
| Native UI smoke | WinUI binding / PasswordBox、close-to-tray、同window Open、native Tray Pause / Resume / Exit、background、single-instanceと競合、TaskbarCreated再登録、hidden probe、Diagnostics、実Explorerのlogs folder、FolderPicker3回cancel、shutdown flush。テストのmenu操作修正後、全体3回連続成功 |
| Manual final E2E | 実CLIで専用画像合計4枚と同一コピーの重複判定。ユーザーが画像とalbumを確認。実GUIでhide / Pause / Resume / Disable / Enable / Restart / Exitを確認 |
| Startup E2E | 実HKCU Run値の登録をGUIから行い、Exit後にそのcommandを手動実行。非表示起動、tray、実Session Running、通常secondaryによる同window表示を確認。実Windows再ログインは未実施 |
| Network recovery E2E | 承認済みhostのみを通すloopback TLS tunnelを遮断・復旧。実CLI / Coordinator、同じConnectionGenerationで復旧、RecoveryAttempted正確に1件。以後の成功probeで再起動なし。追加画像1枚をユーザーも確認 |
| Secret check on real logs | 初回upload、実GUI、実network E2Eのflush済みログを保存済みキーとローカル照合。平文 / JSON escapeの一致0件。照合時にキー自体を出力せず、ネットワーク送信もしない |
| Image crash investigation | FolderPicker3回、繰り返しhide / show、画像uploadで再現なし。過去30日のApplication Error 88件にも対象appの一致なし。faulting moduleの特定には至らず、未解決扱いを維持 |
| VRChat final test | ユーザー指定により今回は見送り。大量uploadを避ける手順を記載済み。実写真フォルダは変更していない |
| Long-running test | 1〜2時間の手動soak手順を記載済み。実測soakは未実施。短時間stressを長時間実測として扱わない |
| Residual processes | 実upload / GUI / network E2E後のapp / launcher / Node / CLI / server-info残存0件を確認 |

実GUIの一時変更はユーザーの明示承認を受け、元のsettings.jsonとbackup、自動起動OFF / Run登録なしへ復元した。
settings.jsonはハッシュ／byte照合でも一致。資格情報ファイルは変更していない。
Windows全体のproxy、firewall、DNS設定を変更していない。

実際のRun command:

```text
"C:\Users\katyo\source\repos\immich-auto-upload\src\ImmichDesktopUploader\bin\Debug\net10.0-windows10.0.19041.0\win-x64\ImmichDesktopUploader.exe" --background
```

詳細な手順、途中失敗と再試験の理由、証拠の保存先は [FINAL-E2E.md](FINAL-E2E.md) を参照。
ビルド・自動テスト出力は `.build/phase8-build.txt`、`phase8-regression.txt`、`phase8-smoke.txt`。
実サーバー出力は `.build/phase8-real-gui.txt` と `phase8-network-e2e.txt`。

## 変更箇所

| 範囲 | 主なファイル |
|---|---|
| Logger / dependency | Infrastructure/Logging/FileLogging.cs、SecretRegistry.cs、CrashDiagnostics.cs、ImmichDesktopUploader.csproj |
| Application logging | AppDiagnostics.cs、AppCoordinator.cs、UploadManager.cs、UploadSession.cs、UploadSessionModels.cs、UploadSessionFactory.cs、ConnectionMonitor.cs |
| CLI / persistence | ImmichCliBackend.cs、ImmichServerInfoProbe.cs、SettingsService.cs、CredentialService.cs |
| Desktop / lifetime | DiagnosticSummary.cs、DesktopApplicationService.cs、MainWindow.xaml / .cs、MainViewModel.cs、Mvvm.cs、TrayService.cs、StartupService.cs、SingleInstanceService.cs、Program.cs、App.xaml.cs、SmokeTestProfile.cs |
| Tests / evidence | tests/Logging、ConfiguredNetworkE2E.cs、ConnectGate.cs、Session / GUI tests、Program.cs、Smoke-Resident.ps1、FinalGuiE2E.ps1 |
| Documentation | README.md、FINAL-E2E.md、PHASE8-REPORT.md、IMPLEMENTATION-HISTORY.md |

## Known issues / v0.1 limitations / next step

既知のread address 0x88クラッシュは未解決。再現した場合は時刻、操作、起動exe、障害モジュールを取得し、
原因を絞れた場合だけ最小修正する。native crashで末尾ログが必ず保存されるとは保証しない。

現在のPhase 7では、ConnectionMonitorが障害を観測する前に発生したErrorは回復候補に含まれない。
最初のnetwork試験でもこの条件を確認した。成功試験は障害観測後の適格なErrorを対象にしており、
「すべてのErrorがネットワーク復旧で再開する」ことは保証していない。今回この方針は変更していない。

今後の運用確認には1〜2時間の常駐観察を推奨する。実VRChat確認はユーザー指定により今回の完了条件から除外し、手順のみ残す。Run command手動起動は済んでいるが、実再ログインは別途未実施。
Installer / MSIX / 自動更新 / Windows notifications / Immich API direct integration / 独自FileSystemWatcher / 新しい同期機能は追加していない。
