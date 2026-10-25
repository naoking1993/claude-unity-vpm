# トラブル対応

`cct_*` が呼べない、止まる、結果がおかしいときの切り分け。MCP サーバー（Python・FastMCP）そのものの更新や改修は `unity-mcp-maintenance`、接続の切り分け表と既知エラーは `unity-mcp-operations` の verified-facts が正。ここには cct に関わる部分だけを書く。

根拠は MCP for Unity 9.7.1 の Unity 側と Python サーバー（PyPI の wheel）のソースを読んだ調査（2026-10-24）と、パッケージのコード。ユーザーのサーバーはローカル改修版 `9.7.1+fastmcp4.2`（Python 3.14.5）で、改修版での挙動は**未実測**（[台帳](verified-facts.md)）。

## 目次

- 1. cct が tool 一覧に無い
- 2. 「disabled in the Unity Editor」
- 3. タイムアウト
- 4. 説明や引数が古いまま
- 5. コンパイルエラー
- 6. 日本語パス
- 7. 出力の場所と片付け
- 8. ツールが知り得ないこと
- 9. その他の症状

---

## 1. cct が tool 一覧に無い

1. **`execute_custom_tool` で呼んでみる**：`{"tool_name": "cct_environment", "parameters": {}}`。成功すれば Unity 側の登録はできていて、単独 tool として出ていないだけ。以後もこの形で呼ぶ（引数の説明は [ツールの仕様](tool-reference.md)。この経路には引数のスキーマが無い。ツールの Description はリソース `mcpforunity://custom-tools` でも読める）。
   - 単独 tool として出ない理由で最も疑わしいのは Python 3.14：9.7.1 のサーバーは、引数を持つカスタムツールを 3.14 で登録すると `KeyError: '<最初の引数名>'` で失敗する（手元で再現、上流の main では修正済み）。8つとも引数を持つ。ユーザーの改修版がこの修正を含むかは未確認。
   - `execute_custom_tool` は、サーバーを `--project-scoped-tools` 付きで起動したときだけある。ユーザーの起動コマンドには付いている記録がある（2026-09-28〜29）。
2. **`execute_custom_tool` も「Tool 'cct_…' not found」**なら、Unity がツールを登録していない。順に確かめる：
   1. Claude Creator Tools がプロジェクトに入っているか（`Packages/io.github.naoking1993.claude-creator-tools`）。
   2. MCP for Unity（`com.coplaydev.unity-mcp`）が 9.7.1 以上か。MCP 用のアセンブリ（`Naoking.ClaudeCreatorTools.Mcp`）は、その版があるときだけコンパイルされる（`versionDefines` で `CCT_UNITY_MCP`）。無ければメニューだけが使える。
   3. コンパイルエラーで止まっていないか（5章）。
   4. Window > MCP for Unity の Tools で、Custom Tools の欄に `cct_*` があり On か（2章）。
   5. Unity が接続中か。複数の Unity を開いているなら、接続先のインスタンスがこのプロジェクトか（`unity-mcp-operations` の切り分け表）。
3. **Unity が登録したツールの一覧**は MCP のリソース `mcpforunity://custom-tools` で読める。PC 上ならサーバーの `GET http://127.0.0.1:8080/api/custom-tools` でも見える（ソース上。ユーザーの改修版では未確認）。
4. **サーバーのログ** `%LOCALAPPDATA%\UnityMCP\Logs\unity_mcp_server.log`（時刻は JST）で次の行を探す：
   - `Failed to register custom tool 'cct_…' globally: '<引数名>'`：上の Python 3.14 の問題。そのツールだけ単独登録されず、`execute_custom_tool` では呼べる。
   - `Unexpected error during global custom tool registration`：登録の処理全体の失敗。
   - `already registered with a different schema; keeping existing definition.`：古い定義が残っている（4章）。
   - 単独登録の修正はサーバー側の変更で、このスキルの範囲外（`unity-mcp-maintenance`）。サーバーのファイルを直接編集しない。
5. **追加した直後に見えない**：新しいツールは、コンパイル → 再接続 → 登録 で自動的にサーバーへ届き、サーバーは一覧の変更を通知する。Claude Code がその通知に追従するかは環境によって違う（過去に不具合の報告あり）。見えなければ Claude Code で `/mcp` から再接続してもらう。

MCP が使えない間の代わり：ユーザーに Unity の Tools > Claude Creator Tools のメニュー（[ツールの仕様](tool-reference.md) 0.7）を押してもらう。Console に1行の要約とレポートのパスが出るので、Claude がそのレポートを Read・Grep で読む（Claude Code が同じ PC で動いているときだけ）。

## 2. 「disabled in the Unity Editor」

`Tool 'cct_…' is disabled in the Unity Editor.` は、Window > MCP for Unity > Tools のトグルが Off になっている。

- このトグルは EditorPrefs（`MCPForUnity.ToolEnabled.<ツール名>`）に保存され、**PC のすべてのプロジェクトで共通**。別のプロジェクトで Off にすると、ここでも Off になる。
- カスタムツールは既定で On。一度キーができると、それが使われ続ける。
- ユーザーにトグルを On にしてもらう。HTTP で接続中なら Unity が登録を送り直す。
- Off にしても、すでに単独登録されたツールはサーバーの一覧に残り、呼ぶとこのエラーになる。一覧から消すにはサーバーの再起動が要る。

## 3. タイムアウト

- 1回の呼び出しは既定 30 秒で打ち切られる。`timeout_seconds` で延ばせる（サーバーの上限は 3600 だが、Claude Code 側の都合で 240 以下にする。目安はこの章の最後）。サーバーは送るコマンドの引数から `timeout_seconds` を読むので、`execute_custom_tool` でも `parameters` の中に入れればよい（コード読解）。
- 打ち切られたとき、サーバーは `success: false` と `hint: "retry"` を返すことがある（コード読解。エラーの文面は環境で違いうる）。**cct ではこの hint に従って再送しない**。Unity 側では、まだ始まっていない呼び出しは取り消されるが、始まっていた呼び出しは最後まで動き、その間 Unity のメインスレッドを止める。終わったかどうかは結果では分からない。
- 打ち切られた後の確かめ方：
  - 読み取り系（audit・inspect・report）：少し待って、範囲を絞るか `timeout_seconds` を上げて呼び直す。
  - `cct_capture_views`：`Library/ClaudeCreatorTools/Captures/` の最新のフォルダーを見る（撮り終えていることがある）。
  - `cct_snapshot`：`list`（create が終わっていれば新しい id がある）、restore の後なら `diff`。
  - `cct_world_gimmicks` の import：`list` の `imported`。
- 自分で走査を打ち切るツール（結果は途中までになる）：
  - `cct_world_audit`：参照の走査は `timeout_seconds - 10` 秒、U# の走査は `(timeout_seconds - 10) / 4` 秒で止まる（`REFERENCE_SCAN_TRUNCATED`・`USHARP_SCAN_TRUNCATED`）。
  - `cct_dependency_report`：`timeout_seconds - 5` 秒（既定 25 秒）で止まる（`SCAN_INCOMPLETE`、`conclusive: false`）。
  - `cct_animator_inspect`：`timeout_seconds × 0.6`（最低 8 秒）で止まる（`TRUNCATED`）。
  - どれも「打ち切り＝問題なし」ではない。`timeout_seconds` を上げて呼び直す。
- 目安：大きいアバターの `post_build`、大きいワールドの監査、高解像度・多方向・透過・比較の撮影は 120。プロジェクト全体の `reverse` と数百 MB のスナップショットは 240。**240 より大きくしない**：Claude Code は HTTP の MCP 呼び出しから5分間何も返らないと中断する（ツールの Description とパッケージの README の記載。Claude Code のドキュメント由来で、ユーザーの環境では未実測）。240 でも足りない処理は、範囲を絞って複数回に分ける。

## 4. 説明や引数が古いまま

- 既存のツールの Description・引数を変えた版にパッケージを更新しても、**MCP サーバーを再起動するまで古い定義のまま**。サーバーは同じ名前のツールが来ると古い定義を残す（ログに `already registered with a different schema`）。
- 症状：このスキルに書いた引数を単独 tool のスキーマが受け付けない、Description に新しい説明が無い。
- 直し方：ユーザーに MCP for Unity ウィンドウで Stop Server → Start Server を押してもらい（9.7.101 なら Stop が効く記録あり）、Claude Code は `/mcp` で再接続する。
- C# の中身だけの変更（Description と引数が同じ）は、コンパイル後すぐ新しいコードが動く。再起動は要らない。
- 版の確かめ方：`cct_environment` の `tool_version`（パッケージの版）と `packages` の `io.github.naoking1993.claude-creator-tools`。どちらも表示上の版で、サーバーが持っている定義の版ではない。

## 5. コンパイルエラー

- プロジェクトのどこかにコンパイルエラーがあると、Unity はスクリプトを読み込み直さず、cct の新しい版も動かない。`read_console` がエラー0件でもコンパイルが止まっていた実例がある。真実は Editor.log の `error CS`（`unity-mcp-operations`）。ワールドなら `cct_world_audit` の `EDITOR_LOG_CS_ERRORS`・`EDITOR_LOG_USHARP_ERRORS`・`SCRIPT_COMPILATION_FAILED` でも見られる（cct が動いている場合）。
- コンパイル中はどのツールも `EDITOR_BUSY` で止まる。終わってから呼ぶ。
- パッケージは2つのアセンブリ：`Naoking.ClaudeCreatorTools.Editor`（本体とメニュー。MCP・Newtonsoft・VRChat SDK に依存しない）と `Naoking.ClaudeCreatorTools.Mcp`（MCP 用。MCP for Unity 9.7.1 以上のときだけ）。VRChat SDK の型は名前で引くので、SDK が無くてもコンパイルは通る設計。
- アバタープロジェクトにワールドギミックのサンプルを入れると、UdonSharp が無いので全スクリプトがコンパイルできなくなる。`cct_world_gimmicks` は UdonSharp が無ければ断る（`NO_UDONSHARP`）が、Package Manager の Samples の「Import」ボタンは確かめない。入ってしまったら、取り込まれたフォルダー（`Assets/Samples/` の下など）を `cct_snapshot` で退避してから、ユーザーの同意を得て消す。
- `cct_snapshot` でスクリプトを戻すと、呼び出しの後でコンパイルとドメインリロードが起き、MCP が一時的に切れる（`SCRIPTS_RESTORED`）。つながり直してから次を呼ぶ。

## 6. 日本語パス

- ツールはファイルを UTF-8（BOM なし）で書き、返すパスは区切り `/` の絶対パスで、日本語をそのまま残す。
- フォルダー名：撮影は `<日時>-<4桁>_<label>`。label の日本語は残り、ファイル名に使えない文字・空白・`%` は `_` になり、40字で切る。スナップショットの id は日時と label の ASCII の部分だけ。スナップショットの中のファイル名は Windows の MAX_PATH を避けるため短い。
- 深い日本語のプロジェクトパスで、撮影の比較画像（`compare_<view>.png`）まで含めたパスが長くなりうる。Windows の長いパスでの書き込みは未実測。`WRITE_FAILED` が出たら label を短くする。
- Claude Code の Read・Grep で、日本語を含む `/` 区切りの Windows パスを開けるかは未実測。開けなければ `\` 区切りに直して試す。
- MCP for Unity 自体の日本語パスの不具合（起動バッチの文字化け、pid ファイル）は cct と別の問題で、9.7.101 で直っている（`unity-mcp-operations`）。

## 7. 出力の場所と片付け

| 出力 | 場所 | 消してよいか |
| --- | --- | --- |
| レポート JSON・依存一覧 TSV | `<プロジェクト>/Library/ClaudeCreatorTools/Reports/` | 使い捨て。Library を消すと消える |
| 撮影 | `<プロジェクト>/Library/ClaudeCreatorTools/Captures/<id>_<label>/` | 使い捨て。ただし比較の基準にしているフォルダーは、作業が終わるまで残す |
| スナップショット | `<プロジェクト>/ClaudeCreatorTools/Snapshots/<id>/` | Library の外で、Library を消しても残る。消すのは `cct_snapshot delete`（ドライラン → 同意 → confirm） |
| 一時コピー | `Assets/__ClaudeCreatorToolsTemp` | `cct_snapshot create` が作って消す。残っていたら `TEMP_CLEANUP_FAILED` を見る |
| ワールドギミック | `Assets/ClaudeWorldGimmicks`（既定） | ユーザーの資産。消すなら破壊的操作の規律で |

- 場所は `cct_environment` の `output_dirs` で確かめる。メニューの「レポートフォルダを開く」などでユーザーにも開いてもらえる。
- `ClaudeCreatorTools/` はプロジェクトの直下にできる。スナップショットには購入アセットのコピーが入ることがあるので、git で管理しているプロジェクトでは `.gitignore` に `/ClaudeCreatorTools/` を足すようパッケージの README が勧めている（足すかはユーザーが決める）。`Assets/`・`Packages/` の中へ移さない（GUID が重複する）。
- Claude が勝手に消さない。片付けはユーザーに一覧を見せて同意を得てから。

## 8. ツールが知り得ないこと

結果を報告するときに「確かめていないこと」として書く。

- **ビルドの前と後**：MA・NDMF・AAO・VRCFury はビルド時に構成を変える。cct の値は基本的にビルド前（`stage: "pre_build"`）。`post_build: true` は NDMF だけで一時クローンを処理した値で、VRCFury、SDK のビルド処理（`IPreprocessCallbackBehaviour` など）、実際の SDK ビルドは通っていない。NDMF は Missing Script を黙って消すので、処理後の0は依存がそろった証明ではない。
- **SDK のビルドとアップロード**：どのツールもしない。ダウンロードサイズ・非圧縮サイズ、Udon の署名、Network ID の割り当て、アカウントの条件（Trust Rank、iOS の権限）は分からない。`upload.blocked`・`verdict` は「このツールで判定できる止まる条件」だけ。
- **エディタと VRChat クライアント**：撮影はエディタの Built-in パイプラインでの描画で、ポストプロセスが無く、isolated は中立の照明。VRChat クライアントの描画・シェーダーの挙動・性能は分からない（クライアントの Unity の版と制作用 Editor の版の関係は `vrchat-unity-version-check`）。
- **実行時の値**：Animator のパラメーターの今の値、保存された値（LocalAvatarData）、Udon の実行時の状態、同期、他のプレイヤーからの見え方、ClientSim・Gesture Manager・エミュレーターでの挙動は、静的解析では分からない。
- **見つけられない参照**：コードからの名前での使用（`Shader.Find`・`Resources.Load`、導入補助が固定 GUID で探すもの）、バイナリで保存されたアセット（`BINARY_SERIALIZATION`）。
- **プラットフォームの見積もり**：テクスチャメモリはアクティブなビルドターゲットのインポート結果。別のプラットフォームの値は推定（`estimate_for`）。
- **版の前提**：ランクの表は SDK 3.10.5 の値。SDK の計算が使えるときは SDK が正（`RANK_TABLE_MISMATCH`）。MA のビルド時の予測は MA 1.19.0-alpha.2・NDMF 1.15.0-beta.2 のソースで確かめたもので、ユーザーの記録にある MA 1.18.7・NDMF 1.14.8 でも同じと仮定している（メッセージに「推定」と書く）。
- **ワールドの許可リスト**：`NOT_WHITELISTED` は、SDK がビルド時に検査せずクライアントが読み込み時に除去する、という推定に基づく。

## 9. その他の症状

| 症状 | 原因と対処 |
| --- | --- |
| `AMBIGUOUS_TARGET` | 候補は `data.candidates`（`cct_avatar_audit`・`cct_animator_inspect`）か `data.details`（`cct_capture_views` は `details.candidates`、`cct_dependency_report` は `details`）。ユーザーに選んでもらい、階層パスか instance ID で呼び直す。アバターなら `cct_environment` の `avatar_roots` でも選べる |
| `INVALID_ARGUMENT`（platform） | `platform` は all・pc・android・ios だけ。Quest は `android` |
| `TARGET_NOT_FOUND`（instance ID） | ドメインリロードで ID が変わった。階層パスで指定し直す |
| 撮影に対象が写っていない・小さい | `contact_sheet` を見て確かめる。`NO_HUMANOID_HEAD`・`FRAMING_OUTLIERS_EXCLUDED`・`BAKE_FAILED` を見る。Play 中なら編集モードで撮る |
| 前後の差が全体に出る | `COMPARE_CAMERA_DIFFERS`・`COMPARE_MISMATCH` を見る。`_Time` で動くシェーダーの差、`TEXTURE_STREAMING` の粗いミップも差になる |
| 監査の値が Play 中と違う | Play 中は NDMF の Apply on Play で処理済みのことがある（`PLAY_MODE`）。Play を止めて比べる |
| ブリッジが `no_unity_session` になった | cct はダイアログを出さない。SDK などのモーダルダイアログが開いていないかをユーザーに確かめてもらう（`unity-mcp-operations`） |
| `SCENE_DIRTIED`・`ASSET_DIRTIED` が出た | 読み取り専用のはずのツールでシーン・アセットが変更扱いになった。保存せずにユーザーに伝え、内容を確かめてもらう。再現条件を [台帳](verified-facts.md) に記録する |
| 書き込みの結果が `success: false, message: null` | cct の失敗はふつうでも外側の `message` が null で、理由は `error` と `data.message`、コードは `data.code` にある（[ツールの仕様](tool-reference.md) 0.3）。`data.code` が無い失敗（`hint: "retry"` や接続エラーの文面だけ）は cct が結果を返す前に切れたもので、ドメインリロードと重なると書き込みが成功していることもある（MCP for Unity の既知の挙動、`unity-mcp-operations`）。再送せず `list`・`diff` で確かめる |
