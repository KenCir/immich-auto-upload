# Immich Desktop Uploader

Windows 11 / x64 向けの個人用常駐アプリです。WinUI 3 で設定したフォルダを、PATH 上の Immich CLI の watch 機能でアップロードします。Phase 1〜7 に、Phase 8 の file logging / Diagnostics / エラー表示を追加しています。最終手動 E2E は一部未実施です。

## Build / run

.NET 10 SDK、Windows 11 x64、PATH 上の Immich CLI が必要です。検証した CLI は 3.2.0、実サーバーは 3.1.0 です。アプリが Node や CLI をインストールすることはありません。

```powershell
dotnet build ImmichDesktopUploader.sln
& ./src/ImmichDesktopUploader/bin/Debug/net10.0-windows10.0.19041.0/win-x64/ImmichDesktopUploader.exe
```

Windows App SDK 1.8.260101001 は self-contained、.NET は framework-dependent です。Installer / MSIX はありません。

## 使い方

1. Settings で Server URL と API Key を保存します。キーは DPAPI で現在の Windows ユーザー向けに暗号化します。
2. Add folder でフォルダ、album、recursive、ignore、concurrency を設定します。初めは専用の少数画像で確認してください。
3. Enabled なフォルダを自動開始します。Pause All / Resume All は全体の一時停止／再開、Disable は個別の無効化です。
4. ×はウィンドウを隠します。アップロードと接続確認は続きます。Tray Open または通常の再起動で同じウィンドウを表示します。
5. 明示的な Exit で新規操作を止め、AppCoordinator と全 CLI tree の cleanup、ログ flush を待って終了します。

Tray は Win32 Shell_NotifyIcon と native menu を使用し、TaskbarCreated で Explorer 再起動時に再登録します。登録失敗時はウィンドウを残します。Windows App SDK AppInstance で secondary activation を primary に転送し、secondary は UploadManager を作りません。

Start with Windows は HKCU Run に `"現在のexe絶対パス" --background` を保存します。通常の background 起動はウィンドウを表示せず、操作が必要な初期化エラー時は表示します。Settings の希望値と registry の実状態を区別し、不一致を表示します。実際の再ログイン検証は未実施です。

## 接続と再試行

1 フォルダにつき 1 UploadSession / 1 CLI process、Job Object が子孫プロセスを所有します。継承した IMMICH_* を除去して接続 URL / API Key だけを設定します。動作設定は引数で渡します。

Session event loop と RunGeneration が古い通知を排除します。2 / 5 / 10 秒の計3回再試行、30秒安定動作後の回数リセットは Phase 8 でも変更していません。Error 表示は再試行上限と設定修正が必要な失敗を区別し、次の操作を案内します。

Phase 7 の ConnectionMonitor は server-info を直列に実行し、cleanup 後30秒間隔、10秒 timeout で確認します。接続確認は Session 開始の前提ではありません。適格な失敗→成功イベントで、既存条件に従って Error Session を再開します。恒常的な成功状態から無限再開しません。接続失敗の観測より前に発生したErrorは、現在の回復候補条件に入りません。

server-info には CLI が要求する API Key 権限も必要です。実検証では `server.about` / `asset.statistics` 不足を確認し、ユーザーの権限追加後に成功しました。Connection check failed はネットワーク断だけを意味しません。

## ログと Diagnostics

保存先は `%LocalAppData%\ImmichDesktopUploader\logs`。GUI の **Open logs** で開き、**Diagnostics** でバージョン、CLI の最終観測値、接続先、資格情報の設定有無、フォルダ／Session 件数、接続状態、ログの状態を確認できます。CLI 未起動時はバージョン未確認と表示します。

- 共通入口は Microsoft.Extensions.Logging、保存は Serilog の非同期 file sink。
- JSON Lines、日次＋約10 MB分割、最大30ファイル。個々の巨大イベントにより10 MBを超える場合があります。
- キューは2048件で、満杯時は捨てて件数を記録します。UI / Session event loop / CLI pipe reader はファイル I/O を待ちません。
- CLI stdout / stderr は Debug、OutputSource で区別します。stderr という理由だけで Error にしません。FolderId / RunGeneration / LauncherPid、probe には ConnectionGeneration を付けます。
- pipe の欠落数／drain 状態と logger の欠落数は別です。正常終了時に logger の最終欠落数を保存します。終了時の未収集出力末尾、強制終了／native crash 時の完全性は保証しません。
- API Key は出力境界の分割対応 redaction と file formatter の構造化 redaction を通します。退役したキーもアプリ存続中は保持し、遅延イベントからの漏れを防ぎます。
- Settings 全体、DPAPI blob、復号 payload は記録しません。fatal diagnostics は例外型／HResult／stack などに限定し、例外 Message / Data は記録しません。
- file 書込み失敗やキュー欠落を GUI に表示します。logging failure を理由に upload を停止しません。書込不可時にはログ自体が残らない場合があります。

主なイベントは AppStarted / PrimaryInstance / ActivationRedirected、TrayOpen / WindowHidden / TrayReregistered、SessionStarting / SessionRetryScheduled / SessionStableReset、ConnectionChanged / StaleProbeIgnored / RecoveryEdgeDetected / RecoveryConsumed、ShutdownCompleted です。

接続ログの主なフィールド例（全レコードではありません）:

```json
{"EventName":"ConnectionChanged","ConnectionGeneration":2,"ConnectionStatus":"Unavailable"}
{"EventName":"RecoveryEdgeDetected","ConnectionGeneration":2,"ConnectionStatus":"Reachable","OutageId":1}
```

secondary は長寿命 logger を作りません。受信した activation は primary のログに記録します。AppDomain / TaskScheduler / WinUI の未処理例外を観測し、安全な範囲で fatal flush を試みます。Handled / SetObserved による強制継続は行いません。

## 検証

```powershell
./scripts/Test.ps1
./scripts/SmokeTest-WinUI.ps1
```

Test.ps1 は独自 console runner です。`dotnet test` 単独では実行されません。通常テストは隔離データと ProcessTestHost を使用し、実写真をアップロードしません。Smoke は Explorer が動作する対話デスクトップで実行してください。Shell IPC を禁止する制限トークンでは Tray 登録が失敗します。

Phase 8 の検証は JSON redaction、実子プロセスの分割キー出力、rotation / retention、書込不可、blocked sink / drop、Session / connection / recovery ログ、fatal flush、Diagnostics と終了時の操作禁止を含みます。Native smoke は実 Tray、background、single-instance、同じ window の再表示、TaskbarCreated、終了時 flush を検査します。

2026-09-13 の実サーバー確認では専用画像4枚（初回1枚＋GUI E2E 3枚）、アルバム、同一コピーの重複判定を CLI とユーザーの画面確認で検証しました。実GUIで hide / Pause / Resume / Disable / Enable / Restart、実HKCU Run commandの手動起動、secondary activation、Tray Exitも成功し、関連プロセス残存は0件でした。承認を受けた一時 settings / Run registry 変更は元に復元済みです。実写真フォルダは変更していません。

実施結果と未実施項目は [最終E2E手順・記録](docs/FINAL-E2E.md) を参照してください。Phase 1〜7 の記録は [実装履歴](docs/IMPLEMENTATION-HISTORY.md) に保存しています。履歴中の「未実装」「未検証」はその時点の記録です。

## 既知の問題・v0.1 の範囲

報告されたメモリ読み取りエラー（read address 0x88）は、原因未特定・未解決です。専用画像のアップロード成功や smoke 成功は解消の証明ではありません。再現操作、発生時刻、起動 exe、Windows の障害モジュール情報が必要です。

実ネットワーク失敗／復旧は、テストプロセスだけのTLS中継で検証し、同じ接続generationでの1回だけの再開と新規画像uploadを確認しました。Windows全体の通信設定は変更していません。

実VRChatフォルダの確認はユーザー指定により今回は見送り、手順のみ残しています。実Windows再ログインと1〜2時間常駐の手動確認は未実施です。CLI の watch / 重複判定に依存し、Windows notifications、installer、MSIX、自動更新、Immich API direct integration、独自 FileSystemWatcher、双方向同期には対応しません。
