# claude-unity-vpm

Claude などの AI アシスタントと Unity Editor を MCP で接続する **MCP for Unity**（© CoplayDev, MIT License）を、VRChat Creator Companion (VCC) から入れるための**非公式** VPM リスティングです。

- リスティング URL: `https://naoking1993.github.io/claude-unity-vpm/index.json`
- 上流: https://github.com/CoplayDev/unity-mcp

## 収録バージョン

| バージョン | 中身 | 用途 |
| --- | --- | --- |
| **9.7.101** | 上流 9.7.1 に**1行だけ**修正を加えたパッチ版（表示名 `MCP for Unity (JP path fix)`） | 通常はこちら |
| 9.7.100 | コードは 9.7.101 と同じ。ただし 9.7.1 から更新すると、Unity が再コンパイルせず修正が効かないことがある（下の「9.7.100 に更新済みのプロジェクト」） | 使わない。9.7.101 へ更新してください |
| 9.7.1 | 上流 9.7.1 の無改変ミラー（zipSHA256 `a3eee339…1831`） | ロールバック用。削除・差し替えはしません |

## 9.7.101 の変更点

`Editor/Services/Server/TerminalLauncher.cs` の Windows 分岐で、Unity が生成するサーバー起動スクリプト
`Library/MCPForUnity/TerminalScripts/mcp-terminal.cmd` の 2 行目に `chcp 65001>nul` を追加しただけです。
差分は [`patches/9.7.101/0001-windows-cmd-chcp-65001.patch`](patches/9.7.101/0001-windows-cmd-chcp-65001.patch) にあります（9.7.100 と同じ内容）。
それ以外に変えたのは `package.json` の `version` / `displayName` / `description` の 3 項目だけで、他のファイル（`.meta` の GUID を含む）は上流と同じバイト列です。
差し替えた 2 ファイル（`TerminalLauncher.cs` と `package.json`）だけは、zip 上の日時を 2026-09-28 にしてあります。他のファイルは上流 zip と同じ 1985-10-26 のままです。

### 直る問題

- 上流 9.7.1 は `mcp-terminal.cmd` を BOM なし UTF-8 で書きます。日本語 Windows の cmd.exe はこれを CP932 として読みます。そのため、プロジェクトのパスに日本語（例: `テスト1`、`霧島`）が入っていると、`--pidfile` のパスが文字化けします。
- その結果、pid ファイルが本来の `Library/MCPForUnity/RunState/` ではなく**文字化けした名前のフォルダー**に作られ、Unity の **Stop Server** がサーバーを止められませんでした。
- 2 行目で `chcp 65001` を実行すると、3 行目以降（起動コマンド）が UTF-8 として読まれ、pid ファイルが正しい場所に作られます。この修正は Windows 11（日本語）+ Unity 2022.3.22f1 の実機で、Start → pid 生成 → Stop（pid 削除・サーバー停止）→ Start まで確認済みです。
- `mcp-terminal.cmd` は BOM なしのままにする必要があります。BOM を付けると 1 行目の `@echo off` が壊れます。

### 9.7.100 に更新済みのプロジェクト

9.7.100 では、差し替えた `TerminalLauncher.cs` の日時が上流 9.7.1 と同じ 1985-10-26 のままでした。これが原因で修正が効かないことがあります。

- **起きること**
  - 公式 VCC は、zip の日時をそのまま展開したファイルに付けます。
  - Unity のスクリプトのコンパイルは、`.cs` が変わったかどうかをファイルの更新日時で判断します。
  - そのため、9.7.1 から 9.7.100 に更新しても Unity は「変更なし」と判断し、修正前のコンパイル結果（`Library/ScriptAssemblies/MCPForUnity.Editor.dll`）を使い続けることがあります。
  - MCP for Unity ウィンドウの表示は `package.json` を読んでいるだけなので、`v9.7.100` と表示されていても修正が効いているとは限りません。
  - 霧島で実際に起きました（2026-09-29）。ALCOM（vrc-get）は展開したファイルの日時を設定しないため、この問題は起きません。
- **直し方（推奨）**：VCC で **9.7.101** に更新します。9.7.101 は差し替えた 2 ファイルの日時が新しいので、Unity が変更に気付いて再コンパイルします。
- **直し方（手作業）**：Unity でプロジェクトを開いたまま、PowerShell で `TerminalLauncher.cs` の更新日時を今の時刻にしてから、Unity で Ctrl+R（Assets > Refresh）を押します。

  ```powershell
  (Get-Item "<プロジェクト>\Packages\com.coplaydev.unity-mcp\Editor\Services\Server\TerminalLauncher.cs").LastWriteTime = Get-Date
  ```

- **直らない見込みの操作**：Assets > Refresh だけ、Unity の再起動、VCC で同じ 9.7.100 を入れ直すこと。どれもファイルの日時が変わりません。右クリック → Reimport が効くかは未確認です。
- 直ったかどうかは、「更新後の確認」の 2 番（DLL に `chcp 65001` が含まれるか）で確かめてください。

## 前提条件（FastMCP 4 移行との関係）

このリスティングは **Unity 側パッケージ**だけを配ります。Python サーバーは上流 PyPI 版ではなく、FastMCP 4 対応のローカル改修版 wheel（`mcpforunityserver 9.7.1+fastmcp4.2`）を使う前提です。

- Unity の EditorPrefs `MCPForUnity.GitUrlOverride` に、その wheel の `file:///…whl` 参照が入っていること。9.7.101 は起動時にまずこの値を使います。
- HTTP トランスポート（`http://127.0.0.1:8080/mcp`）を使っていること。
- `GitUrlOverride` が空だと、9.7.101 は `mcpforunityserver==9.7.101` を探します。PyPI にその版は無いので、**わざと起動に失敗します**。上流 9.7.1 のように FastMCP 3 系の PyPI 版へ黙って戻ることはありません。
- `GitUrlOverride` はパーセントエンコード（`%E3%83%86…` のような形）ではなく、そのままのパス表記で保存してください。バッチファイルでは `%` が展開されるため、`chcp` があっても壊れます。
- Claude Code 側の接続設定は HTTP（`http://127.0.0.1:8080/mcp`）にしてください。以前の STDIO 設定や `mcpforunityserver==9.7.1` の指定には戻さないでください。

## 導入・更新手順

1. **事前バックアップ**
   - 各プロジェクトの `Packages/com.coplaydev.unity-mcp` フォルダーを、**プロジェクトの外**（例: デスクトップ）にコピーしておきます。
     - `Packages/` や `Assets/` の中にコピーを残さないでください。同じ名前のパッケージが 2 つになり、VCC がそのプロジェクトのパッケージを読み込めなくなります。
     - 手で `TerminalLauncher.cs` を直している場合、更新でその修正は上書きされます。9.7.101 に同じ修正が入っているので問題はありません。
   - `~/.claude.json` などの MCP クライアント設定ファイルをコピーしておきます。
   - `MCPForUnity.GitUrlOverride` と HTTP 設定の現在の値を控えておきます。
2. VCC の **Settings → Packages** で、このリスティングにチェックが入っている（有効な）ことを確認します。
3. 各プロジェクトの **Manage Project** 画面で、「Manage Packages」見出しの右にある **Refresh（↻）** を押してリスティングを再取得します（キャッシュは最大 1 時間残ります）。そのうえで MCP for Unity を **9.7.101** に更新します。プレリリース表示を有効にする必要はありません。
4. **同じ日のうちに全プロジェクトを更新してください。** 上流の `StdIoVersionMigration` は「最後に処理したバージョン」を PC 全体で 1 つしか記録しません。そのため、版の違うプロジェクト（9.7.1 と 9.7.101 など）を行き来するたびに再実行されます。
   - この処理は STDIO 形式で残っている設定を HTTP 形式に書き換える方向にしか動きません。`GitUrlOverride` が入っていれば、`mcpforunityserver==` の指定が書き戻されることはありません。
   - なお、上流は Editor 起動ごとにクライアント設定の自動再チェック（`StartupConfigRewrite`）も行います。これはバージョンに関係なく以前から動いている処理です。

### 一度だけ必要な後片付け

- **修正前に起動したサーバー**は、pid ファイルが文字化けフォルダー側にあるため、Stop Server では止まりません。Unity を終了するか、ポート 8080 で待ち受けているプロセスを終了してください。
- そのあと**文字化けした名前のフォルダー**を削除します。
  - 文字化けフォルダーは、日本語名のプロジェクトフォルダーの**隣**（同じ階層）にできます（例: `霧島` → `髴ｧ蟲ｶ`、`テスト1` → `繝・せ繝・1` など）。中身は通常 `Library/MCPForUnity/RunState/mcp_http_8080.pid` です。
  - フォルダー名の最後の文字によっては CP932 の 2 バイト目が `\` を飲み込み、`繝・せ繝・Library` のように `Library` がくっついた名前になります。その場合の中身は `MCPForUnity/RunState/mcp_http_8080.pid` です。
  - どちらの場合も、たどった先にあるのが `mcp_http_8080.pid` 1 つだけであることを確認してから削除してください。

## 更新後の確認

1. Unity のコンパイルエラーが 0 件であること。`Packages/com.coplaydev.unity-mcp/package.json` の version が `9.7.101` であること。MCP for Unity ウィンドウの表示が `v9.7.101` であること。
2. **修正がコンパイルされていること**：`Library/ScriptAssemblies/MCPForUnity.Editor.dll` の更新日時が、更新した時刻より後であること。さらに、DLL に文字列 `chcp 65001` が含まれていること（下の PowerShell で `True`）。ウィンドウの版表示だけでは判断できません。

   ```powershell
   $dll = "<プロジェクト>\Library\ScriptAssemblies\MCPForUnity.Editor.dll"
   (Get-Item $dll).LastWriteTime
   [BitConverter]::ToString([IO.File]::ReadAllBytes($dll)).Replace('-','').Contains([BitConverter]::ToString([Text.Encoding]::Unicode.GetBytes('chcp 65001')).Replace('-',''))
   ```
3. `GitUrlOverride` が以前と同じ wheel を指し、HTTP 設定が有効なままであること。
4. **Start Server** のあと、次の 4 点を確認します。
   - `Library/MCPForUnity/TerminalScripts/mcp-terminal.cmd` の 2 行目が `chcp 65001>nul` で、先頭に BOM が無いこと（先頭バイトが `40 65 63 68` = `@ech`）。
   - `mcp-terminal.cmd` の 4 行目（起動コマンド）、または Unity Console の `Started local HTTP server in terminal:` の行に、`--from "…fastmcp4.2…whl"` と `--transport http` が含まれていること。黒い窓は `@echo off` のため、起動コマンドを表示しません。
   - `http://127.0.0.1:8080/health` が応答し、`version` が `9.7.1+fastmcp4.2` であること。
   - **日本語名の本来のプロジェクトフォルダー**に `Library/MCPForUnity/RunState/mcp_http_8080.pid` があること。
5. **Stop Server** で pid ファイルが消え、`/health` が落ちること。もう一度 Start Server で復帰できること。
6. `claude mcp list` で Unity の MCP が HTTP（`127.0.0.1:8080`）のままであること。
7. 事前バックアップと設定ファイルを比べます。どこにも `mcpforunityserver==` が入っていないこと。STDIO→HTTP の書き換えは 1 回だけ起こり得ます。
8. 新しいプロジェクトに追加すると 9.7.101 が入ること。

## ロールバック

VCC のバージョン選択で **9.7.1** を選びます。9.7.1 の zip とエントリーは今後も変更・削除しません。
ただし 9.7.1 に戻すと、日本語パスでの pid ファイル問題も戻ります。9.7.101 から 9.7.1 に戻す場合は日時が違うので再コンパイルされますが、9.7.100 から 9.7.1 に戻す場合は同じ理由で再コンパイルされないことがあります（その場合は上の手作業の直し方と同じ操作をします）。また、更新時に STDIO→HTTP へ書き換えられた設定は元には戻りません。

## 既知の制限（9.7.101 では直していないもの）

- プロジェクトパスに `% & ^ | < >` が入ると、バッチファイルの仕様で起動コマンドが壊れることがあります。`GitUrlOverride` をパーセントエンコードにした場合も同じです。どちらも今の環境では起きていないため、上流のままにしています。
- サーバーのハンドシェイク情報は PC 全体で 1 つです。複数のプロジェクトで同時に Start/Stop すると、別プロジェクトのサーバーを止めることがあります。上流の仕様です。
- `netstat` などの出力は文字コード指定なしで読まれますが、照合するのは英数字だけなので、日本語パスの影響はありません（調査済み）。

## 上流の新しい版への追従方針

- 上流 9.7.3 以降はサーバー起動の仕組みが変わっているため、このパッチは 9.7.1 系専用です。
- 上流の新しい版に移るときは、先に FastMCP 4 改修版 wheel との互換性を確認してください。
- このリスティングでパッチを更新する場合は **9.7.102, 9.7.103 …** と番号を上げます。同じバージョン番号で zip を差し替えることはしません（VCC は同じ番号を再インストールしないため）。
- バージョンは常に `X.Y.Z` 形式にします。`-jp` などのプレリリース表記は使いません。理由は次の 3 つです。
  - VCC ではプレリリースが既定で表示されません。
  - `9.7.1-jp.1` は 9.7.1 より古い版として扱われます。
  - 上流のコードの挙動が変わります。`-` を含むと更新チェックがベータ系列になり、`-beta` / `-alpha` / `-rc` / `-pre` を含むと、`GitUrlOverride` が空のときにベータ版のサーバーを取りに行きます。

## 開発者向け

| パス | 内容 |
| --- | --- |
| `tools/build_patched_zip.py` | 上流 9.7.1 zip（SHA256 照合つき）に `patches/<版>/` を当て、`RELEASES` にある各版の zip を作ります。エントリーの並び・名前（`\` 区切り）は上流 zip のままです。日時は、変更していないエントリーは上流のまま、差し替えた 2 エントリーは版ごとの固定の日時（`stamp`）にします（9.7.100 だけは上流のまま） |
| `tools/verify_listing.py` | `index.json` と各 zip の整合性チェック、差分がパッチ由来の 2 ファイルだけであることのチェック、日時のチェック（差し替えたエントリーは上流より新しい・未来の日時でない・どの 2 版を比べても「同じ名前で同じ日時なら同じ中身」）、mono で `TerminalLauncher.cs` をコンパイルして日本語パスで生成される `.cmd` のバイト列の検証を行います |
| `tests/cs/` | 上記検証用の最小スタブとハーネス |
| `.github/workflows/verify.yml` | push / PR ごとに `verify_listing.py` を実行します |

これらのスクリプトは **Linux・WSL・CI 専用**です。Windows 版 Python の `zipfile` はエントリー名の `\` を `/` に変換するため、Windows で実行すると誤った失敗を報告します。

```sh
python3 tools/build_patched_zip.py   # 既存の zip と同じものが再現できるかを確認（公開済みの zip は上書きしません）
python3 tools/verify_listing.py      # "OK" で終われば合格（mono-mcs / mono-runtime / patch が必要）
```

新しいパッチ版を出すときは、次の手順で番号を上げます（公開済みバージョンの zip は変更しません）。

1. `tools/build_patched_zip.py` の `RELEASES` に新しい版を足し、`patches/<新バージョン>/` にパッチを置きます。`stamp` は公開日の**前日の 00:00:00**（秒は偶数）にします。実行時刻にしないのは同じ zip を再現できるようにするため、前日にするのはどのタイムゾーンでも未来の日時にならないようにするためです。
2. ビルドで表示された SHA256 を、`index.json` の新しいエントリーの `zipSHA256` と、`tools/verify_listing.py` の `PUBLISHED` に記録します。

## ライセンス

MCP for Unity は CoplayDev による MIT License のソフトウェアです。9.7.101 と 9.7.100 は上流 9.7.1（revision `78ee5418`）を改変したものです。改変内容は上記の 1 行と `package.json` の 3 項目だけです。このリスティングは非公式で、CoplayDev とは関係ありません。
