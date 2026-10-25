# Claude Creator Tools

`io.github.naoking1993.claude-creator-tools` 0.1.0 — VRChat のアバター改変・ワールド制作を、Claude が MCP for Unity 経由で手伝うための Unity エディター拡張です。

> **状態: 初版（0.1.0）。Unity の中で一度も動かしていません。** 確かめたのは、オフラインでのコンパイルと静的なチェックだけです（「既知の制限」を参照）。最初に「初回確認」の手順を実機で行ってください。

この README は、作者本人と Claude の両方が読む前提で書いています。Claude 向けの決まりごと（呼び出し方・戻り値の読み方）は「Claude からの呼び出し方」と「共通の引数と戻り値」にまとめてあります。

---

## これは何か

Claude はツールの戻り値と、自分で読めるファイルしか見られません。このパッケージは、VRChat の作業で毎回 `execute_code` で書き直していた検査や、間違えやすい判定を、決まった形の 1 回の呼び出しにします。

- **MCP ツール 8 個（`cct_*`）**: MCP for Unity 9.7.1 以上が入っていると自動で登録されます。
- **Tools メニュー**: `Tools > Claude Creator Tools`。MCP ツールと同じ処理を、Claude なしで実行できます。MCP for Unity が無いプロジェクトでもメニューは使えます。
- **ワールドギミックのサンプル**: UdonSharp のスクリプト 8 種（`Samples~/WorldGimmicks`）。ワールドプロジェクト専用です。

どのツールも、結果の要約を Claude に返し、全文の JSON レポートを `Library/ClaudeCreatorTools/Reports` に保存します。ビルドとアップロードはしません。

## 動作要件

| 項目 | 要件 |
| --- | --- |
| Unity | 2022.3（作者の環境は 2022.3.22f1、Built-in RP） |
| VRChat SDK | 任意。コンパイル時には参照せず、実行時に型名で読みます。フィールド名は SDK 3.10.5（Avatars / Worlds / Base）の DLL と照合済みです。Worlds SDK が無いと `cct_world_audit` は `WORLDS_SDK_MISSING` で止まり、Avatars SDK が無いと `cct_avatar_audit` の数値はツール自身の推定値になります |
| MCP for Unity（`com.coplaydev.unity-mcp`） | `cct_*` ツールを使うときだけ必要。**9.7.1 以上**（同じリスティングの 9.7.101 = JP path fix で可）。入っていなければ MCP 用のアセンブリはコンパイルされず、エラーにもなりません |
| ワールドギミック | VRChat Worlds SDK（UdonSharp 同梱）。サンプルによって TextMeshPro・uGUI・PlayerData（Persistence）・`[NetworkCallable]`（SDK 3.8.1 以降）も使います |

アセンブリは次の 2 つで、どちらも Editor 専用です（プレイヤービルドには何も入りません）。

- `Naoking.ClaudeCreatorTools.Editor`: ツール本体・メニュー。MCP for Unity・Newtonsoft・VRChat SDK に依存しません。
- `Naoking.ClaudeCreatorTools.Mcp`: MCP アダプター。`versionDefines` で `com.coplaydev.unity-mcp` 9.7.1 以上があるときだけ `CCT_UNITY_MCP` を定義し、それを `defineConstraints` に置いています。Unity 2022.3.22f1 のコンパイラのソース（`EditorCompilation.GetTargetAssemblyDefines` → `CustomScriptAssembly.IsCompatibleWith`）で、自分の `versionDefines` が自分の `defineConstraints` に効くことを確認しています。

## 導入（VCC）

MCP for Unity と同じリスティング（`Claude-Unity 接続キット (MCP for Unity)`、`https://naoking1993.github.io/claude-unity-vpm/index.json`）から入れます。

1. VCC の **Settings → Packages** で、このリスティングが追加されていて、チェックが入っていることを確認します。無ければ **Add Repository** で上の URL を追加します。
2. プロジェクトの **Manage Project** を開き、「Manage Packages」の右の **Refresh（↻）** を押します（リスティングのキャッシュは最大 1 時間残ります）。
3. 一覧の **Claude Creator Tools** を追加（＋）します。版は `0.1.0` です。
4. Unity を開き、コンパイルが終わってから下の「初回確認」を行います。

MCP for Unity が入っているプロジェクトでは、追加・更新したあとに **MCP サーバーを再起動**してください（理由は次の節）。

## MCP ツール一覧

| ツール | 何をするか | 主な引数 | 書き込み |
| --- | --- | --- | --- |
| `cct_environment` | 接続中のプロジェクトの Unity 版・パス・シーン（未保存か）・Play 中か（コンパイル中は全ツールが `EDITOR_BUSY` で止まります）、VRChat 関連パッケージの版、アバタールートとワールド記述子（Blueprint ID 付き）、選択、出力先。**セッションの最初に呼ぶ** | `all_packages` | なし |
| `cct_avatar_audit` | アバター 1 体のアップロード前監査。PC とモバイルの性能ランクと、ランクを決めている項目・次のランクに必要な値（SDK の計算器を使える時は SDK と同じ数値）、SDK がアップロードを止めるエラー、Missing Script、Write Defaults の混在、Expression Parameters とメニュー | `target`, `platform`（all / pc / android / ios。Quest は android）, `top_n`, `include_lists`, `post_build`（NDMF で処理した一時クローンも監査。`timeout_seconds` 120 程度） | なし（`post_build` はプレビューシーンの一時クローンを作って消す） |
| `cct_world_audit` | ワールドシーンのアップロード前点検。`data.verdict` に SDK・UdonSharp が実際に止める項目、その他にライティング・ミラー・Udon 同期・動画プレイヤー・Android/iOS の制限・Editor.log のコンパイルエラー | `scene`（active / all / シーン名）, `platform`, `top_n`, `check_references`, `max_reference_scan`, `check_editor_log` | なし |
| `cct_capture_views` | 対象の正面を基準にした決まった角度から PNG を撮る。一覧画像 `contact_sheet.png`、前回の撮影との画素比較 | `target`, `views`（例 `front,back,left,right`、`turnaround`、`face`）, `mode`（isolated / scene）, `resolution`, `framing`, `background`, `compare_with`（前回のフォルダか `latest`） | `Library/ClaudeCreatorTools/Captures` にファイルだけ |
| `cct_snapshot` | 危ない編集の前のファイル単位のバックアップ（`.meta` 込み）、差分、復元、削除 | `action`（**必須**: create / list / diff / restore / delete）, `assets`, `include_dependencies`, `label`, `id`, `confirm` | create はスナップショットフォルダーにコピー。restore / delete は `confirm: true` のときだけ |
| `cct_animator_inspect` | AnimatorController のダンプと VRChat 向けの検査（WD 混在、未定義パラメーター、型の不一致、届かない遷移、Binding のパス切れ）。パラメーターの使用箇所や、あるプロパティを書くクリップの検索 | `target`, `controller`, `layer`（fx / gesture / … / all）, `layers`, `find_parameter`, `find_binding`, `check_bindings`, `max_states` | なし |
| `cct_dependency_report` | 依存関係（同梱すべきもの・外部パッケージ・未解決 GUID）、使用元の逆引き、シーン外への参照（プレハブ化すると切れる参照） | `mode`（dependencies / reverse / scene_references）, `assets`, `export_root`, `declared_packages`, `target`, `search_root` | なし（レポートと TSV だけ） |
| `cct_world_gimmicks` | UdonSharp ギミック 8 種の一覧と取り込み。UdonSharp・必要パッケージ・取り込み先を確かめてから、`.meta` ごとコピーし Program Asset も作る | `action`（list / import）, `confirm`, `dest`, `only`, `overwrite`, `create_program_assets` | import + `confirm: true` のときだけ `dest`（既定 `Assets/ClaudeWorldGimmicks`）に書き込む |

各ツールの引数の全部と既定値は、MCP の Description（`Editor/Mcp/<クラス名>Tool.cs`）に書いてあります。Issue コードの一覧は `Editor/Tools/<クラス名>.cs` の先頭のコメントにあります。

## Claude からの呼び出し方

Claude Code では、MCP サーバー名が `UnityMCP` なので、ツール名は `mcp__UnityMCP__cct_environment` のようになります。

1. **まず単独のツールとして呼ぶ**: `mcp__UnityMCP__cct_environment` などが一覧にあれば、それを使います。
2. **一覧に無いときは `execute_custom_tool` 経由で呼ぶ**:

   ```
   mcp__UnityMCP__execute_custom_tool(tool_name="cct_avatar_audit", parameters={"target": "/霧島", "platform": "android"})
   ```

   - 利用者のサーバー（9.7.1 + FastMCP 4 改修版、Python 3.14）では、引数を持つカスタムツールが単独ツールとして登録できない可能性があります（上流 9.7.1 のコードを Python 3.14 で動かすと `KeyError` で失敗することを再現済み。改修版に修正が入っているかは未確認）。`cct_*` は 8 個とも引数を持つので、この場合は全部が `execute_custom_tool` 経由になります。
   - この経路にはスキーマも既定値の補完もありません。引数名は snake_case でそのまま渡します（camelCase も受け付けます）。
   - 引数の説明は、リソース `mcpforunity://custom-tools` の `data.tools[].description` で読めます（引数ごとの説明は `parameters[].description`）。
   - `execute_custom_tool` は、サーバーが `--project-scoped-tools` 付きで起動しているときだけあります（利用者の起動コマンドには付いていると記録されています）。
3. **登録されているかの確かめ方**（上流 9.7.1 のコードでの方法）:
   - Unity の **Window > MCP for Unity** の **Tools** にある「Custom Tools」欄に `cct_*` が 8 個並び、オンになっていること。
   - ブラウザーで `http://127.0.0.1:8080/api/custom-tools` を開くと `cct_*` が載っていること。
   - サーバーログ `%LOCALAPPDATA%\UnityMCP\Logs\unity_mcp_server.log` に `Failed to register custom tool 'cct_…'` があれば、単独ツールとしては登録されていません（`execute_custom_tool` では呼べます）。

### 更新したら MCP サーバーを再起動する

- このパッケージを入れた・更新したあとは、**Window > MCP for Unity の Stop Server → Start Server** でサーバーを再起動し、Claude Code 側も `/mcp` から UnityMCP を再接続します（新しい会話を始めても構いません）。
- 理由: 9.7.1 のサーバーは、同じ名前のツールが既にあると「already registered with a different schema; keeping existing definition.」として**古い説明・引数のまま**にします。新しいツールの追加はコンパイル後の再登録で反映されることがありますが、既存ツールの説明や引数の変更は、サーバーを再起動するまで反映されません。

### ツールのオン・オフは全プロジェクト共通

- **Window > MCP for Unity > Tools** のトグルは EditorPrefs（`MCPForUnity.ToolEnabled.<ツール名>`）に保存されます。EditorPrefs は PC のユーザー単位なので、**あるプロジェクトで切ると、他のプロジェクトでも切れます**。
- 切ったツールを呼ぶと Unity が `Tool 'cct_…' is disabled in the Unity Editor.` を返します。単独ツールとして登録済みのものは、サーバーを再起動するまで一覧に残ります。単独ツールとして登録されていないものを `execute_custom_tool` で呼ぶと、サーバーが `Tool 'cct_…' not found for project …` を返します（パッケージが入っていない・コンパイルが止まっている・再接続前のときも同じ表示です）。
- 各ツールの説明文は長めで、単独ツールとして一覧に出ていると会話のたびにトークンを使います（`execute_custom_tool` 経由だけのときは、リソース `mcpforunity://custom-tools` を読んだときだけ使います）。アバターしか作らない時期にワールド用（`cct_world_audit`・`cct_world_gimmicks`）を切る、という使い方はできますが、上のとおりワールドプロジェクトでも切れます。

### 時間の上限（30 秒）

- MCP の 1 回の呼び出しは、既定で 30 秒で打ち切られます。`timeout_seconds` を渡すとサーバーが待つ時間を延ばします（サーバーの上限は 3600）。
- ただし Claude Code は、HTTP の MCP サーバーから 5 分間何も返ってこない呼び出しを中断します（Claude Code のドキュメント。`CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT` で変更可）。**`timeout_seconds` は 240 以下**にしてください。
- 大きいアバターの `post_build`、高解像度・多方向の撮影、大きいプロジェクトの逆引き、数百 MB のスナップショットは 30 秒を超えることがあります。各ツールの説明にある目安（120〜240）を渡します。
- `cct_world_audit`・`cct_dependency_report`・`cct_animator_inspect` は `timeout_seconds` に合わせて走査を途中で止め、「途中までの結果」だと issue で示します（`cct_world_audit` は `REFERENCE_SCAN_TRUNCATED` / `USHARP_SCAN_TRUNCATED`、`cct_dependency_report` は `SCAN_INCOMPLETE` と `conclusive: false`、`cct_animator_inspect` は `TRUNCATED`）。
- タイムアウトしても Unity 側の処理は続いています。**同じ呼び出しをすぐに繰り返さず**、出力（キャプチャフォルダー、`cct_snapshot action=list` など）を先に確かめてください。

## 共通の引数と戻り値

### 共通の引数（全ツール）

| 引数 | 既定 | 意味 |
| --- | --- | --- |
| `detail` | `summary` | `summary` は短い応答（error が先頭）。`full` はレポート全文をそのまま返す（長い） |
| `max_issues` | 30 | 応答に入れる issue の数（error → warning → info の順） |
| `save_report` | true | 全文の JSON を `Library/ClaudeCreatorTools/Reports` に保存し、`report_path` で返す |
| `timeout_seconds` | 30 | サーバーの待ち時間。上の「時間の上限」を参照 |

詳しい内訳が要るときは、`detail=full` で呼び直すより、`report_path` のファイルを読む方が確実です（Claude Code は 1 回のツール結果が 25,000 トークンを超えると切り詰めるため）。

### 対象（`target`）の書き方

| 書き方 | 意味 |
| --- | --- |
| 空 / `selection` | Hierarchy で選択中の GameObject。`cct_avatar_audit` と `cct_animator_inspect` は、選択から上にたどって VRCAvatarDescriptor のあるルートを対象にし、何も選択していなければ読み込み中のシーンにいる唯一のアバターを対象にします（`TARGET_ADJUSTED` で知らせます）。`cct_animator_inspect` は、Hierarchy で何も選択せず Project でコントローラを選択しているときは、そのコントローラを解析します |
| `/Root/Child` | 読み込み中のシーンの階層パス（`Assets/X.unity:/Root/Child` でシーンを指定できます） |
| `#12345` | インスタンス ID。ドメインリロード（コンパイル）で変わるので使い捨てです |
| `名前` | 読み込み中のシーンで一意な GameObject 名 |
| `Assets/…/X.prefab` | プレハブアセット（読み取りに使います） |

候補が複数あるときは推測せずに `AMBIGUOUS_TARGET` で失敗し、候補のパスとインスタンス ID を並べます（`cct_avatar_audit`・`cct_animator_inspect` は `data.candidates`、`cct_capture_views` は `data.details.candidates`、`cct_dependency_report` は `data.details`）。

### 戻り値の形

MCP の応答は、成功時は `{success: true, message, data}`、失敗時は `{success: false, error, data}`（`error` に日本語の一行要約）です。`data` の中身は成功・失敗とも次のとおりです。

- `success`, `message`（日本語の一行要約）, 失敗時は `code`（`EDITOR_BUSY` などの固定名）
- `issue_counts`（`error` / `warning` / `info` の件数）, `issues`（error が先頭。`max_issues` 件まで）, 入りきらなかった件数 `issues_omitted`
- 各ツールのデータ（要約に入るキーはツールごとに決まっています。一覧の長いものはレポートだけにあります）
- `tool`, `tool_version`（このパッケージの版。`0.1.0`）, `elapsed_ms`
- `report_path`: 全文 JSON の絶対パス（`/` 区切り、日本語のまま）。Claude は自分のファイル読み取りで開きます

issue は `{severity, code, message, path?, asset?, value?, limit?}` です。`code` は大文字の固定名で、スキルや会話から参照できます。`message` は日本語です。

| severity | 意味 |
| --- | --- |
| `error` | アップロードや動作を確実に壊す、または確実に誤り（SDK がアップロードを止めないものもあります。止めるかどうかは各ツールの `upload` / `verdict` を見ます） |
| `warning` | 直す価値が高い問題や負荷 |
| `info` | 判断の材料。対応は不要 |

全ツール共通の失敗コード:

| code | 意味と対処 |
| --- | --- |
| `EDITOR_BUSY` | Unity がコンパイル中（スナップショットの復元は、インポート中・ベイク中も）。終わってから再実行 |
| `EXCEPTION` | 予期しない例外。`data.details.stack` にスタックの抜粋 |
| `INVALID_ARGUMENT` | 引数の値が不正（`platform` の綴り違いなど）。メッセージに使える値があります |
| `TARGET_NOT_FOUND` / `AMBIGUOUS_TARGET` | 対象が見つからない・複数ある。上の「対象の書き方」の候補一覧から選び直す |

## Tools メニュー

すべて `Tools > Claude Creator Tools` の下にあります。どの項目もダイアログを出さず（MCP の `execute_menu_item` から呼んでも止まりません）、Console に 1 行の要約とレポートのパスを出します（「一覧をログ出力」「一覧と導入状況をログ出力」は続けて一覧も出します。スナップショットの一覧はレポートを保存しません）。

| メニュー | 中身 |
| --- | --- |
| 環境情報を出力 (cct_environment) | `cct_environment` |
| アバター監査 (cct_avatar_audit) > 選択中のアバターを監査 (PC + モバイル) / 選択中のアバターを監査 + NDMF 処理後の値 | `target=selection`、`platform=all`（2 つ目は `post_build=true`。Play 中は押せません） |
| アニメーター解析 (cct_animator_inspect) > 選択中のアバターの FX / 選択中のアバターの Gesture / 選択中のアバターの全レイヤー (+ MA Merge Animator) / Project で選択中のコントローラ | `layer=fx` / `gesture` / `all`、最後は `controller=<選択したコントローラ>` |
| キャプチャ (cct_capture_views) > 選択中を4方向 / 選択中を顔3方向 / 選択中をシーン照明で4方向 | `views=front,back,left,right` / `face`（`mode=isolated`）、最後は `mode=scene`。撮影フォルダーのパスも Console に出します |
| ワールド監査 (cct_world_audit) > アクティブシーン (全プラットフォーム) / アクティブシーン (PC) / アクティブシーン (Android) / アクティブシーン (iOS) | `scene=active`、`platform=all` / `pc` / `android` / `ios`。メニューからは `timeout_seconds=300` 相当まで走査します |
| 依存関係 (cct_dependency_report) > 選択アセットの依存関係 / 選択アセットの使用元を検索 / 選択オブジェクトのシーン外参照 | `mode=dependencies` / `reverse`（Project の選択）/ `scene_references`（Hierarchy の選択） |
| スナップショット (cct_snapshot) > 開いているシーンと依存アセットを保存 / 選択アセットを保存 / 一覧をログ出力 | `action=create`（開いているシーン + 編集可能な依存アセット）/ `create`（選択アセットだけ）/ `list`。復元と削除はメニューに置いていません（確認なしに実行できないため） |
| ワールドギミック (UdonSharp) > 一覧と導入状況をログ出力 / Assets/ClaudeWorldGimmicks にインポート | クリックが同意の代わりなので、インポートは `confirm=true` で実行します（既存ファイルは上書きしません） |
| レポートフォルダを開く / キャプチャフォルダを開く / スナップショットフォルダを開く | そのフォルダーをエクスプローラーで表示（無ければ作ってから） |

## 出力先

| 種類 | 場所 | 消してよいか |
| --- | --- | --- |
| レポート（JSON、依存関係の TSV） | `<プロジェクト>/Library/ClaudeCreatorTools/Reports/<ツール名>-<日時>-<英数字4文字>.json`（ワールド監査と依存関係のメニューから実行したものは、`cct_world_audit-world-pc-…`・`cct_dependency_report-deps-…`（`users` / `scene-refs`）のように間に名前が入ります。依存関係の一覧は `cct_dependency_report-files-<日時>-<英数字4文字>.tsv`、メニューからは `cct_dependency_report-deps-files-…tsv`。同じ名前があれば末尾に `-2` などを付けます） | いつでも消せます。溜まる一方なので、ときどき「レポートフォルダを開く」から整理してください |
| キャプチャ | `<プロジェクト>/Library/ClaudeCreatorTools/Captures/<日時>-<英数字4文字>_<ラベル>/`（`<view>.png`、`contact_sheet.png`、`manifest.json`、比較時は `compare_<view>.png`・`diff_<view>.png`） | 消せます。ただし `compare_with` で比べる元になる撮影は残してください |
| スナップショット | `<プロジェクト>/ClaudeCreatorTools/Snapshots/<id>/`（コピーしたファイル + `manifest.json`） | `cct_snapshot action=delete id=… confirm=true` か、フォルダーごと削除 |
| 一時フォルダー | `Assets/__ClaudeCreatorToolsTemp`（未保存のシーンをスナップショットする間だけ作り、すぐ消します） | 残っていたら消して構いません |
| ワールドギミック | `Assets/ClaudeWorldGimmicks/`（`dest` で変更可） | 普通のアセットです |

スナップショットについての注意:

- `Library/` は Unity が作り直すことがあるので、スナップショットだけは `Library/` の外（プロジェクト直下の `ClaudeCreatorTools/`）に置いています。`Assets/` の外なので Unity は取り込みません。**`Assets/` や `Packages/` の中へ移さないでください**（GUID が重複します）。
- git で管理しているプロジェクトでは、`.gitignore` に `/ClaudeCreatorTools/` を足してください（購入アセットのコピーが入ることがあるので、公開リポジトリに上げないため）。Unity 標準の `.gitignore` は `Library/` を無視するので、レポートとキャプチャは足さなくても入りません。
- プロジェクトフォルダーを丸ごとコピー・zip にするバックアップにはスナップショットも入り、大きくなります（VCC の Make Backup が含めるかは未確認です）。不要になったスナップショットは消してください。

## 安全の考え方

- **読み取り専用のツール**: `cct_environment`, `cct_avatar_audit`, `cct_world_audit`, `cct_capture_views`, `cct_animator_inspect`, `cct_dependency_report`。シーンにもアセットにも書き込みません（`SetDirty`・保存・Undo 登録をしません）。一時物は、撮影用のクローン・カメラ・ライトと `post_build` のクローンなら見えない一時シーン（プレビューシーン）に、`cct_capture_views` の `mode=scene` の撮影用カメラなら保存されない非表示のオブジェクト（`HideAndDontSave`）として作り、`finally` で消します。シーンが意図せず変更済みになったときは `SCENE_DIRTIED` で知らせます。書くのはレポートとキャプチャのファイルだけです（例外: `post_build` の処理中に NDMF のプラグインが一時アセットを書いた場合は、その呼び出しの間に増えたものだけを消して `POST_BUILD_CLEANUP_FAILED` で知らせます）。
- **書き込むツール**は `cct_snapshot` と `cct_world_gimmicks` だけです。プロジェクトのファイルを変える操作（復元・削除・取り込み）は、どちらも既定がドライラン（予定の一覧だけを返す）です。`cct_snapshot action=create` は確認なしで実行しますが、書くのはスナップショットフォルダー（と未保存シーン用の一時フォルダー）だけです（未保存のシーンは一時フォルダーへのコピー保存で取り出すので、他のパッケージのシーン保存時の処理は Ctrl+S のときと同じように動きます）。
  - `cct_snapshot action=restore` / `delete` と `cct_world_gimmicks action=import` は、`confirm: true` を付けたときだけ実行します。付けないと `CONFIRM_REQUIRED` と予定の一覧を返します。Claude は**予定を本人に見せて、同意をもらってから** `confirm: true` で呼び直します。
  - 復元の前には、そのときの状態（未保存のシーンの変更も含む）を自動でスナップショットし、`pre_restore_snapshot_id` を返します。それを復元すれば元に戻せます（復元で新しく作られたファイルは消えません）。
  - `cct_world_gimmicks` は、UdonSharp が無いプロジェクトでは取り込みを拒否します（`NO_UDONSHARP`）。既存ファイルは `overwrite: true` が無い限り上書きせず、既存の `.meta` は常に残します。
- **破壊的な操作の前にはスナップショット**: MCP 経由の削除は Unity の Undo に載りません。また、シーンのコピーだけでは、後から変えた共有のマテリアル・メッシュ・AnimationClip は戻せません。削除・一括変更・インポート設定の変更の前に `cct_snapshot action=create` を呼び、変えるアセットを `assets` に入れるか `include_dependencies: true` を付けます。`Packages/` の中はコピーしません。
- ビルド・アップロード・ClientSim の実行はどのツールもしません。

## ワールドギミック（サンプル）

`Samples~/WorldGimmicks` に、UdonSharp のスクリプト 8 種（ローカル/全員共有のトグル、ミラー切り替え、テレポーター、ピックアップのリセット、音量スライダー、エリア内だけ表示、人数表示）があります。詳しい設定とテスト手順は [Samples~/WorldGimmicks/README.md](Samples~/WorldGimmicks/README.md) にあります。

- **ワールドプロジェクト専用です。** アバタープロジェクトには UdonSharp が無いので、入れるとコンパイルエラーでプロジェクト全体のスクリプトが止まります。
- 取り込みは次のどちらかで行います。どちらも UdonSharp の有無と必要パッケージを確かめ、`.meta` ごとコピーし（GUID が安定します）、各スクリプトの UdonSharp Program Asset を作ります。
  - Claude から: `cct_world_gimmicks` を `action=list` → `action=import`（ドライラン）→ 本人の同意 → `action=import, confirm=true`
  - メニューから: `Tools > Claude Creator Tools > ワールドギミック (UdonSharp) > Assets/ClaudeWorldGimmicks にインポート`
- **Package Manager の「Samples > Import」は使わないでください。** UdonSharp の有無を確かめず（アバタープロジェクトではコンパイルエラーになります）、Program Asset も作りません。既にそちらで取り込んでいる場合、上の方法はそのスクリプトを取り込まずに `SAMPLE_EXISTS_ELSEWHERE` で知らせ、足りない Program Asset だけをそのコピーの隣に作ります。同じスクリプトを手で別のフォルダーにもコピーすると、同じクラスが 2 つになりコンパイルエラー（CS0101）になります。

## 初回確認

導入した PC で、上から順に行います。日本語名のプロジェクト（例: `霧島`、`泡ワールド`）で行ってください。

1. **コンパイル**: Unity の Console に赤いエラーが無いこと。念のため Editor.log（`%LOCALAPPDATA%\Unity\Editor\Editor.log`）に `error CS` が無いこと（Console が 0 件でもコンパイルが止まっていた例があるため）。
2. **メニュー（MCP 不要）**: **Tools > Claude Creator Tools > 環境情報を出力 (cct_environment)** を押します。
   - 合格: Console に次の形の行が出ること。
     ```
     [Claude Creator Tools] cct_environment: OK - Unity 2022.3.22f1 / avatar / シーン 1 個 / アバター 1 体 / ワールド記述子 0 個 (error 0 / warning 0 / info 0)
     report: C:/Users/…/VRChatProjects/霧島/Library/ClaudeCreatorTools/Reports/cct_environment-20261025-….json
     ```
   - `avatar` の所はプロジェクトの種類（`avatar` / `world` / `avatar+world` / `no_vrchat_sdk`）です。未保存のシーンがあると info が 1 件増えます（`SCENE_UNSAVED`）。
   - **レポートフォルダを開く** で、その JSON があり、メモ帳で開くと `project_root` の日本語が文字化けしていないこと。
3. **MCP の登録**: **Window > MCP for Unity** で Stop Server → Start Server を押し、**Tools** の「Custom Tools」に `cct_*` が 8 個あり、オンになっていること。Claude Code では `/mcp` で UnityMCP を再接続します。
4. **Claude から呼ぶ**: Claude に「cct_environment を実行して」と頼みます。
   - 合格: `success: true`、`tool_version: "0.1.0"`、`project_root` が開いているプロジェクトと同じであること。
   - そのとき Claude が `mcp__UnityMCP__cct_environment` を直接呼べたか、`execute_custom_tool` 経由だったかを記録しておいてください（サーバーの Python 3.14 の問題が起きているかが分かります）。
5. **撮影（アバタープロジェクト）**: Hierarchy でアバターのルートを選び、Claude に「cct_capture_views で撮って、一覧画像を見て」と頼みます（メニューなら **キャプチャ (cct_capture_views) > 選択中を4方向**）。
   - 合格: `Library/ClaudeCreatorTools/Captures/<日時>-<英数字4文字>_<アバター名>/contact_sheet.png` に、front / back / left / right の 4 枚が名前付きで並び、アバター全身が切れずに中央にあり、背景が灰色（#808080）であること。Claude がその PNG を読んで見た目を説明できること。
   - シーンのタブに未保存の印（`*`）が新しく付いていないこと（読み取り専用の確認）。
6. **監査（アバタープロジェクト）**: **アバター監査 (cct_avatar_audit) > 選択中のアバターを監査 (PC + モバイル)**。
   - 合格: Console が `OK` で、メッセージに `[推定値]` が付いていないこと（付いていれば SDK の計算器を使えていません）。今のビルドターゲット（PC なら PC）の総合ランクが、VRChat SDK の Control Panel（Builder タブ）の表示と同じであること。
7. **スナップショット**: シーンを保存してから（Ctrl+S）、**スナップショット (cct_snapshot) > 開いているシーンと依存アセットを保存**。
   - 合格: `<プロジェクト>/ClaudeCreatorTools/Snapshots/<id>/` にファイルと `manifest.json` ができること。続けて Claude に `cct_snapshot action=diff id=<id>` を頼み、変更 0 / 削除 0 であること。
8. **ワールドプロジェクトでは**: **ワールド監査 (cct_world_audit) > アクティブシーン (全プラットフォーム)** が `OK` で終わること。Claude から `cct_world_gimmicks`（`action=list`）を呼び、`udonsharp_installed: true` と 8 種の一覧が返ること。

どれかが合わないときは、Console の行・レポートの JSON・サーバーログ（`%LOCALAPPDATA%\UnityMCP\Logs\unity_mcp_server.log`）を残して、作者に知らせてください。

## 既知の制限

- **Unity の中で一度も実行していません（0.1.0）。** 確かめたのは次の範囲だけです。
  - Roslyn（C# 9）で、**Unity 2021.1 の参照 DLL** と、MCP for Unity 9.7.1 のソースから作ったスタブに対してコンパイルできること（Unity 2022.3 で動くかは未確認。2022.3 にしか無い API は使っていません）。
  - MCP for Unity 9.7.1 の登録規則（明示した名前、引数の順序、Description にすべての引数の説明があること）を満たすこと。
  - ツールが読む VRChat の型とプロパティのパスが、**VRChat SDK 3.10.5 の実際の DLL** にあること（他の版は未確認）。
  - ワールドギミックが、SDK 3.10.5 の DLL・SDK 同梱の UdonSharp ランタイム・Unity 2021.3 のエンジン DLL に対してコンパイルでき、U# が扱えない構文と Udon に公開されていない API を使っていないこと（U# コンパイラそのもの・ClientSim・VRChat では未確認）。
- 利用者のサーバー（9.7.1 + FastMCP 4 改修版、Python 3.14）で、`cct_*` が単独ツールとして出るか、`execute_custom_tool` 経由になるかは未確認です。
- 監査は編集時点の静的な点検です。SDK のビルド・NDMF 処理後（`post_build` は NDMF だけを動かし、VRCFury などは含みません）・エミュレーター・VRChat 実機の結果とは別物として扱ってください。
- 撮影した画像は「その条件でのエディターの見た目」で、VRChat 実機の見た目ではありません。`isolated` モードはシーンの照明・ポストプロセス・NDMF/MA のプレビューを再現しません。
- 依存関係の GUID 走査は、テキストとして保存されたアセットが対象です。バイナリのファイルと、コードからの名前での参照（`Shader.Find`、`Resources.Load`）は網羅しません。
- 0.x のあいだは、issue コード・引数・出力のキーが変わることがあります。変わったら CHANGELOG に書きます。
- MCP for Unity 10.x では、コンパイルはされる見込みですが未確認です（`versionDefines` は「9.7.1 以上」なので 10.x でも有効になります）。

## ライセンス

MIT License（[LICENSE.md](LICENSE.md)）。© 2026 naoking1993。VRChat SDK・MCP for Unity・UdonSharp などは同梱していません。それぞれのライセンスに従って別に入れてください。このパッケージは VRChat・CoplayDev とは関係のない非公式のものです。
