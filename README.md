# Immich Desktop Uploader — Phase 1 / Phase 2 / Phase 3

Windows 11 / x64 向け。Phase 1 の process foundation、Phase 2 の単一 UploadSession の状態機械・再試行基盤、Phase 3 の **ImmichCliBackend / upload command / 単一フォルダの手動E2E入口**を実装しています。WinUI は最小ウィンドウのままです。

UploadManager、設定保存、DPAPI、トレイ、自動起動、ConnectionMonitor、アップロード用 GUI は未実装です。実アップロードは明示的な資格情報を使う手動E2E入口に限定しています。API直接呼出し・FileSystemWatcher・CLI/Node 自動インストールは行いません。

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

次の候補はUploadManagerと設定管理の設計・実装です。Phase 3では複数フォルダ管理、設定保存、DPAPI、ConnectionMonitor、server-info polling、ネットワーク回復、GUI、tray、自動起動へ進んでいません。
