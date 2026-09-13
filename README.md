# Immich Desktop Uploader — Phase 1–5

Windows 11 / x64 向け。プロセス基盤、設定保存、DPAPI、UploadManagerに加え、Phase 5の **WinUI GUI / ViewModel連携** を実装しています。Settingsから接続情報を保存し、フォルダ追加・編集・無効化・再起動を操作できます。

通常起動では保存済み設定・資格情報を読み、有効なフォルダを開始します。window closeはCLI cleanup後にapp exitです。トレイ、自動起動、ConnectionMonitorは未実装です。通常テストは隔離データを使い、実アップロードを行いません。API直接呼出し・FileSystemWatcher・CLI/Node 自動インストールは行いません。

## Build / run

必要環境: Windows 11 x64、.NET 10 SDK。WinUI 本体は Microsoft.WindowsAppSDK 1.8.260101001 と Microsoft.Windows.SDK.BuildTools 10.0.26100.4654 を NuGet 復元します。実機確認には Visual Studio 2026 Community のある環境を使用しました。

リポジトリルートから:

```powershell
dotnet build ImmichDesktopUploader.sln
& ./src/ImmichDesktopUploader/bin/Debug/net10.0-windows10.0.19041.0/win-x64/ImmichDesktopUploader.exe
```

WinUI の Windows App SDK 依存は self-contained、.NET ランタイムは framework-dependent です。インストーラーや MSIX はありません。

本体プロジェクトは二つのターゲットを持ちます。

| ターゲット | 用途 |
|---|---|
| `net10.0-windows10.0.19041.0` | WinUI 3 本体 |
| `net10.0` | 同じ基盤ソースを UI なしで参照するテスト用ライブラリ。プロセス実装の実行は Windows 専用 |

`-p:FoundationOnly=true` を指定すると WinUI 依存を復元せず基盤とテストだけをビルドします。

## Automated tests

```powershell
./scripts/Test.ps1
./scripts/SmokeTest-WinUI.ps1
```

テストは追加のテストフレームワーク依存を持たないコンソールランナーです。上の `Test.ps1` がこの solution の **`dotnet test` 相当コマンド**で、失敗時は非ゼロで終了します。`dotnet test` 単独ではこのランナーを実行しません。

個別に実行する場合:

```powershell
dotnet build tests/ImmichDesktopUploader.Tests -p:FoundationOnly=true
& ./tests/ImmichDesktopUploader.Tests/bin/Debug/net10.0/ImmichDesktopUploader.Tests.exe
```

Smoke test は先に solution 全体をビルドした状態で実行してください。最小 WinUI ウィンドウの生成、応答、閉じる操作と exit code 0 を確認します。

## Architecture / key classes

| 型 | 責務 |
|---|---|
| `CliLauncherResolver` | PATH 順に絶対パスの launcher を検出 |
| `LauncherCommandBuilder` | executable / command line / environment を構築、入力制約を検証 |
| `CliEnvironmentBuilder` | 親環境のコピーと IMMICH_* 隔離 |
| `ProcessStartSpecification` | 不変の起動入力。ToString に環境やキーを展開しない |
| `WindowsProcessRunner` | Native リソースの確保、Job 所属付き CreateProcess、所有権移譲 |
| `IProcessRun` | Application 側の境界。RootProcessId / Output / WaitForExitAsync / StopAsync / DisposeAsync |
| `WindowsProcessRun` | パイプ読取、終了監視、有限時間 cleanup と終了結果 |
| `NativeMethods` / `StartupAttributes` | P/Invoke、SafeHandle、ネイティブ属性リスト |

Application 境界に `System.Diagnostics.Process` や Job handle を公開していません。実行中の PID は診断用で、停止は所有している Job / process handle のみで行います。

## CLI launcher resolution / arguments

PATH の各ディレクトリを順番に調べ、同一ディレクトリでは `immich.exe`、`immich.cmd`、`immich.bat` の順で選びます。空・相対 PATH 要素は現在ディレクトリの暗黙探索を避けるため無視します。検出失敗は明示的な FileNotFoundException です。

package.json / bin の解析や Node 直接起動はしません。`.exe` は直接起動、`.cmd` / `.bat` は SystemDirectory の `cmd.exe /d /s /v:off /c` 経由です。`start`、PowerShell、シェルでの環境変数設定は使いません。

| 入力 | EXE | CMD / BAT |
|---|---|---|
| 空白・日本語・空引数 | 対応 | 対応 |
| 末尾バックスラッシュ | 対応 | 対応（テスト済み） |
| `" % ! ^ & \| < > ( )` | CRT 引数規則で対応 | 明示的に拒否 |
| 制御文字（改行・NUL・タブ等） | 拒否 | 拒否 |

CMD 制約は launcher 自身のパスにも適用します。batch の `%*` 再展開を含めた普遍的なエスケープを約束せず、未対応文字を含む値は実行前の ArgumentException とします。入力値自体を例外に含めません。CMD は8191文字未満、直接実行は32767文字未満に制限します。

任意の独自 launcher の切り離し動作や引数書換えまで保証しません。検証対象の launcher は通常のプロセス生成で CLI を起動し、終了を待つものです。

## Environment isolation / credentials

親環境をコピーし、大小文字を区別せず全 `IMMICH_*`（`IMMICH_CONFIG_DIR` も含む）を取り除きます。認証付き呼出しのみ `IMMICH_INSTANCE_URL` / `IMMICH_API_KEY` を加えます。両方の値が必要です。

watch / recursive / album / ignore / concurrency / progress 等は呼出側が引数として渡す設計です。Phase 1 は upload 設定モデルを実装していません。version/help には認証を渡しません。親環境を変更しません。

API Key はコマンドラインに入れず、ネイティブ環境ブロックは使用後に全領域をゼロ化します。子がキーを平文出力した場合は出力境界で `[REDACTED]` に置換し、チャンクをまたぐキーも保持バッファで検出します。変換・エンコードされた秘密の検出や、実行中の親/子メモリを読める同一ユーザーからの秘匿を保証する機能ではありません。

## Process creation / Job ownership

実行ごとに匿名 Job を作り、`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` を設定します。breakaway フラグは設定しません。

`STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_JOB_LIST` によって **CreateProcess 時点から Job に所属**します。作成後に AssignProcessToJobObject する race window はありません。Job 構成や属性設定・作成が失敗すると StartAsync は失敗し、確保済みリソースを解放します。

Job handle は非継承。`PROC_THREAD_ATTRIBUTE_HANDLE_LIST` で stdin の NUL と stdout/stderr の書込 handle のみを継承します。出力には、読み側が overlapped I/O に対応したユーザー専用 named pipe を使います。

```
IProcessRun (one run)
└─ Job (owned by the application)
   └─ cmd.exe (RootProcessId)
      └─ launcher / Volta / Node / descendants
```

Job 内の PID 一覧取得は内部のテスト診断だけで使い、停止には使いません。

## stdout / stderr

二つのパイプを独立して非同期読取します。行単位ではなく最大4096文字のチャンクで通知するため、改行なしの長大出力で行バッファが増え続けません。UTF-8 を使用します。

`ChannelReader<ProcessOutput>` を後続ロガーの境界にします。Source と Timestamp を持ちます。キューは1024チャンク、満杯時は古いチャンクを破棄し、`DroppedOutputChunks` を終了結果で返します。読み手が遅くても stdout/stderr の排出を止めません。全ログの無損失保持は保証しません。

stderr に書かれただけでは失敗にしません。読取例外は観測し、監視側へ通知してツリーを cleanup します。UI callback を読取ループから直接呼びません。

## Stop / cleanup

StopAsync は冪等で、同時呼出しは一つの終了 Task を待ちます。停止の要求後に caller token がキャンセルされても、cleanup 自体は継続します。WaitForExitAsync のキャンセルは待機のみを取り消し、子を停止しません。

1. Stop または root の終了・読取障害を検出する。
2. TerminateJobObject を実行する。自然終了でも残った子孫を cleanup する。
3. Job の ActiveProcesses = 0 と root handle の signaled 状態を確認する。
4. 残ったパイプ出力を回収する。2〜4の待機は同じ5秒予算を使用する。
5. 読取をキャンセル・パイプを閉じ、読取 Task を観測して handle を解放する。

最後の managed disposal/cancellation 完了には OS / ランタイムのスケジューリングが必要であり、5秒の厳密なリアルタイム保証ではありません。任意の無限読取 Stream を受け入れる設計ではなく、キャンセル可能な named pipe を所有します。

`ProcessExitResult` は ExitCode、StopRequested、TreeExited、OutputDrained、DroppedOutputChunks、CleanupError を返します。`TreeExited=false` や CleanupError を成功と扱わないでください。Stop 中の exit code 1 は強制終了用のコードであり、アプリの停止意図とは別です。通常終了・非ゼロ終了の root code は維持します。

アプリが強制終了しても OS が最後の Job handle を閉じてツリーを終了させます。DisposeAsync は停止と同じ cleanup を行います。

## ProcessTestHost

以下のモードを持つテスト専用実行ファイルです。

| モード | 動作 |
|---|---|
| `stdout N` / `stderr N` / `both N` | 1024文字+改行をN回出力 |
| `long N` | 両方へ1024文字×N回、改行なし |
| `exit CODE` | 指定コードで即終了 |
| `wait MS` / `forever` | 指定時間待機 / 無限待機 |
| `tree DEPTH` | 子孫生成、PID 出力、待機 |
| `orphan DEPTH` | 子孫生成後 root だけ終了 |
| `owner` / `owner-close` | Job 所有親の強制終了 / 通常プロセス終了を検証 |
| `args` / `env` / `env-hash` / `secret-split` | 引数・環境・秘密の分割出力検証 |

## Real Immich CLI verification

`Test.ps1` は PATH 上に CLI があれば、実 launcher で次を行います。

1. 検出絶対パスを表示。
2. `--version` と `upload --help` の stdout/stderr、exit code、cleanup を確認。
3. もう一度 help を起動し、Job の PID 一覧に実 Node child が存在することを確認。
4. Node が動いている間に Stop を要求し、ツリー終了と観測した PID の消滅を確認。

認証情報・実アップロードは不要です。CLI が見つからない場合だけ以下を出します。検出できたが実行に失敗した場合はテスト失敗であり、skip にしません。

```
Immich CLI実機検証: skipped
Reason: CLI not found
```

## Phase 1 verified results (2026-09-12)

- .NET SDK 10.0.401、Windows 11 x64。
- solution build: warnings 0 / errors 0。
- WinUI: ウィンドウ生成・応答・閉じる・exit code 0 を確認。
- 基盤と ProcessTestHost 統合: 13テストグループ成功。
- 実 Immich 統合を含めた合計: 14成功、0失敗。
- launcher: `%LocalAppData%\Volta\bin\immich.cmd`、CLI 3.2.0。
- version/help は exit code 0、stderr 0。実 Node child の Job 所属を確認。
- 実 CLI の Stop 後、観測した launcher / Volta / Node PID は残存なし。
- Node 直接起動への変更は不要。

通常の制限付き実行環境では NuGet.Config と Volta launcher へのアクセスが拒否されました。アクセス可能な実行環境でビルド・実 CLI を再検証しています。システム全体の権限変更、CLI/Node のインストールはしていません。

昇格した実行環境からの WinUI smoke test は1回、ウィンドウ生成成功後の終了待ちが5秒でタイムアウトしました（スクリプトが対象を cleanup）。通常のデスクトップ実行環境で同じスクリプトを再実行すると起動・応答・終了コード0まで成功しました。差の原因は未特定です。WinUI の通常実行成功と、CLI 統合テストの成功は分けて記録しています。

## Phase 2 — UploadSession / State Machine / Retry Foundation

### Boundary and immutable state

`Application/UploadSession.cs` と `UploadSessionModels.cs` を追加しました。Phase 1 の process foundation 本体には変更を加えていません。

```
UploadSession (one folder)
    ↓ IUploadBackend.StartAsync(UploadRunRequest, CancellationToken)
    ↓ IProcessRun (Phase 1 contract)
```

`UploadSessionConfiguration` は FolderId と Path の immutable record のみ。`UploadRunRequest` は設定スナップショットと RunGeneration を持ちます。CLI の引数・認証・Windows Process・Job handle は UploadSession にありません。実 backend の実装は次フェーズです。

公開状態は immutable `SessionSnapshot` です。FolderId、Status、RunGeneration、LauncherPid、RetryCount、LastStartedAt、LastActivityAt、LastError、StopReason を含み、`Snapshot` を atomic に差し替えます。

`Changes` は最新64件の snapshot 通知キュー（単一 consumer 用、broadcast ではない）です。遅い consumer は古い通知を失う場合があります。現在値の正本は `Snapshot` です。UI の DispatcherQueue への転送は将来の UI 層の責務です。

LastError は日時・種別・固定の安全な要約・任意の exit code を持ち、Running 復帰後にも保持します。backend の例外 Message / ToString や ProcessStartSpecification 全体を公開状態へコピーしません。出力内容は解析・保存せず、出力を受信した時刻だけ LastActivityAt に反映します。stdout/stderr を成功判定に使いません。出力通知は coalesce し、1 run あたり未処理 activity event を最大1件に抑えます。

### Public operation semantics

| 操作 | 挙動 / await の意味 |
|---|---|
| `StartAsync` | Stopped から開始要求。要求が event loop に受理されたら完了。Starting / Running / Restarting / Stopping / Error では冪等な no-op |
| `RestartAsync` | retry count を0にし、旧実行を意図的に停止して新世代を開始。要求受理まで待機。新 run が Running になるか開始失敗するまで、同じ restart 操作の連打を coalesce |
| `ApplyConfigurationAsync` | 同じ FolderId の新設定を採用し、SettingsChanged 理由で明示的に再スタート。要求受理まで待機。停止中の連続変更は最新設定を次の run に使用 |
| `StopAsync` | desired running を先に false にし、timer を無効化。開始処理・現 run の cleanup 完了まで待機。cleanup 失敗は安全な固定メッセージの例外 |
| `DisposeAsync` | 新要求を拒否、停止、全 background work の観測、event loop 終了まで待機。繰り返しは同じ終了 Task を返す |

Start/Restart の await は Running 到達を意味しません。Snapshot / Changes で結果を確認します。Stop の待機中に後続の明示的 Restart が受理された場合、Stop は旧実行の cleanup 完了を待ち、後続要求は新実行を開始できます。

StopReason は UserRequested / SettingsChanged / ApplicationShutdown / Disabled / Removed / Paused。Manual Restart の停止理由には UserRequested を使います。新しい run を開始すると現在の StopReason は null になります。

ApplyConfigurationAsync は**明示的な再スタート API**です。設定の保存や Enabled/Pause の管理 API ではありません。Pause 中に保存だけ行う調整は将来の所有側で行います。

### State machine

| 状態 | 意味 |
|---|---|
| Stopped | 所有中の start/run/cleanup がなく、retry も pending ではない |
| Starting | backend の開始処理を保留中 |
| Running | 現世代の run を取得済みで、終了をまだ観測していない。接続・watch 準備・upload 成功は保証しない |
| Restarting | 予期しない失敗後の backoff 中 |
| Stopping | start のキャンセル結果回収、または run の cleanup 中。観測障害後の安全な cleanup にも使用 |
| Error | non-retryable failure、retry 上限、または cleanup failure |

基本遷移: `Stopped → Starting → Running`、`Running → Stopping → Restarting → Starting`、`Stop → Stopping → Stopped`。Phase 1 の exit result を受けた場合も DisposeAsync の完了を確認するため、一時的に Stopping を通ります。3回目の retry run が失敗した後は Error です。

意図的停止理由のない終了は **exit code 0 でも非ゼロでも UnexpectedExit** です。Phase 1 の結果にある StopRequested は Session 自身の停止意図を上書きしません。

### Retry and stable-run reset

初回とは別に最大3回。**RetryCount は予約済み retry の番号を意味し、backoff 開始時点で増加**します。Phase 1 設計時の「再起動直前に消費」ではなく、Phase 2 の確定仕様に合わせた定義です。

| 状況 | RetryCount | 次の開始まで |
|---|---:|---:|
| 初回失敗 | 1 | 2秒 |
| retry 1 失敗 | 2 | 5秒 |
| retry 2 失敗 | 3 | 10秒 |
| retry 3 失敗 | 3 | Error。自動開始なし |

Manual Restart、明示的な設定再適用、同一 run の30秒安定稼働で0に戻します。Stop や通常の Start だけでは count をリセットしません。手動再起動の開始自体が失敗した後も、backoff 中に再度 Manual Restart できます。

Running 到達時の monotonic timestamp から30秒 timer を開始します。29秒ではリセットせず、30秒 timer のイベント時に現世代・Running・非停止中を確認し、monotonic elapsed time が30秒以上なら reset。run の再生成はしません。これは retry 制御上の生存条件であり、通信状態の確認ではありません。

`UploadBackendException` の `BackendFailureKind.NonRetryable` は即 Error、Retryable とその他の開始例外は retry 対象です。大きな例外階層はありません。生存観測・出力読取の失敗は現在の run を停止・dispose してから retry します。

### Serialization / generation / ownership

状態変更は `Channel<Message>` の single-reader async event loop 一箇所だけで行います。Start/Stop/Restart、backend start 成功・失敗、exit、観測障害、retry/stable timer、activity、cleanup 完了を同じループで処理します。

backend 呼出し・プロセス終了待機・Stop/Dispose は別 Task、timer は非同期 delay と完了イベントです。event loop はこれらを await せず、10秒 backoff や終了待機で停止しません。独自の UI thread blocking や `.Result` / `.Wait()` はありません。

各 start attempt に単調増加する RunGeneration を割り当てます。start 成功/失敗、exit、観測失敗、activity、cleanup は実行 context と世代、retry/stable timer は世代と timer version を持ちます。現 context と一致しないイベントは状態を変更しません。timer version は同じ世代の Stop/Restart 中にも古い callback を拒否します。

新 run を作る箇所は BeginStart 一箇所です。現 context は **開始要求が保留中でも、cleanup が保留中でも保持**します。Stop/Restart で start token をキャンセルしても、backend が無視して成功を返した場合はその run を引き取り、Running へ公開せず停止・dispose します。これが終わるまで次の世代を作りません。

Stop はまず desired running を false、context を retired とし、その後 StopAsync を呼びます。したがって Stop 呼出しから即座に exit が返っても retry しません。受付側には短い lock と intent sequence の fence もあり、新しい Stop/Restart/Dispose がキューに入っている間、旧 timer event から backend 開始を予約しません。この lock 内で外部の開始処理を実行しません。

cleanup は TreeExited と run.DisposeAsync 成功を確認して所有権を解放します。cleanup が不完全なら自動再試行はしません。終了・解放を確認できない run は Error のまま所有し続け、Start/Restart/設定再適用から新 run を生成しない quarantine とします。その Session の DisposeAsync も成功扱いにせず例外を返します。Error の LauncherPid はこの未確認の所有対象を示すことがあります。

### Cancellation / disposal

- Session lifetime、run context、retry delay、stable timer の CTS を分離します。
- Stop/Restart は run context と timer をキャンセルします。cleanup へ caller token は渡しません。
- 公開操作の caller cancellation は要求を撤回せず、caller の待機だけをキャンセルします。
- Dispose 開始時点で新しい操作の受付を閉じます。Dispose 後の Start/Restart/Stop/設定再適用は ObjectDisposedException です。
- Dispose は保留中 start の結果と cleanup を回収してから event loop を閉じ、追跡している全 background Task を await します。旧世代の遅れた observer も放棄しません。
- worker は結果または安全な障害イベントを返します。完了済み worker は追跡表から除去し、長期間の retry で Task リストを増やし続けません。

backend は開始失敗前の部分的な生成物を自身で cleanup する契約です。また、backend / IProcessRun / clock は呼出しを最終的に完了させる必要があります。協調しない backend を強制的に打ち切って新 run を重ねる機能はありません。安全な所有権を優先するため、そうした backend が永久に応答しなければ Session の Stop/Dispose も完了しません。Phase 1 実装は自身の有限 cleanup を持っています。

### Fake backend / fake clock / tests

追加ファイルは `tests/ImmichDesktopUploader.Tests/Sessions/` の3ファイルです。

`FakeUploadBackend` は開始要求・設定・世代・token を記録し、成功/失敗/保留をテスト側で選びます。FakeProcessRun は PID、任意 exit code、Stop/Dispose の保留・失敗、遅れた exit、観測障害、出力を制御します。active run 数と最大同時数を記録し、各 fixture の終了時に **active = 0 / maximum <= 1** を検証します。

`ISessionClock` の本番実装は .NET TimeProvider と Task.Delay を使用します。FakeSessionClock は仮想時刻を進めるだけで2/5/10/30秒を検証します。キャンセル後に遅れて完了する timer も再現します。テストの15秒 watchdog はデッドロック検出だけに使い、業務時間の待機には使いません。

内部の mailbox fence と retired-generation work fence により、古い exit がまだ event loop に届いていない段階で「無視できた」と判定しません。公開 backend API をテスト専用に拡張していません。

25テストグループで、ライフサイクル、Start/Stop/Restart 連打、0/非ゼロ終了、3回上限、29/30秒境界、旧 timer/exit、開始保留中の操作、設定の差替え、例外分類、cleanup failure、caller cancellation、Dispose と未完了 worker を検証しています。

同じ `scripts/Test.ps1` が Phase 1 と Phase 2 の両方を実行します。Phase 2 に実 CLI upload やネットワーク回復処理はありません。

### Phase 2 verified results (2026-09-12)

| 検証 | 結果 |
|---|---|
| `dotnet build ImmichDesktopUploader.sln` | 成功、警告0・エラー0 |
| `scripts/Test.ps1` — Phase 1 回帰 | 14成功。実 Immich CLI 3.2.0 の version/help・Job 所属・Stop 後の PID 消滅を含む |
| `scripts/Test.ps1` — Phase 2 | 25成功、fake clock で業務時間を制御 |
| 自動テスト合計 | **39成功、0失敗** |
| `scripts/SmokeTest-WinUI.ps1` | 通常環境で起動・応答・閉じる・exit code 0 |

テスト teardown は緊急の fake cleanup **より前**に active run 数と pending timer 数を検証します。Dispose のテストでは、run cleanup が Stopped に到達した後も、旧 observer が未完了なら Dispose が完了しないことを確認しています。

Phase 1 の本体コードと scripts は無変更です。既存テストランナーには Phase 2 テスト呼出しを追加しただけです。NuGet / 実 launcher の検証はアクセス可能な実行環境で、WinUI smoke test は通常環境で実行しました。Phase 2 の未実行ゲートはありません。実画像アップロードはスコープ外なので行っていません。

## Phase 3: single-folder CLI backend

`UploadSession → IUploadBackend → ImmichCliBackend → ImmichUploadCommandBuilder → LauncherCommandBuilder → WindowsProcessRunner` を接続しました。Session は CLI オプションや認証方法を知りません。Backend は検証・互換性確認・起動だけを担当し、成功時に実 `IProcessRun` の所有権を Session に渡します。再試行は既存 Session の責務です。

### Configuration / credentials

`UploadRunRequest` の形は維持し、`UploadSessionConfiguration` に次の immutable な入力だけを追加しました。

| 入力 | 初期値 |
|---|---|
| Recursive | true |
| AlbumName | null |
| IgnorePatterns | 空の ImmutableArray<string> |
| Concurrency | 2 |

FolderId / Path は従来どおりです。Enabled、UI状態、接続情報、保存用情報は追加していません。

接続情報は別の `ImmichConnectionSettings` に保持します。公開の読み取り専用 ServerUrl と内部の読み取り専用 API Key を持ち、自動生成 ToString が秘密を出す record は使用しません。キーの永続保存はありません。ToString・通常の JSON シリアライズ・例外・Session snapshot・診断情報にキーを出さず、起動引数にも渡しません。

Phase 1 の `CliEnvironmentBuilder` を再利用し、親環境を変更せず、子環境から大文字小文字を問わずすべての `IMMICH_*` を除去します。upload にだけアプリの `IMMICH_INSTANCE_URL` / `IMMICH_API_KEY` を設定します。E2E専用変数も子へ継承しません。version/help確認は認証変数なしです。stdout/stderr は Phase 1 の非同期取得と秘密のマスクをそのまま使用します。

### Upload arguments and CLI compatibility

実機の PATH launcher（Volta の `immich.cmd`）、Immich CLI **3.2.0** の `upload --help` で次の正式オプションを確認しました。

| 設定 | 生成する引数 |
|---|---|
| 常時 | `upload --watch` |
| Recursive=true | `--recursive`（falseなら省略） |
| AlbumNameあり | `--album-name "値"` |
| IgnorePatterns 1件 | `--ignore "pattern"` |
| IgnorePatterns 複数 | `--ignore "{pattern1,pattern2}"` |
| Concurrency | `--concurrency 2` 等 |
| progress抑制 | 対応時に `--no-progress` |
| 最後 | `-- "絶対フォルダパス"` |

AlbumName は null / 空 / 空白のみなら省略し、それ以外では前後空白も保持します。IgnorePatterns が空なら省略します。Concurrency は正の Int32 とし、追加の上限は設けません。CLI側のデフォルト値には依存せず、アプリ初期値2を明示します。

**ignore は繰り返し指定や単なるカンマ区切りではありません。** help の `--ignore <pattern>` は単一文字列です。インストール済み3.2.0の実装で、初回走査が fast-glob、watch が micromatch を使用することを確認し、複数の単純なパターンを設定順に brace alternation へまとめます。意味の変化を避けるため、複数指定の各要素に `{` / `}` / `,` / バックスラッシュ、または先頭 `!` がある場合は検証エラーにします。単一の `**/*.{jpg,png}` のようなパターンは保持します。CLI全体のglob言語をアプリ側で再実装することはしません。

参考: [Immich CLI](https://docs.immich.app/features/command-line-interface/)、[micromatch braces](https://github.com/micromatch/micromatch#braces)、[fast-glob pattern syntax](https://github.com/mrmlnc/fast-glob#pattern-syntax)。互換性判定の実機基準は上記3.2.0です。

Backend 初期化時に `--version` と `upload --help` を一度実行し、成功した機能確認をインスタンス内でキャッシュします。同時初期化は直列化し、各 Start で help を再実行しません。失敗・キャンセルはキャッシュしません。確認は各15秒、出力上限64 Ki文字で、子プロセスの終了・cleanup・出力回収も確認します。必須オプション不足や未知のignore引数形式は NonRetryable、`--no-progress` だけ未対応なら省略します。バージョンの完全固定はしません。CLI更新後はBackendを作り直して再確認します。

起動は Phase 1 の PATH launcher方式です。npmのpackage.json/bin解析やNode直接起動は追加していません。任意のlauncherパス固定も可能ですが、プロセス起動自体は常に同じ本番実装を使います。

### Validation / diagnostics

- URL: absolute http(s)、userinfo/query/fragmentなし。末尾slashだけを除去し、hostやpathを書き換えず、`/api` を自動追加しません。3.2.0 CLI自身のdiscoveryと入力URLへのfallbackに任せます。手動検証にはサーバーの正しいAPIベースURL（通常は `/api` を含む）を指定します。
- Folder: absolute、存在するdirectory、直下の列挙が可能であることを起動前に確認します。子孫全ファイルの読取保証や、検証後のアクセス権変更の防止までは行いません。
- 値: 制御文字・オプションと誤認し得る先頭 `-` などを拒否します。CMD経由では Phase 1 の特殊文字制限も維持します。日本語・空白を含むパスは対応します。
- NonRetryable: launcher不在、URL/キー/フォルダ/concurrency/引数の不備、必須CLI機能不足。
- Retryable: ネイティブプロセス作成失敗、互換性probeの実行失敗。開始後の終了・観測障害は既存Sessionが処理します。

例外階層を増やさず、既存 `UploadBackendException` に任意の固定 `BackendErrorCode` を追加しました。生の入力や内部例外は含めません。将来のログ連携用に、launcher path、値を省いたcommand summary、FolderId、RunGeneration、launcher PIDを bounded channel（32件、古い項目を破棄）で提供します。

`--delete`、`--delete-duplicates`、その他削除動作、`--skip-hash` は生成しません。任意CLI引数の追加口もありません。認証情報入りのコマンドをログ出力する処理はありません。

### Automated verification (2026-09-12)

| 検証 | 結果 |
|---|---|
| solution build | 成功、警告0・エラー0 |
| `scripts/Test.ps1` Phase 1 | 14成功 |
| 同 Phase 2 | 25成功 |
| 同 Phase 3 | 11成功 |
| 合計 | **50成功、0失敗** |
| `scripts/SmokeTest-WinUI.ps1` | 起動・応答・閉じる・exit code 0 |
| 実CLI | 3.2.0、version/help成功、no-progress対応、Job所属とStop後のPID消滅を確認 |
| `scripts/Upload-E2E.ps1` | skipped（資格情報未設定） |

Phase 3 は引数の完全一致、設定初期値、URL正規化、キー秘匿、親環境隔離、空白/日本語パス、CMD拒否文字、読取拒否ACL、必須機能不足、互換性キャッシュ、probeキャンセル後のcleanupと再初期化、CreateProcess失敗分類を検証します。ACLテストは生成した一時フォルダだけを対象にし、元のアクセス規則への復元も確認します。

本番Backendと安全な `ProcessTestHost` launcherを接続し、実IProcessRun、SessionのStarting/Running/Stopped、Job内の子孫終了、予期しないexitから2/5/10秒の3回再試行も検証します。通常テストの実Immich呼出しはversion/helpだけで、サーバーへの接続や実写真のuploadは行いません。

Phase 1 本体と Phase 2 `UploadSession` は無変更です。Phase 2モデルの互換的拡張と既存テスト入口への追加だけを行いました。既存のsingle event loop、RunGeneration、stable reset、Stop intent、cleanup quarantine、active run <= 1を維持しています。

### Opt-in / manual single-folder E2E

通常の `scripts/Test.ps1` から独立した `scripts/Upload-E2E.ps1` を使います。実サーバー検証には対話可能なPowerShellと明示的な資格情報が必要です。次の例ではキーを対話入力し、コマンド履歴やリポジトリへ保存しません。

```powershell
$env:IMMICH_E2E_SERVER_URL = Read-Host 'Immich API base URL'
$e2eSecureKey = Read-Host 'E2E API key' -AsSecureString
try {
    $env:IMMICH_E2E_API_KEY = [System.Net.NetworkCredential]::new('', $e2eSecureKey).Password
    & .\scripts\Upload-E2E.ps1
} finally {
    Remove-Item Env:IMMICH_E2E_API_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:IMMICH_E2E_SERVER_URL -ErrorAction SilentlyContinue
    $e2eSecureKey.Dispose()
}
```

1. スクリプトはCLI互換性を確認し、毎回新しい空の `%TEMP%\ImmichDesktopUploader-E2E-<GUID>` を作ります。既存フォルダを指定する入力口はありません。
2. 固定album名 `ImmichDesktopUploader-E2E`、recursive=true、concurrency=2でSessionを開始し、Runningとlauncher PID、実際の一時フォルダパスを表示します。
3. 表示されたフォルダへ、アップロードしてよいテスト画像を**1枚だけ**コピーします。Runningはプロセス起動の確認であり、watch準備完了・upload成功の保証ではありません。
4. CLIのwatch待ち時間（3.2.0では約10秒のバッチ待ち）も考慮し、Immich画面で画像のuploadとテストalbumへの所属を確認してEnterを押します。
5. 同じ画像のバイト列を別ファイル名で再投入し、Immich画面で重複挙動とalbum所属を確認してEnterを押します。アプリから削除や重複判定の無効化は行いません。
6. Session Stop後のStoppedとプロセス終了を確認します。Jobのtree cleanupに加えてlauncher PID消滅を確認し、Task Managerでも対応するlauncher/Nodeが残っていないことを確認できます。
7. 中断はCtrl+Cです。失敗時もSessionをDisposeします。ローカル一時フォルダとサーバー上のテスト画像は確認用に残し、自動削除しません。

Enterによる確認はユーザーによる手動確認として扱い、APIで検証済みとは表示しません。標準入力がredirectされている場合は実E2Eを開始しません。キーは実行中のメモリと子環境に存在しますが、出力やファイルには保存しません。

今回の実行結果:

```text
Immich upload E2E: skipped
Reason: E2E credentials not provided
```

したがって、実サーバーでのupload・album・重複挙動は今回未検証です。ユーザーの既存写真はアップロードしていません。

### Files / limitations / next phase

追加: `Infrastructure/Immich/ImmichConnectionSettings.cs`、`ImmichUploadCommandBuilder.cs`、`ImmichCliBackend.cs`、`tests/ImmichDesktopUploader.Tests/Immich/` のbackend testsとE2E入口、`tests/ProcessTestHost/ImmichFixture.cs`、`scripts/Upload-E2E.ps1`。

変更: `Application/UploadSessionModels.cs`、両テストプロジェクトの `Program.cs`、本README。コミットは自動実行しません。

CLI出力の文面からupload成功やwatch準備完了を推定しません。Phase 1のストリームマスクは、静かなプロセスの末尾出力を一時保持することがあります。そのためtreeテストは出力PIDの到着に依存せずJobのメンバーを検査します。ignoreの複雑な複数パターン、CMD制限文字、サーバーバージョン間の実upload互換性には上記制約があります。

Phase 3時点ではUploadManagerと設定管理は次の候補でした。現在の実装は以下のPhase 4を参照してください。

## Phase 4: Settings Persistence / DPAPI / UploadManager

### Settings model and storage

保存先は `AppStoragePaths` で一元化します。既定は次の3ファイルで、テスト時だけ専用の一時ディレクトリを注入します。

```text
%LocalAppData%\ImmichDesktopUploader\settings.json
%LocalAppData%\ImmichDesktopUploader\settings.json.bak
%LocalAppData%\ImmichDesktopUploader\credentials.dat
```

`AppSettings` / `UploadFolderSettings` はimmutable record、Folders / IgnorePatternsはImmutableArrayです。フォルダIdは `UploadFolderSettings.Create(path)` で新規作成時だけ生成し、編集では既存Idを維持します。デシリアライズ時に不足Idを自動生成しません。保存されるJSONの例（架空のURLとId）:

```json
{
  "schemaVersion": 1,
  "serverUrl": "https://immich.example.com/api",
  "startWithWindows": false,
  "folders": [
    {
      "id": "458bf5ca-5a16-4fb1-9397-c38a4244b681",
      "path": "C:\\Pictures\\VRChat",
      "enabled": true,
      "recursive": true,
      "albumName": null,
      "ignorePatterns": [],
      "concurrency": 2
    }
  ]
}
```

Enabled=true、Recursive=true、Concurrency=2、AlbumName=null、IgnorePatterns=[] が初期値です。StartWithWindowsは保存だけで、レジストリを操作しません。API Key、Pause状態、SessionSnapshotはJSONに含めません。

`SettingsValidation` はschema=1、Phase 3と共通のURL検証、GUID重複、絶対パス、パス重複、正のconcurrencyを検証します。フォルダの存在は保存時には要求せず、起動時のPhase 3 backendで確認します。URLは末尾slashのみ正規化し、`/api`を追加しません。

比較用PathKeyは `Path.GetFullPath` → separatorをバックスラッシュへ統一 → root以外の末尾separator除去、比較はOrdinalIgnoreCaseです。大文字小文字、`/`と`\`、末尾slash、`.`を含む表現の差は重複として拒否します。親子判定はseparator境界を使い、`Pictures` と `PicturesElse` を親子と誤認しません。親子フォルダは許可し、`ValidatedSettings.Warnings` とManager snapshotのWarningsに親子のFolderIdを返します。

### Atomic save / corruption / schema

`SettingsService` はload/validate/save/recoveryのみを担当し、Sessionを操作しません。

1. draftを検証してserializeする。
2. 同じディレクトリの一意なtemporary fileへ書く。
3. FlushAsyncとFlush(flushToDisk:true)を完了する。
4. 初回はMove、既存ファイルにはFile.Replaceを使用する。
5. 置換前の正常なprimaryを `settings.json.bak` に残す。

通常Saveは既存primaryも検証してから置換します。サービス内の操作は直列化し、`.lock` ファイルをFileShare.Noneで開いて別SettingsServiceの同時書込みも拒否します。ロックファイルは空のまま残り、所有権はファイルハンドルにあります。書込み権限・共有違反などはStorageFailureで返し、直接上書きへfallbackしません。temporary fileはfinallyで回収します。

ファイルなしはMissingSettingsです。既定設定の自動保存や自動uploadはしません。破損JSON、必須項目不足、重複JSONプロパティ、未対応schemaは明確なfailureとして返します。未知のJSON項目も拒否し、誤ってキー等を通常設定として扱いません。schemaが1以外ならUnsupportedSchemaで、通常Saveは禁止です。Phase 4にmigrationはありません。

primaryを読めない場合、有効な `.bak` があれば `SettingsLoadResult.RecoveryCandidate` を返しますが、自動採用はしません。明示的な `RecoverBackupAsync()` だけが復旧し、拒否したprimaryは `.rejected-<GUID>` に保存します。元の `.bak` は維持します。future schemaのprimaryは復旧APIでも上書きしません。復旧後も資格情報の整合性確認が必要です。

### CredentialService / consistency

`CredentialService` はAPI Keyと対応ServerUrl、内部format versionをまとめてJSON bytesへ変換し、Windows DPAPIの **DataProtectionScope.CurrentUser** で暗号化して `credentials.dat` へatomic保存します。同じWindowsユーザーで復号します。これは[.NET ProtectedData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata)を使用する実装で、パッケージは `System.Security.Cryptography.ProtectedData 10.0.0` です。

復号後、共通のURL正規化を行い、settings.ServerUrlとOrdinalで一致する場合だけ `ImmichConnectionSettings` を返します。hostの大文字小文字等も含め、意味上同じでも表記が異なるURLは保守的にmismatchとなる場合があります。別URLへ古いキーを転用することはありません。

- ファイルなし: MissingCredentials。
- DPAPI復号失敗・不正payload: InvalidCredentials。平文fallbackなし、再入力が必要です。
- URL不一致: CredentialMismatch。Sessionは1つも開始しません。
- byte bufferは使用後にZeroMemoryします。接続に必要なstringだけを保持し、完全なstringゼロ化は保証しません。
- キーは通常JSON、ToString、例外、診断イベント、Manager/Session snapshotに出しません。内部のpayloadもToStringでは値を表示しません。

### UploadManager / factory / diff

`IManagedUploadSession` は既存UploadSessionの公開操作を表す最小interfaceです。UploadSessionの状態機械は変更せず、このinterfaceを実装する宣言だけを追加しました。`IUploadSessionFactory.Create(configuration)` でManagerから生成を分離します。

本番 `UploadSessionFactory` は同一接続に対して1つのImmichCliBackendを共有します。Backendの機能確認はSemaphoreSlimで直列化・キャッシュ済みで、WindowsProcessRunnerの各Startは独立したJob/handlesを所有します。SessionごとのRetry/RunGeneration/cleanupは共有しません。

UploadManagerはFolderIdをキーにSessionを所有します。StartAllでEnabledだけを開始し、DisabledもStoppedのSessionとして保持します。バックエンド開始失敗は各SessionのErrorとなり、他フォルダの開始を妨げません。ApplySettingsは事前に全draftを検証し、以下の差分だけを適用します。

| 差分 | 動作 |
|---|---|
| 変更なし / StartWithWindowsのみ | Session操作なし |
| 追加 | Session作成。実行要求中かつEnabledでPause中でなければ開始 |
| Path / Recursive / AlbumName / IgnorePatterns / Concurrency変更 | 同じFolderIdのSessionだけ再設定 |
| Enabled true→false | Stop(Disabled) |
| Enabled false→true | 実行要求・Pause状態に従って開始 |
| 削除 | Stop(Removed) → Dispose → dictionaryから削除 |

IgnorePatternsは配列の参照でなく順序を含む内容で比較します。Path変更でも同じFolderIdなら同じSessionです。Disabled・Pause・StopAll中の編集は保留し、次の開始時に最新configurationを渡します。これは既存ApplyConfigurationAsyncが再起動を伴うためです。保留中の設定はManager snapshotに反映されます。

削除cleanupが失敗した場合はidentityを隔離して保持し、同Idの新SessionやManual Restartを許可しません。安全に所有権を確認できない状態を成功扱いにしません。

### Pause / Resume / shutdown

Pauseはruntime-onlyでEnabledを保存変更しません。Pause時点でErrorではないSessionをStop(Paused)します。既にErrorだったSessionはErrorを維持し、Resume対象から外します。手動Restartで明示的に解除します。DisabledまたはPause中の手動Restartは拒否します。

Pause中に追加・編集したEnabledフォルダはResume時に開始できます。StopAllは実行要求自体を解除するので、その後の編集やResumeだけで停止済みフォルダを起動しません。StartAllまたは対象へのManual Restartが必要です。

Manager操作は順序付きのasync queueで直列化します。公開APIのCancellationTokenは呼出し側の待機だけを取り消し、受理済み操作やcleanupを放棄しません。すべての内部Taskを観測します。Start/ApplyのSession操作発行とDisposeの受付終了を同じlockで保護します。

StopAllは全Sessionを並列停止し、`ManagerOperationResult.FailedFolderIds` に全失敗を集約します。Disposeは受付を即時終了し、先行操作の終了を待ち、全SessionのStop(ApplicationShutdown)とDisposeを個別に観測してdictionaryを空にします。一部でもcleanup失敗があればCleanupFailedを返します。Dispose後の新操作はObjectDisposedExceptionです。cleanup不能な外部プロセスが絶対に存在しないと偽って報告することはありません。

Snapshotはimmutableなフォルダ設定と各Sessionの最新Snapshot、IsPaused、RunningRequested、Warningsを返します。ResumeBlockedでPause前のErrorによる抑止も確認できます。可変Session辞書を公開しません。

### AppCoordinator / save ordering

Application層のAppCoordinatorが、SettingsServiceとCredentialService、Managerの順序を管理します。Startは次の順です。

```text
settings load/validate → credentials load/URL照合 → shared backend factory → manager → Enabled start
```

どこかで設定・資格情報が不正ならManagerを生成しません。server-info、接続状態polling、ネットワーク回復は含めません。

Updateは既存primaryの破損・未対応schemaも事前確認してから次の順で進め、保存が完了するまで現在のSessionを操作しません。未対応schemaではcredentialsも変更しません。

```text
draft validation
→ 新credentialsを指定した場合はそのURL照合とDPAPI保存
→ settings atomic save
→ 両ファイルを再loadし、draft・URL・期待するcredentialとの一致を確認
→ runtimeへ差分適用
```

通常のフォルダ変更は既存Managerに差分適用します。URLまたはキーが変わる場合だけ、全旧SessionをStop/Disposeしてから新しい共有backend contextとManagerへ切替えます。Pause/StopAll状態とResumeBlockedは引き継ぎます。cleanupに失敗した場合、新contextは作りません。

2ファイルの分散transactionや秘密の自動rollbackは実装しません。credentials保存成功後にsettings保存が失敗しても、旧runtimeは旧immutable connectionで継続し、新設定を部分適用しません。次回起動時にURLが違えばmismatchで停止します。同じURLのキー更新だけが保存された場合は、次回起動はその新キーを使います。settings先行・credential失敗の手動編集状態もURL照合で拒否します。

初回のUpdateは保存だけで、暗黙にuploadを始めません。保存後にStartを呼びます。開始後のStartAll/StopAll/PauseAll/ResumeAll/RestartはCoordinator経由でも利用できます。現在のGUIには結線していません。

### Diagnostics / limitations

`AppDiagnostics.Events` は128件のbounded channelで、settings/credentials load/save、session追加/削除/変更、pause/resume、manager start/stop、整合性failureを固定enumとFolderIdで返します。値や秘密を含む例外をログ化しません。ファイルloggerは未実装です。

設定の保存前validationと保存失敗に対してruntimeを維持しますが、保存後に起きた実プロセスのcleanup失敗まで全セッションを巻き戻すtransactionはありません。その場合はfailureを返して新しい所有権の生成を止めます。外部からの設定手編集や複数アプリによる協調しない書込みを統合する機能もありません。

atomic saveは同一volumeでのWindows File.Replace/Moveを前提とし、ストレージ機器故障などに対する完全な電源断保証はしません。junction/symlink/8.3名の同一実体判定、古いschema migration、キーのメモリ完全消去はスコープ外です。

### Verified results (2026-09-13)

| 検証 | 結果 |
|---|---|
| solution build | 成功、警告0・エラー0 |
| Phase 1 regression | 14成功 |
| Phase 2 regression | 25成功 |
| Phase 3 regression | 11成功 |
| Phase 4 | 25成功（保存/DPAPI 7、Manager 10、Coordinator 8） |
| `scripts/Test.ps1` 合計 | **75成功、0失敗** |
| WinUI smoke | 起動・応答・終了、exit code 0 |
| 実CLI version/help | 3.2.0、既存Job所属・Stop検証成功 |
| Phase 3 E2E入口 | ビルド・起動成功、資格情報なしで指定どおりskip |

追加テストには、first run、atomic replacementと前版backup、backupをロックした保存失敗で両ファイル維持、破損/未来schemaの上書き拒否、明示復旧、パス重複/親子warning、実DPAPI roundtripと暗号化bytes検査、URL mismatch、秘密が通常設定・シリアライズ・診断へ出ないことを含みます。

Managerはfake session/factoryで差分、Enabled、Pause/Error、並行Apply、shutdown fence、cleanup failureを決定的に検証します。さらに本番UploadSessionとfake backend/clockを使って、Starting中の削除が遅いrunを回収すること、retry中の編集が旧generationを起動しないことを検証します。Coordinatorは実filesystem+DPAPIとも接続し、保存失敗によるruntime部分適用なし、URL/key変更時の所有権切替、Pause/Error維持を確認します。実uploadは行っていません。

Phase 3テスト1件は、終了直後のEXE mappingによる共有違反を避けるため、旧テストEXEをrenameしてから同じpin先に不正EXEを作るよう変更しました。CreateProcess failureの検証内容は維持しています。Phase 3のE2Eスクリプトと実行コードは無変更です。今回の結果は `Immich upload E2E: skipped / Reason: E2E credentials not provided` であり、過去の手動E2Eを再実行したという意味ではありません。

### Files changed / next phase

追加ファイル:

- `Application/AppSettings.cs` — 設定、検証、warning、固定failure。
- `Application/AppDiagnostics.cs` — 秘密を含まないイベント境界。
- `Application/UploadSessionFactory.cs` — 最小Session interfaceと共有backend factory。
- `Application/UploadManager.cs` — 複数Session管理。
- `Application/AppCoordinator.cs` — 保存・資格情報・runtimeの順序管理。
- `Infrastructure/Persistence/AtomicFile.cs` / `SettingsService.cs` / `CredentialService.cs` — 永続化。
- `tests/ImmichDesktopUploader.Tests/Management/` — fake、保存、Manager、Coordinatorテスト。

変更はUploadSessionのinterface宣言、ImmichConnectionSettingsのURL検証共通化と内部比較、DPAPIパッケージ参照、既存テスト入口、Phase 3のテスト準備1件、本READMEです。Phase 1のプロセス基盤とPhase 2の状態機械は維持しました。コミットは実行していません。

Phase 4時点ではGUI連携は次の候補でした。現在の実装は以下のPhase 5を参照してください。

## Phase 5: WinUI GUI / ViewModel Integration

### Architecture / ownership

```text
MainWindow / FolderEditorDialog / SettingsDialog
  → MainViewModel / FolderViewModel / editor drafts / AsyncCommand
  → IDesktopApplication / DesktopApplicationService
  → AppCoordinator / UploadManager
  → UploadSession / ImmichCliBackend
```

MVVMは追加パッケージを使わず、INotifyPropertyChangedとICommandの最小実装です。observable properties、async commandのbusy/CanExecute、draft validation、snapshot projectionを分けました。ViewModelはSessionを直接操作せず、Application層の窓口だけを使用します。

DesktopApplicationServiceがSettingsService・CredentialService・AppCoordinatorの寿命をまとめます。MainViewModelがこの窓口を所有し、MainWindowのcloseからDisposeします。MainWindow.xaml.csにはwindow lifecycle、dialog表示、FolderPickerのHWND bridge、DispatcherQueue bridge、設定フォルダを開くUI処理だけを置いています。設定検証、JSON書込み、UploadSessionの操作は置いていません。

### Main view / operations

Main画面にはServer URL、Credentials configured / missing、Managerの実行要求・Pause状態、global error、フォルダ一覧を表示します。Connected/Disconnectedという接続状態は表示しません。RunningはCLIプロセスの稼働状態で、サーバー接続やupload成功の保証ではありません。

各行はEnabled/Disabled、パス末尾の表示名、Path、Album、Status、Retry、Last activity、安全なLastErrorを表示します。状態名の変換はUiText.Statusに集約しました。Error状態のLastErrorはError InfoBar、Running復帰後の残存エラーはPrevious errorとしてInformational表示です。

- Add folder: Windows FolderPickerで選択後、Folder Editorを開きます。Pickerキャンセルでは保存しません。
- Edit: Path、Enabled、Recursive、AlbumName、IgnorePatterns、Concurrencyをdraftで編集します。Path変更でもFolderIdを維持します。
- Enable/Disable: AppCoordinatorの保存・差分適用を通します。Disabledなら該当Sessionを停止し、他フォルダは再起動しません。
- Restart: 指定FolderIdだけへ依頼します。Errorからの手動再試行にも使えます。Disabled/Pause中や起動・再起動・停止処理中は無効です。
- Remove: 確認dialogの承認後、設定から対象だけを外します。ローカル画像・Immich assetsは削除しないと明示します。
- Pause All / Resume All: Enabledを変更せずPhase 4のAPIへ委譲します。Pause前からErrorだったSessionはResumeでは再試行しません。

FolderPickerには[WinUI desktop向けInitializeWithWindow](https://learn.microsoft.com/en-us/windows/apps/develop/ui/display-ui-objects)でowner HWNDを設定します。ContentDialogは同じwindowのXamlRootを使用します。MainWindowは標準Border/Card、Button、InfoBar、ProgressRing、ListViewとsystem themeを使います。

### Draft validation / credentials UX

Save前はruntimeへ反映しません。保存はvalidation → persist → reload/consistency → runtime applyの既存順序です。保存失敗ならdialogを閉じず、入力draftを保持して修正可能なメッセージを表示します。

Concurrencyは数値用InputScopeの入力欄で、1以上のInt32だけを許可します。Application層の検証も維持します。exact duplicate pathは保存不可、親子overlapは関連パスを警告表示して保存を許可します。パスの存在確認は実行時のbackendにも残っています。

IgnorePatternsは1行1パターンです。CRLF/LF/CRを分割し、空行・空白だけの行を除外しますが、残すパターンの前後空白はtrimしません。AlbumNameも有効な文字を含む値の前後空白を保持します。Phase 3の複数glob/CMD文字の制約は引き続き適用されます。

SettingsのAPI KeyはPasswordBoxで更新時だけ入力します。保存済みキーを復号して画面に戻す処理はありません。ViewModelにも公開のキーpropertyを設けず、入力bridgeは書込み専用です。

- 空欄は既存キーを維持します。削除機能は今回追加していません。
- Server URL変更時、または資格情報が未設定・利用不可の場合は新キーを必須にします。
- Save成功またはdialogを閉じると入力キー参照を解放し、PasswordBoxを空にします。Save失敗時は再入力を避けるためdraft内に保持します。
- Start with Windowsは保存済み値を表示するdisabled controlで、Available in a later phaseと明記しています。
- 内部例外、stack trace、ProcessStartSpecificationをUIへ表示しません。

### Snapshot synchronization / lifetime / errors

DesktopApplicationServiceは500ms周期で**メモリ上の**Manager snapshotを読みます。ネットワークpollingやserver-infoではありません。各発行に単調増加Sequenceを付け、MainViewModelは[DispatcherQueue.TryEnqueue](https://learn.microsoft.com/en-us/windows/apps/develop/performance/keep-ui-thread-responsive)でUI threadへmarshalしてObservableCollectionへ投影します。受理済みSequence以下を破棄するので、古いsnapshotがSave後の状態や削除後の一覧を上書きしません。

MainViewModelだけが通知を購読し、行ViewModelはSessionを購読しません。削除時に行のcommandを無効化し、MainViewModel Disposeで通知を解除します。既にenqueueされたcallbackもdisposedを確認して破棄します。Applicationのpolling taskはキャンセル・awaitしてからCoordinatorをDisposeします。

global IsBusy、行のRestart/Remove command busy、dialog Save busyで重複実行を防ぎます。Save中はeditor内容を無効化します。UIにWait()/Resultはありません。操作エラーは次の操作まで表示し、周期snapshotですぐ消しません。

初期化失敗でもwindowは表示し、自動アップロード未開始と修正方法を提示します。有効なbackup候補がある場合だけRestore backupを表示します。Restoreには確認があり、復旧後に資格情報を再確認してEnabledフォルダを開始します。未対応schemaではRestoreを表示せず、Open settings folderから確認できます。

window closeでは終了を一度キャンセルし、開いているdialogを閉じて進行中Saveを待ち、MainViewModel → DesktopApplicationService → AppCoordinator → Manager → SessionのDisposeをawaitした後に閉じます。cleanup失敗時は成功扱いで終了せず、windowにエラーを表示します。trayへの最小化はありません。

UiFolderAdded/Edited/Removed、UiRestart、UiPauseResume、UiSettingsSaved/SaveFailed、UiActionFailed、UiInitializationFailedを固定enumの診断境界へ流せます。キーや入力値は含めず、完全なfile loggerは追加していません。

### Tests / verified results (2026-09-13)

| 検証 | 結果 |
|---|---|
| solution build | 成功、警告0・エラー0 |
| Phase 1–4 regression | 75成功 |
| Phase 5 | 12成功（ViewModel/command/draft 9、Application bridge/lifetime 3） |
| `scripts/Test.ps1` 合計 | **87成功、0失敗** |
| WinUI smoke | 隔離したfirst-runと2フォルダ表示の両方でMainView/Settings PasswordBox/終了を確認 |
| 実プロセスcleanup | ViewModelからのDisposeで実Job treeのPID消滅を確認 |
| Phase 3 E2E入口 | ビルド・起動成功、`Immich upload E2E: skipped / Reason: E2E credentials not provided` |

ViewModelテストは初期/空/複数行、background更新のUI queue経由反映、逆順配送のstale破棄、行削除・購読解除、Pause/Resume、初期化エラー、Remove確認、Restart対象、重複command抑止を検証します。draftテストは初期値・Id維持・全編集項目・改行分割・重複/overlap・数値不正・保存失敗保持・キー秘匿・URL変更ルールを検証します。

Application bridgeは実filesystem/DPAPIを使い、初回設定からの開始、保存失敗時のruntime維持、Disable、backup recoveryを検証します。native lifetimeテストはProcessTestHostだけを実行し、実写真や実サーバーを使いません。

smoke testはWindows標準UI Automationを使用し、MainViewの要素と実際のbinding、2つのdisabled sample folder card、Settings dialogのPasswordBoxを検査します。dialogを開いたままwindow closeしてexit code 0を確認します。`--smoke-test` / `--smoke-test-folders` は毎回新しい一時profileを使い、個人設定を読みません。サンプルは資格情報なし・Disabledなので実uploadは起動しません。FolderPickerの選択操作や実サーバーuploadの自動UIテストは含めません。

### Manual GUI E2E

既存VRChat/写真フォルダを使わず、空の専用テストフォルダを新規作成して行ってください。通常GUI起動には上記Build/runのexeを使用し、smoke-test引数は付けません。

1. Settingsを開き、正しいServer URLと新API Keyを入力してSaveします。Credentials configuredを確認します。
2. 新しい空の専用フォルダ（例: `C:\Temp\ImmichDesktopUploader-GUI-E2E-<任意の新しい名前>`）を用意します。
3. Add folderのPickerでそのフォルダだけを選びます。
4. Folder EditorでEnabled=true、Recursive=true、AlbumName=`ImmichDesktopUploader-GUI-E2E`、Concurrency=2としてSaveします。
5. 行のRunningを確認します。これはupload成功の確認ではありません。
6. uploadしてよいテスト画像1枚だけをコピーします。CLIのwatch待ち時間も考慮し、Immich画面で画像とalbum所属を確認します。
7. Editでalbum等を変更し、同じフォルダ行の設定が更新されることを確認できます。複数の専用テストフォルダを使う場合は、他の行が再起動されないことも確認できます。
8. Disableを押してStoppedを確認します。Task Managerでも対象launcher/Nodeが終了したことを確認します。
9. 必要ならRemove確認で登録を外します。ローカル画像とサーバー画像は残ります。
10. windowを閉じ、アプリと管理CLIが残らないことを確認します。

今回実施したのは自動テストと隔離GUI smokeです。実サーバーを使うGUI E2E、FolderPickerの対話選択、実画像uploadは未実施です。Phase 3のUpload-E2E.ps1と実行コードは変更していません。

### Files / limitations / next phase

追加: `Application/DesktopApplicationService.cs`、`ViewModels/Mvvm.cs` / `MainViewModel.cs` / `Editors.cs`、`Views/FolderEditorDialog.*` / `SettingsDialog.*` / `UiBridges.cs` / `SmokeTestProfile.cs`、`tests/ImmichDesktopUploader.Tests/Gui/`。

変更: App/MainWindowのXAMLとcode-behind、AppDiagnosticsのUIイベント、net10.0テストターゲットからUIコードを除外するcsproj、テスト入口、SmokeTest-WinUI.ps1、本README。既存のSettings/DPAPI/Manager/Session/process動作は変更していません。コミットは実行していません。

状態反映には最大約500msの遅延があります。ディスク上の設定を外部編集した場合の自動reload、接続状態監視、toast、tray、Windows自動起動はありません。保存後のプロセスcleanup失敗まで全runtimeをrollbackするtransactionはPhase 4同様にありません。次の候補はtrayとwindow lifetimeの拡張ですが、今回その実装には進んでいません。
