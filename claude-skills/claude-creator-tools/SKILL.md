---
name: claude-creator-tools
description: Unityに入れた自作VPMパッケージ Claude Creator Tools のMCPツール cct_environment・cct_avatar_audit・cct_world_audit・cct_capture_views・cct_snapshot・cct_animator_inspect・cct_dependency_report・cct_world_gimmicks を、作業のどの工程で、どの引数で呼び、戻り値・レポートJSON・PNG画像をどう読むかを決めるときに使う。アバターのパフォーマンスランクとアップロード前点検、同条件の多方向撮影と前後比較、変更前のバックアップと復元、FXなどAnimatorの解析と逆引き、依存関係と使用元の調査、ワールドの点検とUdonSharpギミックの取り込みを扱う。「アバターのパフォーマンスランク見て」「ランク見て」「何が重いか調べて」「軽量化前後で比べて」「4方向から撮って」「顔を撮って」「消す前にバックアップ取って」「元に戻して」「何が変わったか見せて」「FXレイヤーの遷移見て」「この表情を書いてるのはどこ」「このギミックを別アバターに移したい」「これ使ってる所ある？」「ワールドのアップロード前チェック」「ワールドにテレポーター入れて」「cctが見えない」等の相談で、軽い言い方でも使う。Unity・MCPの接続確認と破壊的操作の規律はunity-mcp-operations、何をどう直すかの判断はvrchat-avatar-optimization・vrchat-avatar-material-tuning・vrchat-expression-debugging・vrchat-gimmick-packaging・vrchat-hand-prop-setupなどのvrchat-*専門スキルとvrchat-world-creationへ。Unityを使わない作業（Blenderでの髪・衣装の制作やレンダー、BOOTH・VRCFinderでの商品探し）には使わず、blender-avatar-hair・blender-avatar-outfit-fitting・vrchat-asset-researchへ。
---

# Claude Creator Tools（cct_*）の使い方

**ステータス: v0（2026-10-25 作成・Claude Creator Tools 0.1.0 に対応・Unity実機で未検証）。**

自作の VPM パッケージ `io.github.naoking1993.claude-creator-tools`（表示名 Claude Creator Tools）が MCP for Unity に登録する8つの `cct_*` ツールを、どの工程で、どの引数で呼び、戻り値・レポート・画像をどう読むかをまとめる。何をどう直すかは決めない。それは各専門スキルが決める。引数・既定値・出力キー・issue コードは、パッケージのコード（`Editor/Mcp/*Tool.cs` の Description と Parameters、`Editor/Tools/*.cs` の冒頭コメントと `Run()`）から写した。記憶で補わない。

Unity への接続確認、破壊的操作の前の確認、計画の提示、1件での試行、失敗やタイムアウトの後に再送せず読み戻すこと、といった MCP で Unity を操作するときの規律は `unity-mcp-operations` が正で、このスキルもそれに従う。ここに書くのは `cct_*` の呼び方と出力の読み方だけ。Unity を使わない作業（Blender での制作、BOOTH での商品探し）には使わない。

このスキルの作成・参照だけを頼まれた場合は、Unity を変更しない。スキルを読み込んだだけでは、Unity への接続や操作を許可したことにはならない。

書き込みをする操作は3つだけ：`cct_snapshot` の `restore` と `delete`、`cct_world_gimmicks` の `import`。この3つはユーザーの依頼があるときだけ使う。まず `confirm` を付けずに呼んでドライランの結果を見せ、同意を得てから `confirm: true` で呼ぶ。Tools メニューの「ワールドギミック (UdonSharp)/Assets/ClaudeWorldGimmicks にインポート」は確認なしで書き込むので、`execute_menu_item` でドライランを飛ばさない。「元に戻して」と言われても、戻せるのは `cct_snapshot` で退避したものだけ。`list` に戻す元が無ければそう伝え、`unity-mcp-operations` の手順に従う（MCP の削除は Undo に載らない）。

ほかの操作もファイルは書く。レポート JSON と撮影画像は `Library/ClaudeCreatorTools/` に、`cct_snapshot` の `create` は `<プロジェクト>/ClaudeCreatorTools/Snapshots/` に書く（未保存のシーンを写すときだけ `Assets/__ClaudeCreatorToolsTemp` に一時コピーを作り、消す）。シーンやアセットの中身は変えない。

ビルド・アップロード・公開はどのツールもしない。点検や撮影の依頼だけで、Claude がアップロードや公開まで進めることもしない。結果はエディタ上の静的な値であり、SDK のビルド、エミュレーター、VRChat 実機での確認ではない。

## 最初に確かめること

1. **接続**：Unity MCP の tool が今のセッションに実在するか、接続先が意図したプロジェクトかは `unity-mcp-operations` の原則1で確かめる。
2. **パッケージ**：Claude Creator Tools（0.1.0）がプロジェクトに入っているか。MCP ツールとして登録されるのは、MCP for Unity（`com.coplaydev.unity-mcp`）9.7.1 以上が入っているときだけ。入っていなければ Unity の Tools > Claude Creator Tools のメニューだけが使える。
3. **見え方**：`cct_*` が単独の tool として一覧にあるか。無ければ `execute_custom_tool(tool_name="cct_...", parameters={...})` で呼ぶ。ユーザーのサーバーは Python 3.14 で動いており、引数を持つカスタムツールは単独 tool として登録されない可能性がある（8つとも引数を持つ）。どちらでも呼べなければ [トラブル対応](references/troubleshooting.md) の1章。
4. **最初の呼び出し**：`cct_environment` を呼ぶ。Unity の版、プロジェクトの種類（`project_kind`）、開いているシーンと未保存の有無、Play 中か、VRChat 関連パッケージの版、シーン内のアバターとワールド記述子（Blueprint ID 付き）、選択中のオブジェクト、出力フォルダーを1回で返す。コンパイル中なら `EDITOR_BUSY` で失敗する（返る `is_compiling` はふつう false）。Editor.log のコンパイルエラーは数えない（ワールドなら `cct_world_audit` が数える）。
5. **更新の後**：パッケージを更新してツールの説明や引数が変わったときは、MCP サーバーを再起動するまで古い定義のまま動く。ユーザーに MCP for Unity ウィンドウで Stop Server → Start Server を押してもらい、Claude Code 側は `/mcp` で再接続する。
6. **Play とコンパイル**：コンパイル中はどのツールも `EDITOR_BUSY` で止まるので、終わってから呼ぶ。Play 中は実行時の状態を読む（`PLAY_MODE`）。アップロード前の状態を見るときは Play を止めてから呼ぶ。

## 8つのツール

| ツール | 目的 | 書き込み | 最初に読む出力 |
| --- | --- | --- | --- |
| `cct_environment` | 接続中のプロジェクトの要約 | なし | `message`、`project_kind`、`scenes`、`packages`、`avatar_roots` |
| `cct_avatar_audit` | アバター1体の性能ランクとアップロード前の点検 | なし（`post_build` は一時クローンを NDMF で処理して破棄） | `message`、`upload.blocked`、`ranks`、`issues` |
| `cct_world_audit` | ワールドのシーンのアップロード前の点検 | なし | `verdict`、`issues` |
| `cct_capture_views` | 決まった角度の PNG と、前回との画素比較 | Library に PNG を書くだけ | `contact_sheet`、`comparisons` |
| `cct_snapshot` | 変更前のファイル退避、差分、復元 | `create` は退避先に書く。`restore`・`delete` は `confirm: true` のときだけ | `id`、ドライランの `would_*` |
| `cct_animator_inspect` | Controller の表、VRChat 向けの検査、パラメーターと Binding の逆引き | なし | `issues`、`layers`、`find` |
| `cct_dependency_report` | 依存、使用元、シーン外への参照 | なし（レポートと TSV だけ） | `conclusive`、`issues`、`required_packages` |
| `cct_world_gimmicks` | UdonSharp ギミックの一覧と取り込み | `import` と `confirm: true` のとき Assets に書く | `importable`、`plan`、`next_steps` |

## 結果の読み方（全ツール共通）

- 戻り値は要約。`data.message`（日本語の1行）→ `data.issue_counts` → `data.issues`（error から順、既定30件まで、超えた数は `issues_omitted`）→ ツールごとの要約キー → `data.report_path`、の順に読む。
- 全文は `report_path` の JSON（`<プロジェクト>/Library/ClaudeCreatorTools/Reports/`）にある。要約で足りないときだけ開き、Grep で必要なキーに絞る。`detail: "full"` で全文を会話に流さない。
- 失敗すると `success: false`。原因のコードは `data.code`（`TARGET_NOT_FOUND`・`AMBIGUOUS_TARGET`・`INVALID_ARGUMENT`・`EDITOR_BUSY` など）。対象の候補は `data.candidates`（`cct_avatar_audit`・`cct_animator_inspect`）か `data.details`（`cct_capture_views` は `details.candidates`、`cct_dependency_report` は `details`）に並ぶ。推測で選ばず、ユーザーに選んでもらう。
- issue の `code` は変わらない英字のコード、`message` は日本語の説明。コードの意味は [ツールの仕様](references/tool-reference.md)。
- 画像は会話に返らない。PNG の絶対パスが返るので Read で開く。最初は `contact_sheet`（全ビューを並べた1枚）だけを開く。
- 対象の指定（`target`）：`""` か `"selection"` は選択中のもの、`"/Root/Child"` は階層パス、`"#12345"` は instance ID（ドメインリロードで変わる）、ほかにシーン内でユニークな名前、`"Assets/.../X.prefab"`。候補が複数あると `AMBIGUOUS_TARGET` で失敗するので、ユーザーに選んでもらってから呼び直す。
- 時間：1回の呼び出しは既定30秒で打ち切られる。大きいアバター・ワールド、`post_build`、高解像度の撮影には `timeout_seconds`（例 120）を渡す。240 より大きくしない（Claude Code は HTTP の MCP 呼び出しから5分間何も返らないと中断する）。打ち切られても Unity 側で処理が続くことがあるので、書き込み系は再送せず `list`・`diff` で状態を確かめる。

## 作業に応じて読む

| 作業 | 参照 |
| --- | --- |
| 引数・既定値・出力キー・issue コード・副作用・呼び出しの JSON（単独 tool と `execute_custom_tool` の両方） | [ツールの仕様](references/tool-reference.md) |
| 前後比較、撮影、退避と復元、表情の鎖の追跡、移植前の確認、ワールドの点検の手順と、レポート・PNG の読み方 | [工程ごとの手順](references/workflows.md) |
| ツールが見えない、disabled、タイムアウト、古い定義、コンパイルエラー、日本語パス、出力の場所、ツールが知り得ないこと | [トラブル対応](references/troubleshooting.md) |
| 何を確かめたか、ユーザーの PC で未実測のこと | [検証済み事実の台帳](references/verified-facts.md) |

参照は必要な工程で読む。このスキルを使うたびに全てを読み込まない。

## 関係する専門スキル

判断の正は左の専門スキル。ここではその工程で使うツールと、そろえる条件だけを書く。手順は [工程ごとの手順](references/workflows.md)。

| 専門スキル | 工程 | 使うツールと要点 |
| --- | --- | --- |
| `vrchat-avatar-optimization` | 作業前の性能値と画像を残す、修正後に比べる | 作業前に `cct_snapshot create`（変更する Mesh・Material・Clip を `assets` に入れる）。`cct_avatar_audit` と `cct_capture_views` で基準を取り、修正後に同じ引数で取り直す。撮影は `compare_with` に作業前の `capture_dir` を渡す。前後は同じ段階どうし（どちらも `post_build: false`、または両方 `true`）で比べる |
| `vrchat-avatar-material-tuning` | 同じ条件で基準と候補を撮る | `cct_capture_views` を固定の引数で撮る（`mode` は isolated のまま、`views` は front,front_left,front_right や `face`）。承認済みの状態の `capture_dir` を `compare_with` に渡す（`latest` ではなく）。部位ごとの RGB 差は、同じ寸法の PNG に `scripts/compare_regions.py` を使う。Blueprint ID は `cct_snapshot diff` の `BLUEPRINT_ID_CHANGES` で見張る |
| `vrchat-expression-debugging` | 制御の鎖を境界ごとに読む | `cct_avatar_audit` の `expressions`（パラメーターの型・既定値・saved・synced、メニューの木、MA の部品数）と、`cct_animator_inspect`（`layer: "all"`、`find_parameter`、`find_binding`）。どちらもビルド前の静的な値で、統合後の Controller と実行時の値（current）は読めない |
| `vrchat-gimmick-packaging` | 成功判定①②と、収録一覧の想定 | `cct_dependency_report` の `scene_references`（シーン外参照・Missing、①）→ 独立プレハブに保存 → `dependencies`（`export_root`・`declared_packages`、②。TSV が④の想定一覧になる）→ 元を消す前に `reverse`。書き出した unitypackage の中身は読めない。③は導入先で `cct_avatar_audit` の `post_build` と `cct_animator_inspect` |
| `vrchat-hand-prop-setup` | 導入時の欠落確認、握り位置の目視 | 導入前に `cct_snapshot create`。`cct_avatar_audit` の `MISSING_SCRIPT`・`MISSING_SCRIPT_GUID`（m_Script の GUID）・`HUMANOID_OPTIONAL_BONES_MISSING`（指）。撮影は `cct_capture_views` の `views: "turnaround"`・`framing: "upper"` で両手と側面、近接は `target` を小物にして `mode: "scene"`。`face` プリセットは頭部の構図なので、持ったときの表情を見るときだけ使う |
| `vrchat-bpm-controls` | State の速度設定を読む | `cct_animator_inspect` のレポートの `graph` で `speed`・`speed_param`・`motion_time_param` と Motion を読む（`speed` は 1 以外のときだけ、パラメーターは有効なときだけキーが入る）。2つの Controller を直接比べる機能は無い（2回呼んでレポートを比べる） |
| `unity-mcp-operations` | 原則2の破壊的操作の前後 | 前に `cct_snapshot create`（変更するアセットと開いているシーン）。後に `diff` で何が変わったかを確かめ、戻すなら `restore` をドライラン → 同意 → `confirm: true`。MCP の削除は Undo に載らないので、削除の前は必ず退避する |
| `vrchat-world-creation` | 点検、ギミック、ライティングの前後 | `cct_world_audit` の `verdict` を先に読む。ギミックは `cct_world_gimmicks` の `list` → ドライラン → `confirm: true`。ライティングの前後は `cct_capture_views` を `mode: "scene"` で撮る。ベイクのやり直しや置き換えの前は `cct_snapshot`、購入アセットを消す前は `cct_dependency_report` の `reverse` |

## 完了の判断

求められた段階で、そろえた条件で確かめた結果だけを合格とする。

- `cct_avatar_audit` の `upload.blocked: false` は SDK ビルドの成功ではない。`post_build` の値は NDMF だけで処理した一時クローンの値で、VRCFury や SDK のビルド処理は通っていない。
- `cct_world_audit` はビルドも ClientSim もしない。`verdict.sdk_build_blocked: false` は「このツールで判定できる止まる条件が無い」という意味。
- `cct_capture_views` の画像はエディタでの見た目で、VRChat 実機の見た目ではない。前後の画素差は同じ引数・同じ段階のときだけ意味がある。
- `cct_animator_inspect` は静的解析で、実行時の値ではない。
- 書き込みの後は読み戻す。`restore` は結果の `restored`・`failed` と `RESTORE_VERIFY_FAILED` の有無を見て、続けて同じ id で `diff` を呼び、戻した範囲が変更なしになったか（戻したパスが `changes` に残っていないか）を確かめる。`import` は `files_written` を見て、コンパイルが終わってから `cct_world_gimmicks` の `list` で `imported` と `program_asset` を確かめる。

報告は「呼んだツールと主な引数」「観測した値（段階と時刻）」「変更したもの（スナップショットの id、取り込んだファイル）」「確認していないこと」に分ける。「完成」「完璧」と書かない。

スナップショットは自動では消えない。不要になったら `list` を見せ、ユーザーの同意を得て `delete` をドライラン → `confirm: true` で消す。撮影フォルダーとレポートは Library の中にあり、消すかどうかはユーザーに任せる。

新しく分かった仕様や失敗は [verified-facts](references/verified-facts.md) に「事実 / 出典 / 検証日」で追記し、ステータス行を更新する。Unity MCP の接続や tool 一覧の事実の正は `unity-mcp-operations` の verified-facts にある。
