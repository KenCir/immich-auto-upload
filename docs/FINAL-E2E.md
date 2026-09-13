# v0.1 最終運用確認

この文書は実施手順です。手順の存在や自動テスト成功を、実サーバーへのupload成功とは扱いません。
各ケースに実施日時、exe絶対パス、App / CLI version、FolderId、結果、対応するログ時刻を記録してください。
API Key、credentials.dat、DPAPI blobを結果報告へ添付しないでください。

## 準備

1. アプリをビルドし、Phase 1〜8の回帰とWinUI smokeを実行する。
2. アップロードしてよい、個人情報を含まない画像を1〜数枚用意する。
3. 空の専用テストフォルダを作る。実写真ライブラリはまだEnabledにしない。
4. Settingsで意図したサーバーURLと資格情報を確認する。既存設定を無断で書き換えない。
5. 専用フォルダを追加し、識別できるテストalbum名を設定する。元の設定を控える。
6. DiagnosticsでApp version、CLI version（確認済みの場合）、launcher、接続状態、ログ場所を記録する。
7. Open logs folderでlogsを開けることを確認する。ログ警告が出ている場合は保存場所と空き容量を確認する。

## A. 通常起動

1. exeを引数なしで起動し、windowが表示されることを確認する。
2. Credentials configured、専用フォルダのEnabled、Session Runningを確認する。
3. テスト画像を1枚追加する。
4. Immichの画面でその画像とテストalbumへの所属を確認する。
5. Connection check succeededやCLI出力だけでupload成功を判定しない。

## B. Tray常駐

1. ×でwindowを隠し、Tray iconが残ることを確認する。
2. 同じCLI PID / RunGenerationが続いていることをログで確認する。
3. 別のテスト画像を1枚追加し、windowを隠したままImmichでuploadを確認する。
4. Tray Openで同じMainWindowが復帰し、重複windowがないことを確認する。

## C. Pause / Resume

1. TrayまたはGUIでPause Allを選ぶ。
2. PausedとSession停止を確認してから画像を追加する。
3. Pause中はその画像がuploadされないことを確認する。
4. Resume Allを選び、保留画像がuploadされることを確認する。
5. 過去のErrorは通常のResumeで保持される場合がある。勝手なnetwork recoveryが起きたと解釈しない。

## D. Disable / Enable

1. 専用フォルダをDisableにする。
2. Session停止後に画像を追加し、uploadされないことを確認する。
3. Enableにし、initial scanで追加画像がuploadされることを確認する。

## E. Restart

1. 専用SessionがRunningの状態でFolderId、PID、RunGenerationを控える。
2. Restartを選び、旧treeの終了後に新しいPID / generationへ変わることを確認する。
3. 同じフォルダのCLIが重複せず、画像追加後のuploadが継続することを確認する。

## F. Network recovery

1. 専用SessionをRunningにする。他の実フォルダはこの試験へ巻き込まない。
2. テスト用サーバー／管理可能なテスト用プロキシを一時停止するなど、影響範囲が限定された方法で到達不能にする。
   Windows firewallやWi-Fi設定を自動変更しない。
3. Connection check failedを確認する。probe完了から次回まで30秒、実行期限は10秒が目安。
4. CLIが生存している場合は、同じPIDでRunningを維持し、強制Stop / Restartされないことを確認する。
5. retry exhaustionも試す場合は、専用CLIのruntime failureを制御できる試験環境で実施する。
   2秒 / 5秒 / 10秒の3回retry後にErrorとなり、その失敗がUnavailable期間内であることをログで確認する。
6. 接続先のURLを変更せず、同じ接続先への到達性を復旧する。
7. Connection check succeededとUnavailable→Reachable edgeを確認する。
8. 対象候補だけが一度再開し、RetryCountが0から始まることを確認する。
9. 成功probeが続いても再起動が反復しないことと、保留画像のuploadをImmichで確認する。

URLの変更・復元は接続generationも変更します。その試験だけでは、同一generationのnetwork recoveryを証明できません。
Running CLIが一度も終了しなかった試験を、retry exhaustionからの復旧成功と記録しないでください。

## G. Windows自動起動

1. SettingsでStart with WindowsをONにし、Saveする。元のdesired stateを控える。
2. Tray Exitで終了し、HKCU\Software\Microsoft\Windows\CurrentVersion\Run の登録値を確認する。
3. 登録値が `"現在のexe絶対パス" --background` であることを確認する。
4. 登録commandを手動実行する。または、ユーザーが作業を保存したうえでWindowsへ再ログインする。
5. 正常設定ならwindow非表示、Trayあり、Session Runningで開始することを確認する。
6. 引数なしで同じexeを追加起動し、既存windowだけが表示され、CLIが重複しないことを確認する。
7. 「登録commandの手動実行」と「実Windows再ログイン」は結果欄で区別する。
8. 試験後、ユーザーが選んだ元のStart with Windows設定へ戻す。

## H. Exit

1. windowが隠れている状態からTray Exitを選ぶ。
2. ShutdownStarted、probeと全Sessionのcleanup、ShutdownCompletedをログで確認する。
3. Tray iconとappが消え、ログ末尾がflushされていることを確認する。
4. 記録したPIDでcmd / launcher / Node / CLI / server-infoが残っていないことを確認する。
5. 無関係なNodeやExplorerは終了しない。

## 実VRChatフォルダ

専用フォルダの試験がすべて通った後に実施します。既存登録がある場合はその設定を使えます。
未登録の場合、ユーザーが指定するフォルダと既存画像の扱いを確認してから設定します。
大量の既存画像を新たにuploadするようなEnableを行わないでください。

VRChatで新しいスクリーンショットを1〜数枚撮影し、指定フォルダへの保存、watchによる検出、Immichとalbumへの反映を順に確認します。
専用テストのみ成功した場合、実VRChat確認は「未実施」と記録します。

## 1〜2時間の常駐観察

CIで何時間も待つテストにはしません。ユーザーが専用環境で実行します。

- 開始時と15分ごとにappのメモリ、handle数、関連子プロセス数、PIDを記録する。
- probeが重複せず繰り返されることと、一定成功状態でError Sessionを無限再起動しないことを確認する。
- ときどきTray Hide / Open、Pause / Resumeを行い、重複windowや操作不能がないことを確認する。
- 少数のテスト画像を追加し、uploadをImmich側で確認する。
- logsのサイズとファイル数を観察する。通常設定は日次・約10MB分割・最大30ファイル。
- 終了時にExitと残存PIDを確認する。短時間の仮想時間テストを長時間実測と混同しない。

## 既知クラッシュの追跡

画像に関連すると報告されたメモリ読み取りクラッシュは、安定して再現できておらず、原因未特定です。解消済みとは扱いません。
元の報告はアプリケーションエラーの画像で、発生操作やfaulting moduleは確定できていません。
アプリ内に画像プレビューやalbum閲覧画面はありません。Immich側の表示操作と区別してください。

FolderPickerを開いて選択／cancel、Tray Hide / Open、MainWindow再表示、専用画像のuploadを繰り返し、
再現時刻、直前の操作、exe絶対パス、Windowsイベントログの障害モジュールと例外コードを記録します。
managed fatal hookは安全な範囲でログと短いflushを試みます。native access violationで必ず末尾が残るとは保証しません。
再現できた場合だけ原因を絞り込み、最小修正と対応する回帰を追加します。

## 結果記録

| ケース | 実施日時・環境 | 結果 | 証拠・ログ時刻 |
|---|---|---|---|
| A〜E / G / H 専用フォルダ | 2026-09-13 / Windows、CLI 3.2.0 | 実GUI成功、GはRun command手動実行 | 下記実GUI記録参照 |
| 実Windows再ログイン | 未記入 | 未実施 | |
| 同一generationのnetwork recovery | 2026-09-13 / CLI 3.2.0、Node 24.21.0 | 実CLI / Coordinatorで成功 | 下記ネットワーク記録参照 |
| 実VRChatフォルダ | 2026-09-13 | ユーザー指定により今回は見送り | 手順のみ保持、実写真フォルダの変更なし |
| 1〜2時間常駐 | 未記入 | 未実施 | |
| 既知クラッシュ再現 | 未記入 | 未解決・観察継続 | |

### 2026-09-13 専用画像の実 CLI 確認

ユーザーの明示承認を受け、保存済み DPAPI 資格情報を `https://immich.kencir.blog` にのみ使用した。
最初の server-info は `server.about` / `asset.statistics` 不足で失敗したが、ユーザーが権限を追加した後、成功した。
CLI 3.2.0 / server 3.1.0。個人用の settings、Run 登録、登録フォルダには変更を加えていない。

- 明示オプション `--configured-upload-check https://immich.kencir.blog` でのみ実行するテスト入口を使用。
- ランダムな縦縞の32×32 PNG 1枚（212 bytes）と、そのバイト単位で同じコピーだけを専用一時フォルダに作成。
- 既存 UploadSession / ImmichCliBackend を使用。CLIが `Successfully uploaded 1 new asset`、アルバム1件作成を報告。
- コピーは `Found 0 new files and 1 duplicate`。追加アップロードなし。
- アルバム `ImmichDesktopUploader-E2E-20260913-085302` の画像1枚とアルバム表示をユーザーが確認。
- Session.StopAsync / DisposeAsync の完了後、関連 app / launcher / Node / CLI / server-info の残存0件を確認。
- ログ: `%TEMP%\ImmichPhase8Connection-44294b528f314bed8279cd43567f36ec\logs`。
- flush 済み実アップロードログを保存済みキーとローカル照合し、平文／JSON escape 形式ともキー一致0件。照合時にキー自体の出力やネットワーク送信はしていない。
- 同じディレクトリの `dedicated-images` とサーバー上のテスト画像は確認用に残している。

これは専用画像の実アップロード・重複・アルバム・Session終了の確認であり、GUIを通した A〜H 全項目の完了ではない。
この初回試験時点では、実再ログイン、実ネットワーク復旧、VRChat 実フォルダ、1〜2時間常駐は未実施だった。
その後の実GUIとネットワーク試験は後述の記録を参照。
報告されている native memory-read crash の再現・解消を証明する結果でもない。

### 自動検証の記録

2026-09-13: Phase 1〜7 の既存125件を含め、Phase 8追加後は139件成功・0件失敗。
solution build は警告0／エラー0。
WinUI smoke で初回起動、フォルダ binding、PasswordBox、close-to-tray、同window Open、
Tray Pause / Resume / Exit、background、single-instance と起動競合、TaskbarCreated 再登録、
primary のみの反復probe、隠れた状態でのprobe継続、Diagnostics実画面、終了時flushを確認した。

このsmokeのSession/probeは隔離fixtureであり、実サーバーupload、実Explorerプロセス再起動、
実Windows再ログイン、実ネットワーク断の代わりにはならない。
Open logs は実GUIから呼び出し、ExplorerのShell windowが隔離profileの正しいlogsフォルダを開いたことを確認した。
FolderPicker は実GUIから3回開いてcancelし、同じMainWindowが維持されクラッシュが起きなかったことを確認した。
初回の検査はダイアログのUI Automationツリー位置とCancelのInvokePattern非対応を想定しておらず失敗した。
所有window配下の実common dialogを検出し、IDCANCELの標準操作を送ることで検査が通った。アプリのpicker実装は変更していない。

### 2026-09-13 実 GUI / Startup E2E

ユーザーから専用フォルダの一時登録と自動起動の一時ON、終了後の復元について明示承認を受けた。
`scripts/FinalGuiE2E.ps1` は承認済みフォルダ、空の登録一覧、自動起動OFF、既存Run登録なしを事前条件とし、
finallyでsettings.jsonと既存backupを復元し、Run値を除去する。通常テストからは実行しない。

| ケース | 結果 |
|---|---|
| A | 通常起動、ウィンドウ表示、保存済み資格情報、専用Enabledフォルダ、実CLI Runningを確認 |
| B | ×でhide中に新規画像1枚が実upload、Tray維持、Openで同じMainWindowを確認 |
| C | Pause中に追加した1枚は15秒間uploadなし。Resume後にCLIの新規upload成功を確認 |
| D | Disable中に追加した1枚は15秒間uploadなし。Enable後にCLIの新規upload成功を確認 |
| E | GUI RestartでRunGeneration増加、旧launcher終了を確認 |
| F | 実CLI / Coordinatorの別試験で成功。GUI試験とネットワーク試験の証拠を区別する |
| G | Settings GUIからStart with WindowsをON、実HKCU Run値を確認。Exit後にそのcommandを手動起動し、window非表示、tray、実Session Running、通常secondaryによる同window表示を確認 |
| H | 実Tray Exit成功、icon除去、関連app / launcher / Node / CLI / server-info残存0件 |

実際のRun command:

```text
"C:\Users\katyo\source\repos\immich-auto-upload\src\ImmichDesktopUploader\bin\Debug\net10.0-windows10.0.19041.0\win-x64\ImmichDesktopUploader.exe" --background
```

ユーザーは同じアルバムの画像が合計4枚（最初の1枚＋GUI試験3枚）であることを画面で確認した。
実運用logsをflush後に保存済みキーと照合し、平文／JSON escapeの一致0件。
元のsettings.jsonをbyte単位で照合し、登録フォルダ0、自動起動OFF、Run登録なしへ復元済み。
設定復元の証拠は `.build/phase8-real-gui-328f3ce8044e409a91cc7da6216c7b45`、実行出力は `.build/phase8-real-gui.txt`。
資格情報ファイルは変更していない。実再ログインやWindows logoffはしていない。

準備用スクリプトでPowerShellのnull→空文字変換によるFile.Replace失敗と、稼働中ログの共有読取りモード不足があった。
それぞれアプリの操作前に停止し、元の状態を確認して修正後に再実行した。最終実行はA〜E/G/Hを完走した。

### 2026-09-13 実ネットワーク復旧 E2E

`--configured-network-e2e https://immich.kencir.blog` の明示入口のみで実行。
通常テストに含めず、保存済み資格情報を承認済みauthorityと照合して使用した。
実 AppCoordinator / UploadManager / UploadSession / Immich CLI / server-info を使用し、
settingsは試験プロセス内の専用1フォルダ分のみ。個人settings、registry、資格情報、Windowsのproxy / firewall / DNSは変更していない。

Node 24.21.0の `NODE_USE_ENV_PROXY=1` / `HTTPS_PROXY` をこの試験プロセスと子だけに適用し、
127.0.0.1のCONNECT中継を通した。中継は承認済みhost:443のみ許可し、TLS終端・証明書置換・復号・payload loggingはしない。
CLIは従来どおりPATH launcherで起動し、URLとConnectionGenerationは変えない。
参考: [Node 24 proxy support](https://nodejs.org/download/release/latest-v24.x/docs/api/http.html)。

1. `Watching for changes` と実server-info Reachableを確認してから、中継の既存通信を切断し、新規接続も拒否。
2. 接続状態Unavailableを確認。生存しているRunning CLIのRunGenerationが増えていないことを確認。
3. 切断中に明示Restartを行い、実CLIの起動失敗で既存2 / 5 / 10秒の再試行を使い切りError / RetryExhaustedになることを確認。
4. 中継を復旧。次の実server-info成功を契機に、同じConnectionGenerationで候補Sessionが自動再開。
5. 専用PNG1枚を追加し、実CLIの新規upload成功を確認。
6. 次の成功probeを待ち、RunGeneration不変を確認。flush後の `RecoveryAttempted` は正確に1件。
7. Coordinatorと中継をDispose。関連app / launcher / Node / CLI / server-info残存0件。
8. 保存済みキーを表示せず実ログと照合し、平文／JSON escape一致0件。

ユーザーがアルバム `ImmichDesktopUploader-Network-E2E` と画像1枚の表示を確認した。
証拠: `%TEMP%\ImmichNetworkE2E-a69896ae83024a859c5a704e0e11262a\logs`、`.build/phase8-network-e2e.txt`。

初回試験はwatch準備前に遮断し、SessionのErrorがConnectionMonitorの最初の失敗観測より先になった。
既存Phase 7の条件ではこのErrorは回復候補に含まれず、その試験は未完了として記録した。
既存の候補条件は変更していない。上の成功試験はwatch準備後に遮断し、失敗観測後の明示Restartで適格なErrorを作った。
この結果を「すべてのErrorがネットワーク復旧で自動再開する」という保証には使わない。

### 短時間ストレスとクラッシュ履歴

実file loggerを有効にし、仮想時間で100回のStart / Stop、10回のRestartを実行した。
110世代の間、同時active runの最大値は1、終了後active runと未完了timerは0。
これは1〜2時間の実測soakの代替ではない。実測用の手順は上記に記載済み。

2026-09-13に、過去30日分のWindows Application Error（Event ID 1000、最大300件）を読み取り、
実際に88件を確認した。その中にAppNameがImmichDesktopUploaderに一致する記録は0件だった。
この範囲ではfaulting moduleを追加特定できず、過去の画像報告に基づく未解決扱いを維持する。

最終スモークの一度でTray Pauseが反映されず、アプリログにはTrayMenuCancelledだけが残った。
テストのMSAA fallbackに使っていた投稿マウスメッセージを、検証済みpopup宛てのHome / Down / Enterへ変更した。
global keyboard injectionやカーソル移動は行わず、アプリのTray実装も変更していない。
修正後の全native smokeは3回連続成功した。出力は `.build/phase8-smoke.txt`、
`.build/phase8-smoke-repeat-1.txt`、`.build/phase8-smoke-repeat-2.txt`。
