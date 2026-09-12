# Immich Desktop Uploader — Phase 1

Windows 11 / x64 向けの **process foundation のみ**。WinUI 3 Unpackaged の最小ウィンドウと、既存 CLI launcher の起動・出力取得・ツリー停止を実装しています。

UploadSession、UploadManager、再試行、設定保存、DPAPI、トレイ、自動起動、ConnectionMonitor、アップロード用 GUI は未実装です。実アップロード・API 呼出し・FileSystemWatcher・CLI/Node 自動インストールは行いません。

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

## Verified results (2026-09-12)

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

## Next phase

次に進める場合は、IProcessRun を使う UploadSession の状態遷移・RunGeneration・再試行を fake backend で実装する段階です。Phase 1 の作業では着手しません。
