# ComWrappersメモリリーク修正 Phase 1

2026年11月6日、JST。容量はMiB（1,048,576 B）。

**隔離GUIで、.NET 10.0.12の同一holder重複登録を再現し、10.0.14で解消を確認した。** 500msのUI通知とアプリの処理は変更していない。2フォルダ・600秒の最終採取では、管理表全体の重複参照が旧版867,613件、新版0件。新版のMainViewModelと各FolderViewModelは登録1件・異なるholder 1個・配列容量4を維持した。

本番PID 33444は終了・変更していない。同じ起動時刻（2026-11-05 15:23:46.7382819 JST）の継続稼働を通常権限で確認した。初回の制限環境からは見えなかったため再照会した。本番へのGC要求・追加ダンプ・実Immichサーバーへの負荷試験は行っていない。**この修正で、既に動いている旧ランタイムのプロセスのメモリが解放されるわけではない。**

## 変更と実行構成

| 項目 | 確認結果・変更 |
|---|---|
| SDK | 10.0.401でビルド・検証。SDK自体の更新は不要 |
| global.json | 存在せず。今回も追加なし |
| TargetFramework | `net10.0;net10.0-windows10.0.19041.0`を維持。GUIは後者、win-x64 |
| .NETの既定配布 | framework-dependentを維持。変更前のruntimeconfig要求は10.0.0、実ロードは10.0.12 |
| ランタイム指定 | GUIの`FrameworkReference Update="Microsoft.NETCore.App"`に`RuntimeFrameworkVersion="10.0.14"`を設定。`RollForward=LatestPatch`で10.0.14以上の10.0系パッチを選択 |
| 指定範囲 | プロジェクト全体のRuntimeFrameworkVersionは使用しない。Windows SDK参照に誤って10.0.14が適用されることを避けるため、CoreCLR参照だけに指定 |
| Windows App SDK | 1.8.260101001、`WindowsAppSDKSelfContained=true`を維持。.NETのSelfContainedとは別設定 |
| Windows SDK BuildTools | 10.0.26100.4654を維持 |
| その他NuGet | ProtectedData 10.0.0、Serilog.Extensions.Logging 10.0.0、Sinks.Async 2.1.0、Sinks.File 7.0.0を維持 |
| self-contained発行 | `--self-contained true`で.NET 10.0.14を同梱することを検証。アプリ固有PRIを発行対象へ追加 |
| UI・接続処理 | 周期、通知、Dispatcher、CLIの設定・実装は変更なし |

ランタイムは[公式配布メタデータ](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)で確認した10.0.14を採用。隔離用ZIPは同メタデータのSHA-512と照合した。重複登録修正は[dotnet/runtime PR 133292](https://github.com/dotnet/runtime/pull/133292)で、[10.0.13](https://github.com/dotnet/runtime/releases/tag/v10.0.13)に含まれる。

WinUIのunpackaged発行では、最初のpublishにアプリ固有PRIが含まれず、画面生成時にXamlParseExceptionとなった。生成済みの`ProjectPriFullPath`を`ResolvedFileToPublish`へ追加して解決した。コンパイル済みXAMLはPRI内にあり、別途XBFを一括コピーする必要はなかった。[上流の同種報告](https://github.com/microsoft/WindowsAppSDK/issues/6720)とも一致する。最終発行物は画面表示だけでなく、設定画面、フォルダ選択、トレイ、終了まで検証した。

## 実際にロードしたランタイム

| 構成 | PID | coreclr.dll製品バージョン | ロード元 |
|---|---:|---|---|
| 旧版・フォルダ0 | 32396 | 10.0.1226.42308 | 共有Microsoft.NETCore.App/10.0.12 |
| 新版・フォルダ0 | 17860 | 10.0.1426.52326 | 隔離dotnet/shared/Microsoft.NETCore.App/10.0.14 |
| 旧版・フォルダ2 | 67968 | 10.0.1226.42308 | 共有Microsoft.NETCore.App/10.0.12 |
| 新版・フォルダ2 | 38772 | 10.0.1426.52326 | 隔離dotnet/shared/Microsoft.NETCore.App/10.0.14 |
| 最終self-contained発行物 | 11948 | 10.0.1426.52326 | 発行先release/coreclr.dll |

比較試験ではスタートアップフックから`Environment.Version`、ロード済みモジュールのパス・製品版、CoreLibのパスを記録し、終了前にも外部からモジュールパスを照合した。新旧ともWorkstation GC。最終発行物も外部からロード済みcoreclr.dllを確認した。SDKの版だけで判定していない。

共有ランタイムは更新していない。10.0.12だけを持つ通常のdotnetホストで更新後のframework-dependent DLLを起動すると、10.0.14不足として終了することを検証済み（終了コード-2147450730）。通常ビルドの利用には対応ランタイムの導入が必要。今回の検証済みself-contained発行物は`.build/runtime-fix-20261106/release/`で、共有ランタイムの更新を必要としない。本番の自動起動先や実行中プロセスの切替は行っていない。

## 比較方法

同一Releaseビルドを2つの隔離ディレクトリへコピーし、旧版側のruntimeconfigだけを10.0.12／roll-forward無効にした。アプリexe、アプリDLL、Microsoft.UI.Xaml.dll、WinRT.Runtime.dllのSHA-256一致を確認。新旧の差は実行ランタイムとその選択設定。各条件の新旧プロセスは同時に開始した。

- 前回の所有者確認に対応する`--smoke-test-ready --background`、フォルダ0、模擬probeのみ、200秒。実サーバー接続なし。
- `--smoke-test-folders --background`、同じ2つの無効なサンプルフォルダ、資格情報なし、アップロードなし、600秒。未設定状態なのでアプリの通常動作としてウィンドウが表示される。新旧とも同じ表示条件・サイズで、測定対象にはUI操作を行わない。
- 全起動で一意の`--smoke-instance`を使用。本番インスタンスへの転送を避け、新規smokeプロファイルを使用した。
- 5秒間隔でPrivate Bytes・GC統計を採取。15/45/90/150/200秒および600秒の直前・Gen2収集後を記録。200秒後から600秒の最終収集までは強制GCなし。
- `s_allManagedObjectWrapperTable`を列挙し、各Listをロックして全要素を参照同一性で数える。重複数は登録件数−異なるholder数。値や文字列内容は出力しない。列挙結果にキー・holder自体を保存せず、型・件数・容量だけを残した。
- 2フォルダ条件のMainViewModel sequenceは、新旧とも200秒で401、600秒で1201。増分800回／400秒で一致し、UI更新が止まったことで重複が消えたのではない。

初回の2フォルダ試験は、進捗表示が書き込み中のJSONを読む際の競合で約120秒に中断した。自分で起動した試験プロセスのみを終了し、進捗表示をOSのプロセスメモリ参照へ変更して再実行した。以下は完走した試験の結果で、中断試験は集計しない。

## 重複登録と配列容量

フォルダ0のMainViewModelは、15→200秒で旧版7,683→101,293件（異なるholderは常に1）、新版は1→1件。旧版の増加率は506件/秒、前回の再現試験と一致。新版は採取した全Listで重複0件だった。

2フォルダ条件、600秒の最終GC後:

| 対象 | 旧版登録件数 | 旧版重複数 | 旧版配列容量 | 新版登録件数／異なるholder／重複数／容量 |
|---|---:|---:|---:|---|
| MainViewModel | 303,992 | 303,991 | 524,288 | 1 / 1 / 0 / 4 |
| FolderViewModel A | 244,599 | 244,598 | 262,144 | 1 / 1 / 0 / 4 |
| FolderViewModel B | 244,598 | 244,597 | 262,144 | 1 / 1 / 0 / 4 |
| 管理表全体 | — | 867,613 | — | 重複0 |

旧版の上記3個のListはすべて異なるholderが各1個だけだった。新版では途中の管理表採取も含め重複0。最終時点の管理表内のLOHサイズのholder配列は旧版4本・8,650,848 B（約8.25 MiB）、新版0本。これがPrivate Bytesだけに依存しない根本原因の改善確認になる。

## LOHとPrivate Bytes

以下は同じ時点のGen2収集後。LOH使用部分は`SizeAfterBytes − FragmentationAfterBytes`であり、全オブジェクトの厳密な到達可能性解析ではない。

| 条件・時点 | 旧版 LOH / 使用部分 / Private | 新版 LOH / 使用部分 / Private |
|---|---|---|
| フォルダ0・15秒 | 0.00 / 0.00 / 99.83 | 0.00 / 0.00 / 102.03 |
| フォルダ0・200秒 | 2.00 / 1.25 / 115.53 | 0.25 / 0.25 / 114.40 |
| フォルダ2・15秒 | 0.12 / 0.12 / 106.98 | 0.12 / 0.12 / 105.71 |
| フォルダ2・200秒 | 6.25 / 4.38 / 117.16 | 1.76 / 1.25 / 113.60 |
| フォルダ2・600秒 | 21.31 / 9.50 / 141.40 | 3.75 / 1.38 / 124.53 |

単位はすべてMiB。最終時点で新版のLOH使用部分は旧版より約8.12 MiB少なく、LOHのholder配列が約8.25 MiB減る結果と整合する。ただしその他のオブジェクトも変動しており、差額の完全な一致を主張しない。

強制GCなしのアイドル区間は205～595秒、各79標本。両者とも自然なGen2が1回あった。Privateは旧版117.10→139.92 MiB（範囲117.10～141.43）、新版113.55→125.33 MiB（範囲113.54～127.84）。新版でもPrivateは完全な横ばいではない。600秒の収集後は124.53 MiBになった。GC統計は直近GC時点の情報で、GC間のLOH瞬時値ではない。観測フックの割り当て、通常のWinUI処理、ネイティブ領域・アロケータ保持も含むため、この短時間試験だけで全種類のメモリ増加の解消や長時間の上限は保証しない。

## テストと発行の検証

- 既存console runner: .NET 10.0.12、10.0.14で各139成功・0失敗。実Immich E2E用オプションは使用していない。
- GUIのReleaseビルドとself-contained発行が成功。最終publishは警告・エラーなし。
- 旧版framework-dependent、新版framework-dependent、最終self-containedの3構成で、同じ最終版`SmokeTest-WinUI.ps1`が成功。表示・設定・フォルダカード・FolderPickerの3回キャンセル・Diagnostics・ログ表示・トレイ・バックグラウンド・単一起動・同時起動・明示終了とflushを含む。
- smokeに`-AppPath`を追加し、検証対象を明示できるようにした。新旧で再現したFolderPickerのUI Automationキャンセル不成立には、2秒後も表示中の場合のみ標準IDCANCELを送る既存フォールバックを拡張。pickerが別brokerプロセスの場合も、観測したpickerのPIDとHWNDを照合して限定する。製品のUI処理は変更しない。
- 古い共有ランタイムで新版framework-dependentを起動した場合の拒否と、最終self-containedの実coreclrロードを確認した。

制限環境ではNuGetのキャッシュ・脆弱性情報アクセスや子プロセス操作が失敗したため、必要なビルド・テストを通常権限で実行した。最初のビルド警告や、修正前のpublish／smoke失敗を成功結果に含めていない。

## 追加UI改善の評価と残存リスク

今回のRuntime修正と混ぜず、UI通知の最適化は未実装・効果未測定とした。

| 改善候補 | 評価・次の独立測定 |
|---|---|
| 値が変わらないプロパティの通知抑制 | 優先候補。前後の表示値・CanExecute条件を比較し、通知数、COM境界の呼び出し、CPU、割り当てを測る |
| Dispatcherへの重複投入防止 | 保留callbackを最大1件にする設計を評価。投入拒否・Dispose・競合時にも最終状態を失わないことを検証する |
| 未処理更新の集約 | 最新Snapshotを保持してUI再開時に反映。エラー等の重要な遷移を落とさず、キュー長と反映遅延を測る |
| 500ms周期の適正化 | まず上記の効果を分離し、その後に周期を比較。応答性、進捗表示の遅れ、CPUを評価する |

今回の合格範囲はComWrappersの同一holder重複登録の解消と既存動作の互換性。10分を超える常駐・多数フォルダ・実アップロード時の全メモリ挙動、CLI側のバッチ間並列、過去の6.9 GBの全原因は未検証。本番PID 33444は旧ランタイムで稼働を続けており、別途切替・再起動するまで修正は適用されない。

## Gitと証拠の扱い

開始時に追跡ファイルの差分はなく、既存のメモリ調査報告4件だけが未追跡だった。これらは編集・ステージしていない。今回の診断用コード、JSON、テストログ、ランタイムZIP、発行物はGit除外済みの`.build/runtime-fix-20261106/`に保持。新しいダンプは採取していない。

コミット対象はプロジェクト設定、README、スモークテスト2本、この要約報告のみ。実認証情報、ダンプ、生ログ、個人の監視パス、写真名を含めない。コミットは論理単位に分け、Pushは行わない。コミットSHAは完了報告に記載する。
