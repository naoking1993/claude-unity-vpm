# MCP for Unity 10.3 への更新評価

評価日：2026-10-23。上流のコード（`git clone https://github.com/CoplayDev/unity-mcp`、タグ `v9.7.1`＝`78ee5418`、`v10.3.0`＝`aa5fc638`）を実際に読み、PyPI のメタデータとリリースノートと照合した。Unity 実機での確認はしていない（**未検証**の項目は明記した）。

## 結論

**推奨：9.7.101 据え置き。10.3.x は今はリスティングに入れない。**

- 日本語パスの不具合（`mcp-terminal.cmd` を cmd.exe が CP932 で読み、`--pidfile` が文字化けする）は、10.3.0 の既定の Start では起きない（コード上の判断）。サーバーの起動がバッチファイルを書かない方式に変わったため。10.3 に移るときに JP パッチは要らない。
- 一方で、Unity 側パッケージだけ 10.3.0 にすると、いま動いているサーバー（FastMCP 4 改修版 wheel、9.7.1 ベース）との**組み合わせが未検証の混在構成**になる。上流 10.3.0 のサーバーは今も `fastmcp<4` で、FastMCP 4 のまま揃えるにはサーバー側の移植が別に要る。
- 無改変の 10.3.0 は `GitUrlOverride` が空だと PyPI の `mcpforunityserver==10.3.0`（FastMCP 3）で**黙って起動する**。9.7.101 の「わざと起動に失敗する」安全策がなくなる。リスティングに入れた時点で、全購入者の VCC に更新として表示され、新規プロジェクトの既定にもなる。
- Unity を2つ開いたときの `set_active_instance` のエラー（`Persistent Unity selection requires _meta.client_id on MCP 2026 requests`）は、手元の FastMCP 4 wheel 側のメッセージ。上流のコードには無い。Unity 側の版を上げても直らない。

10.3 に移るのは、先にサーバーの方針を決め（下の「10.3.x に移る条件」）、実機確認を済ませてから。そのときは JP パッチなしの中身で収録できる。ただし、変更・追加したファイルの zip 日時を新しくすることが必須（下の (c)）。

## 根拠

### (a) 日本語パス不具合は 10.3.0 に残るか

| | 9.7.1 | 10.3.0 |
| --- | --- | --- |
| Start Server の起動経路 | `ServerManagementService.cs:321` → `TerminalLauncher.CreateTerminalProcessStartInfo` → `mcp-terminal.cmd` を書いて `cmd.exe /c start … cmd.exe /k <cmd>` | `ServerManagementService.cs:347` → `TerminalLauncher.CreateHeadlessProcessStartInfo` → `cmd.exe /c "<command> < NUL >> "<log>" 2>&1"`（窓なし） |
| 起動コマンドの渡し方 | BOM なし UTF-8 のファイル。cmd.exe はコンソールのコードページ（日本語 Windows では CP932）で読む | `ProcessStartInfo.Arguments`（`UseShellExecute=false` なので CreateProcessW の UTF-16 コマンドライン）。ファイルを経由しない |
| `--pidfile` の日本語パス | 文字化けして隣の別フォルダーに pid ができる → Stop Server が止められない | 文字コードの変換が入らないため、そのまま届く見込み |

- 10.3.0 の Stop の流れ（`StopLocalHttpServerInternal`）は 9.7.1 と同じ。ハンドシェイクに保存した pid ファイルを読み、PID・待ち受けポート・起動トークンが合えば止める。pid ファイルが正しい場所にできれば止まる。
- `TerminalLauncher.cs:107-122`（Windows 分岐）には、9.7.1 と同じバイト列の `.cmd` 書き出し（chcp なし）がまだ残っている。ただし 10.3.0 では呼び出し元がない（`ServerManagementService.cs:992` の private ラッパーは未使用）。潜在的な不具合なので、上流向けの下書きを [`upstream-issue-draft.md`](upstream-issue-draft.md) に残した（投稿はしていない）。
- 窓なし起動は 9.7.3（`d1092118`）で入った。ただし Windows では 10.1.2（`b954897e`、上流 #1279）まで「The handle is invalid. (os error 6)」で起動に失敗していた。10.0.x〜10.1.1 は選ばない。
- **未検証**：日本語パスのプロジェクトで、pid が正規の場所にでき、Stop で止まること（手順は下）。

### (b) この構成への破壊的変更

| 項目 | 10.3.0 の状態 | この構成への影響 |
| --- | --- | --- |
| Unity 2022.3.22f1 | `package.json` の `unity` は `2021.3` のまま。上流 CI は 2021.3.45f2（下限）・2022.3.62f1・6000.0.75f1・6000.4.8f1 で試験（`tools/unity-versions.json`） | 対象範囲内。2022.3.22f1 そのものは CI に無い。**未検証** |
| 依存の追加 | `dependencies` に組み込みモジュール7つ（`com.unity.modules.animation` `imageconversion` `physics` `physics2d` `uielements` `screencapture` `unitywebrequest`、いずれも 1.0.0）を追加 | 組み込みなので UPM が自動で有効にする。VRChat のプロジェクトでも解決できる見込み |
| Roslyn の参照 | `MCPForUnity.Editor.asmdef` の `precompiledReferences` に `Microsoft.CodeAnalysis.dll` など5つを追加（上流 #1359） | 該当 DLL が無いプロジェクトでは無視される（上流の説明）。UdonSharp を含むワールドのプロジェクトでは同名の DLL を参照に拾う。`USE_ROSLYN` を定義しない限りコードからは使わないので、影響は無い見込み。**未検証**（コンパイルエラー 0 を確認する） |
| HTTP の既定 | `HttpEndpointUtility.cs` は 9.7.1 から無変更（既定 `http://127.0.0.1:8080`、`/mcp`）。起動コマンドを組む `ServerCommandBuilder.cs` も文言以外は無変更 | Claude Code 側の HTTP 登録はそのまま使える |
| 起動の見た目 | 黒い窓が出ない。初回の Start だけ確認ダイアログが1回出る（`HttpServerLaunchConfirmed`）。出力は `Library/MCPForUnity/Logs/server-launch-8080.log` | 「窓を閉じて止める」運用はできなくなる。止めるのは Stop Server か Unity の終了 |
| サーバーの版 | PyPI `mcpforunityserver` 10.3.0（2026-10-04）は `fastmcp<4,>=3.0.2`、`mcp>=1.16.0` を要求。上流は FastMCP 4 に未対応 | FastMCP 4 を続けるなら、10.3.0 のサーバーソースへ FastMCP 4 対応を移植した wheel が別途必要 |
| Unity 10.3.0 ＋ 今の wheel（9.7.1+fastmcp4.2） | Unity とサーバーの WebSocket 部（`WebSocketTransportClient.cs`、`TransportCommandDispatcher.cs`、サーバーの `transport/models.py`・`plugin_registry.py`）と起動引数は無変更 | 基本の読み書きは通る見込み。10.x で増えた機能（`blender_bridge`、アセット生成、`run_tests` の拡張など）はサーバー側に無いので使えない。`run_tests` は両側で変更があり要確認。**未検証** |
| `GitUrlOverride` が空のとき | `AssetPathUtility.cs` は無変更。`mcpforunityserver==<パッケージの版>` を取りに行く | 10.3.0 なら PyPI に実在するので FastMCP 3 版で起動する（9.7.101 は PyPI に無い番号なので失敗する） |
| `set_active_instance` | 上流 10.0.2 以降、選択は MCP セッション（`MCP-Session-Id`）単位で保持（#1023）。`_meta.client_id` を要求するコードは上流に無い（`git grep` で0件） | エラーは手元の wheel の仕様。Unity 側の版では変わらない。当面の回避は、各ツール呼び出しに `unity_instance="<Name@hash>"` を付けること（9.7.1 のサーバーでも使える）。根本対応は wheel 側 |
| Unity 2台運用の既知の問題 | 上流 #1299（未解決）：対話中の Unity を1つ閉じると、共有の HTTP サーバーが全員分止まる | 9.7.1 と 10.3.0 で共通。版を上げても変わらない |

セキュリティ修正の有無：9.7.1→10.3.0 のセキュリティ関連の変更は、リモートホスト型サーバーの認証（`9c6b20d8`）と、10.x で新しく入ったアセット生成まわりだけ。ローカル運用の Unity 側で、更新を急ぐ理由になる修正は見当たらなかった。

### (c) VCC で入れる上での障害

- **依存**：`dependencies` は Unity のレジストリと組み込みモジュールだけ。git URL の依存は無い。`vpmDependencies` も無い。VCC は `dependencies` を解決しない（UPM が解決する）ので、VCC 側の障害は無い。
- **ファイルと GUID**（git タグ間の `MCPForUnity/` を比較）：9.7.1 は 611 ファイル、10.3.0 は 744 ファイル。削除 0、追加 133、変更 87、同一 524。同じパスで GUID が変わったもの・別パスへ移ったもの・重複は 0。変更のうち `.meta` 13 件は `MonoImporter` ブロックが付いただけで GUID は同じ。更新で参照が切れる心配は無い。
- **zip の日時（収録するなら必須の対策）**：上流の配布物（OpenUPM の tarball）は全ファイルが 1985-10-26。9.7.1 のミラー zip もその日時のまま。10.3.0 を同じ日時で zip にすると、9.7.x からの VCC 更新で、変更した 87 ファイルの更新日時が変わらない。
  - Editor アセンブリはファイルが増えるので作り直される見込み。
  - Runtime アセンブリは `.cs` 3 つの中身が変わるだけで、追加・削除が無い。そのため古い `MCPForUnity.Runtime.dll` が使われ続ける見込み（9.7.100 と同じ仕組み。推定）。
  - その場合、新しい Editor 側が Runtime に追加された `ScreenshotCapturer`（`ManageUI.cs:942` で使用）を見つけられず、パッケージ全体がコンパイルエラーになる。
  - 対策：9.7.1 と中身が違うエントリーと新規エントリーに、過去の固定日時を付ける（9.7.101 の方式と、`verify_listing.py` の「同じ名前で同じ日時なら同じ中身」チェックがそのまま使える）。
- **上流の zip の入手**：9.7.1 の zip は OpenUPM の tarball を zip にしたもの。git タグの `MCPForUnity/` と 610 ファイルがバイト一致し、`package.json` だけ OpenUPM が `repository`・`publishConfig` を追記している。この環境からは OpenUPM が 403、npm には無かったので、10.3.0 の tarball は取得していない。git タグ `v10.3.0` の `MCPForUnity/`（tree `3f64dcefddb6622f39805dbe2a5e00a104f162a1`、全ファイル 100644）からなら作れる。中身の一致は git の blob/tree ハッシュで検証できる。

## リスク

据え置き（推奨）のリスク：

- 上流の修正（`read_console`、ビルド、`execute_code`、テスト実行、HTTP 接続の安定化など）が入らない。
- 9.7.101 の既知の制限（起動バッチの `% & ^ | < >`、ハンドシェイクが PC 全体で1つ）が残る。
- MCP for Unity ウィンドウの更新バナー（v10.x）が出続ける。購入者が上流の手順で更新すると、このリスティングの外に出る。

10.3.x に移る場合のリスク：

- サーバーとの組み合わせ（上の (b)）、zip 日時（上の (c)）。
- 窓なし起動は Windows で 10.1.2 まで壊れていた新しい経路。実績が短い。
- 公開した時点で全購入者に更新として出る。公開済みの版は差し替えられない（同じ版番号は VCC が再インストールしない）。

## 10.3.x に移る条件と作り方

1. **サーバーの方針を先に決める。**
   - A：10.3.0 のサーバーソースに FastMCP 4 対応を移植した wheel（例：`10.3.0+fastmcp4.x`）を作り、`GitUrlOverride` で使う。
   - B：上流 PyPI の 10.3.0（FastMCP 3）に戻す。
2. **版番号**
   - A なら、9.7.101 と同じく「wheel が無いとわざと失敗する」ようにするため、PyPI に無い番号（例：`10.3.100`）にする。`package.json` の3項目だけを変えたパッチ版になる。版番号を読む箇所（`AssetPathUtility`、`PackageUpdateService`、`StdIoVersionMigration`）は 9.7.1 から無変更なので、9.7.101 と同じ挙動になる。
   - B なら `10.3.0` を無改変で収録し、`GitUrlOverride` を空に戻す。
3. **zip**：git タグ `v10.3.0` の `MCPForUnity/` から作る（OpenUPM の tarball を取れるならそれと照合する）。エントリー名は `\` 区切り、9.7.1 と中身が同じエントリーは上流と同じ日時、変更・追加したエントリーは公開日前日 00:00:00 の固定日時にする。上流との一致は git の tree ハッシュで検証する。`build_patched_zip.py` と `verify_listing.py` は今は 9.7.1 からのパッチ専用なので、ベースの版を持てるように拡張する。
4. **実機確認を通してから main に入れる**（下の手順）。VCC の更新経路まで確かめたいときは、main とは別の URL（テスト用ブランチの raw URL を指す別の `index.json`）でリスティングを作って試す。

## 実機確認の手順（Windows 11 日本語 ＋ Unity 2022.3.22f1）

本番のプロジェクトでは行わない。日本語名のプロジェクト（例：`テスト1`）の複製で行う。

0. **準備**
   - 複製したプロジェクトの外に、`Packages/com.coplaydev.unity-mcp`、`~/.claude.json`、`GitUrlOverride` の値を控える。
   - 隣に文字化けフォルダー（例：`繝・せ繝・1`）が残っていないことを確認する。
   - 10.3.0 の入れ方：VCC でその複製から MCP for Unity を外し、Unity の Package Manager で `https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.3.0` を追加する。VCC 経由の更新（zip 日時の確認）は、上の 4 のテスト用リスティングで行う。
1. **コンパイル**
   - Console のコンパイルエラーが 0 件であること。
   - `Library/ScriptAssemblies/MCPForUnity.Editor.dll` と `MCPForUnity.Runtime.dll` の両方の更新日時が、入れた時刻より後であること。
   - VCC 更新で入れた場合は、Runtime の DLL に `ScreenshotCapturer` という文字列が含まれること。
2. **Start Server**
   - 初回の確認ダイアログで Start を押す。黒い窓が出ないこと。
   - `Library/MCPForUnity/Logs/server-launch-8080.log` ができること。
   - `Library/MCPForUnity/TerminalScripts/mcp-terminal.cmd` が新しく作られないこと（古いものが残っていれば、更新日時が変わらないこと）。
3. **pid の場所**
   - 日本語名の本来のフォルダーに `Library/MCPForUnity/RunState/mcp_http_8080.pid` ができること。
   - 隣に文字化けフォルダーができていないこと。
4. **サーバーの応答**
   - `http://127.0.0.1:8080/health` が応答すること。`version` を記録する（A なら wheel の版、B なら `10.3.0`）。
   - Console の起動ログに、意図したサーバー（`--from` の参照先）と `--transport http` が出ていること。
5. **Stop Server**
   - pid ファイルが消え、`/health` が応答しなくなること。
   - タスクマネージャーで `uvx`・`python` が残っていないこと。
6. **もう一度 Start**：同じように起動し、pid が本来の場所にできること。
7. **Claude Code 側**
   - `claude mcp list` で HTTP（`127.0.0.1:8080`）のままであること。
   - `~/.claude.json` を控えと比べ、意図しない書き換えが無いこと。上流 #1280 以降、Claude Code の登録の検出方法が変わっている。
8. **Unity 2台**（任意）
   - 別のプロジェクトも開く。`mcpforunity://instances` に2つ出ること。
   - 各ツールに `unity_instance` を付けて、両方から読み取れること。
   - `set_active_instance` の結果を記録する（A の wheel では既知のエラー。B ではセッション単位で保持される）。
9. **Unity の終了**：Unity を終了すると、サーバーも止まること。

戻すときは、VCC で 9.7.101 を選ぶ（日時が違うので再コンパイルされる）。そのあと、0 で控えた設定と比べる。

## 未検証の項目

- 10.3.0 の窓なし起動で、日本語パスの pid が正規の場所にでき、Stop で止まること。
- Unity 2022.3.22f1 でのコンパイル（Roslyn の参照を含む）。アバターとワールド（UdonSharp あり）の両方。
- Unity 10.3.0 ＋ 9.7.1+fastmcp4.2 wheel の組み合わせでの各ツール（特に `run_tests`）。
- VCC 更新時の再コンパイル（zip 日時の対策の効果）。
- 10.3.0 の起動・停止での `~/.claude.json` の扱い。
