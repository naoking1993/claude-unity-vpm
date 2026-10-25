# ツールの仕様（Claude Creator Tools 0.1.0）

出典はパッケージ `io.github.naoking1993.claude-creator-tools` 0.1.0 のコード：`Editor/Mcp/<Class>Tool.cs`（ツールの Description と Parameters、要約に残すキー `SummaryKeys`）、`Editor/Tools/<Class>*.cs`（冒頭コメントの issue コード表と `Run()`）、`Editor/Core/`（`CctResult`・`CctRunner`・`CctPaths`・`CctTargets`・`CctArgs`）。2026-10-25 にコードを読んで写した。Unity 実機での動作は未検証（[台帳](verified-facts.md)）。

コードが変わったらこのファイルを直す。ここに無い引数やコードを記憶で足さない。

## 目次

- 0. 共通（呼び方、共通引数、戻り値、レポート、対象の指定、配列の渡し方、メニュー）
- 1. cct_environment
- 2. cct_avatar_audit
- 3. cct_world_audit
- 4. cct_capture_views
- 5. cct_snapshot
- 6. cct_animator_inspect
- 7. cct_dependency_report
- 8. cct_world_gimmicks

---

## 0. 共通

### 0.1 呼び方は2通り

- **単独 tool**：一覧に `cct_avatar_audit` などがあれば、引数オブジェクトを渡して呼ぶ。Claude Code での名前は MCP サーバーの登録名が前に付く。ユーザーの記録ではサーバー名が `UnityMCP` なので `mcp__UnityMCP__cct_avatar_audit` の形と推定する（未実測）。
- **`execute_custom_tool`**：単独 tool が無いときは、MCP for Unity の `execute_custom_tool` に `tool_name` と `parameters` を渡す。この経路はスキーマの検証も既定値の補完もしない（サーバー 9.7.1 の `services/tools/execute_custom_tool.py`）。既定値は C# 側で補うので、省いた引数は既定値になる。ツールの説明文は、MCP のリソース `mcpforunity://custom-tools` の `data.tools[].description` で読める（パッケージの README による。このファイルの方が詳しい）。

この先の例は、各呼び出しを2行で書く。1行目が単独 tool の引数、2行目が `execute_custom_tool` の引数。

```jsonc
// cct_environment
{}
// execute_custom_tool
{"tool_name": "cct_environment", "parameters": {}}
```

### 0.2 共通の引数（8つ全部にある）

| 引数 | 型 | 既定 | 意味 |
| --- | --- | --- | --- |
| `detail` | string | `"summary"` | `"summary"` は要約。`"full"` はレポート全文をそのまま返す（大きいので普段は使わない） |
| `max_issues` | int | 30 | 要約に入れる issue の数（0〜500）。error → warning → info の順に切る |
| `save_report` | bool | true | 全文の JSON を保存して `report_path` を返す |
| `timeout_seconds` | int | 30 | MCP 呼び出しの待ち時間（サーバーの上限は 3600）。サーバーが読む。`cct_world_audit`・`cct_dependency_report`・`cct_animator_inspect` は自分の走査の上限にも使う |

`timeout_seconds` を大きくしても、処理が遅くなるわけではない（上限が上がるだけ）。ただし **240 以下**にする。Claude Code は HTTP の MCP 呼び出しから5分間何も返らないと中断する（ツールの Description と README の記載）。

### 0.3 戻り値の形

成功（`detail: "summary"`）：

```jsonc
{"success": true, "message": "（日本語の1行）",
 "data": {"success": true, "message": "（同じ1行）",
          "issue_counts": {"error": 0, "warning": 2, "info": 5},
          "issues": [{"severity": "warning", "code": "RANK_LIMIT", "message": "（日本語）",
                      "path": "/Avatar/Body", "asset": "Assets/...", "value": 0, "limit": 0}],
          "issues_omitted": 3,
          "（ツールごとの要約キー）": "...",
          "report_path": "C:/.../Library/ClaudeCreatorTools/Reports/cct_avatar_audit-20261025-101500-1a2b.json"}}
```

- `issues` の `path`・`asset`・`value`・`limit` は、そのコードで意味があるときだけ入る。
- 要約に残るキーはツールごとに決まっている（各章の「要約に残るキー」）。どのツールでも、それに加えて `tool`・`tool_version`（`0.1.0`）・`elapsed_ms` と、失敗の補足の `details`・`candidates` は要約に残る（`CctMcpBridge` の AlwaysKeptKeys）。`cct_environment` は全キーを要約に残す。
- 同じコードの issue が多いときは、ツールが1コードあたりの件数を絞る。残りの数の出し方はツールで違う：`cct_avatar_audit`・`cct_animator_inspect` は25件まで載せ、同じコードで「ほかに N 件」の issue を1つ足す（`value` が残りの数）。`cct_world_audit` は20件まで載せ、`ISSUES_TRUNCATED`（info、`value` に `code`・`total`・`omitted`）を足す。`cct_dependency_report` は100件まで載せ、`ISSUES_CAPPED`（info）を足し、コードごとの省いた数を `issues_capped` に入れる。

失敗：`success: false`。日本語の理由は外側の `error` と `data.message` に、失敗コード（`TARGET_NOT_FOUND` など）は `data.code` に入る。外側の `message` は null になる。Unity 側の `ErrorResponse` は外側の `code` にも理由の文字列を入れるが、サーバー 9.7.1 は応答を `{success, message, error, data, hint}` に詰め替えるときにそれを捨てる（`services/custom_tool_service.py` の `_normalize_response`。ユーザーの改修版では未実測）。コードは必ず `data.code` で見る。
対象の候補は `data.candidates`（`cct_avatar_audit`・`cct_animator_inspect`）か `data.details`（`cct_capture_views` は `details.candidates`、`cct_dependency_report` は `details` そのもの）に並ぶ。`cct_world_audit` は `SCENE_NOT_LOADED` のとき `loaded_scenes`、`WORLDS_SDK_MISSING` のとき `avatars_sdk_present`、`cct_world_gimmicks` は `SAMPLES_NOT_FOUND` のとき `package_root` を返す。

どのツールでも起こる失敗（`CctRunner`）：

| コード | 意味 |
| --- | --- |
| `EDITOR_BUSY` | Unity がスクリプトをコンパイル中。終わってから呼ぶ（`cct_snapshot` の restore は Play 中・インポート中・ライトベイク中にも出す） |
| `EXCEPTION` | 予期しない例外。`data.details.stack` に先頭2000字 |
| `NO_RESULT` | ツールが結果を返さなかった |
| `REPORT_WRITE_FAILED`（warning） | レポートを保存できなかった。結果そのものは有効 |

### 0.4 レポートと出力ファイル

| 種類 | 場所 | 中身 |
| --- | --- | --- |
| レポート | `<プロジェクト>/Library/ClaudeCreatorTools/Reports/<ツール名>-<yyyyMMdd-HHmmss-xxxx>.json`（`xxxx` は16進4桁。同じ名前があれば末尾に `-2` などが付く。ワールド監査と依存関係のメニューから呼ぶと `cct_world_audit-world-pc-…`・`cct_dependency_report-deps-…` のように間に識別語が入る） | 全キーと全 issue。UTF-8（BOM なし）の整形 JSON：辞書はキーごとに改行し、数値・文字列だけの配列は1行に並べる。`save_report` が true（既定）なら失敗した呼び出しでも書く |
| 撮影 | `<プロジェクト>/Library/ClaudeCreatorTools/Captures/<id>_<label>/` | `<view>.png`、`contact_sheet.png`、`manifest.json`、比較時は `compare_<view>.png`・`diff_<view>.png` |
| スナップショット | `<プロジェクト>/ClaudeCreatorTools/Snapshots/<id>/` | `manifest.json`、`files/`、`folders/`。Assets の外なのでインポートされない。Library を消しても残る |
| 依存一覧 | Reports と同じフォルダーの `cct_dependency_report-files-<yyyyMMdd-HHmmss-xxxx>.tsv`（メニューからは `cct_dependency_report-deps-files-…`） | `cct_dependency_report` の `dependencies` の全依存（`save_report: false` のときは書かない） |
| 一時コピー | `Assets/__ClaudeCreatorToolsTemp` | `cct_snapshot create` が未保存のシーンを写すときだけ作り、消す |
| 取り込み先 | 既定 `Assets/ClaudeWorldGimmicks` | `cct_world_gimmicks import` |

返るパスはすべて絶対パスで、区切りは `/`。日本語はそのまま残る。

### 0.5 対象の指定（`target`）

`cct_avatar_audit`・`cct_capture_views`・`cct_animator_inspect`・`cct_dependency_report` の `target` は共通の書き方（`CctTargets.Resolve`）：

| 書き方 | 意味 |
| --- | --- |
| `""` または `"selection"` | 選択中の GameObject（`Selection.activeGameObject`。Project で選んだプレハブも入る）。何も選んでいないとき：`cct_avatar_audit`・`cct_animator_inspect` は読み込み中のシーンの唯一のアバターを使う（`TARGET_ADJUSTED`。0体なら `NO_AVATAR`／`NO_TARGET`、複数なら `AMBIGUOUS_TARGET`。`cct_animator_inspect` は Project で選んだ Controller があればそれを使う）。`cct_capture_views` と `cct_dependency_report` の `scene_references` は `TARGET_NOT_FOUND` で失敗する |
| `"#12345"` または `"12345"` | instance ID。ドメインリロードで変わるので、使い回さない |
| `"/Root/Child"`（`/` を含む） | 読み込み中の全シーンの階層パス。同名の兄弟が複数あると失敗して候補を返す |
| `"Assets/x.unity:/Root/Child"` | 読み込み中の特定のシーンの中の階層パス |
| `"Assets/.../X.prefab"` | プレハブアセット |
| それ以外 | 読み込み中のシーンでユニークな GameObject 名 |

### 0.6 配列の引数

- `assets`・`layers`・`only`・`declared_packages` はスキーマ上 string の配列。単独 tool では JSON の配列で渡す。`execute_custom_tool` では配列でも `"a,b"` のようなカンマ区切りの文字列でもよい（`CctArgs.GetStringList`）。
- `views`（`cct_capture_views`）、`export_root`・`search_root`（`cct_dependency_report`）はスキーマ上 string なので、カンマ区切りの文字列で渡す。
- 引数名は snake_case で渡す。camelCase も受け付ける（`CctArgs`）。

### 0.7 Tools メニュー

MCP が使えないときは、ユーザーに Unity の Tools > Claude Creator Tools のメニューを押してもらい、Console の1行とレポートのパスを受け取って、Claude がレポートを読む方法がある。メニューはどれも選択中のものを対象にする。

| メニュー（Tools/Claude Creator Tools/ の下） | 相当する呼び出し |
| --- | --- |
| 環境情報を出力 (cct_environment) | `cct_environment` |
| アバター監査 (cct_avatar_audit)/選択中のアバターを監査 (PC + モバイル) | `cct_avatar_audit`（platform all） |
| アバター監査 (cct_avatar_audit)/選択中のアバターを監査 + NDMF 処理後の値 | 同上 + `post_build: true` |
| ワールド監査 (cct_world_audit)/アクティブシーン (全プラットフォーム) / (PC) / (Android) / (iOS) | `cct_world_audit`（メニューからは timeout_seconds 300 相当まで走査する） |
| キャプチャ (cct_capture_views)/選択中を4方向 / 選択中を顔3方向 / 選択中をシーン照明で4方向 | `cct_capture_views`（既定 / `views: "face"` / `mode: "scene"`） |
| スナップショット (cct_snapshot)/開いているシーンと依存アセットを保存 | `cct_snapshot create`（開いているシーン + 編集可能な依存アセット） |
| スナップショット (cct_snapshot)/選択アセットを保存 / 一覧をログ出力 | `cct_snapshot create`（選択アセットだけ）/ `list` |
| アニメーター解析 (cct_animator_inspect)/選択中のアバターの FX / Gesture / 全レイヤー (+ MA Merge Animator) / Project で選択中のコントローラ | `cct_animator_inspect` |
| 依存関係 (cct_dependency_report)/選択アセットの依存関係 / 選択アセットの使用元を検索 / 選択オブジェクトのシーン外参照 | `cct_dependency_report`（dependencies / reverse / scene_references） |
| ワールドギミック (UdonSharp)/一覧と導入状況をログ出力 | `cct_world_gimmicks list` |
| ワールドギミック (UdonSharp)/Assets/ClaudeWorldGimmicks にインポート | `cct_world_gimmicks import` を **confirm=true** で（ドライランなし。既存ファイルは上書きしない） |
| レポートフォルダを開く / キャプチャフォルダを開く / スナップショットフォルダを開く | — |

restore と delete はメニューに無い（id と確認が要るため）。メニューはダイアログを出さない。

---

## 1. cct_environment

**目的**：作業の前に、接続中のプロジェクトの状態を1回で読む。読み取り専用。

**引数**：`all_packages`（bool、既定 false）＝ VRChat 関連だけでなく登録済みの全パッケージを出す。＋共通引数。

**出力（全キーが要約に残る）**

| キー | 中身 |
| --- | --- |
| `unity_version` | 例 `2022.3.22f1` |
| `project_root` | プロジェクトの絶対パス |
| `platform` | アクティブなビルドターゲット（`StandaloneWindows64`・`Android`・`iOS` など） |
| `color_space` | `Linear` / `Gamma` |
| `play_mode` | `edit` / `playing` / `paused` |
| `is_compiling` | bool。コンパイル中はどのツールも `EDITOR_BUSY` で失敗するので、成功した結果ではふつう false |
| `scenes[]` | `name`・`path`・`is_dirty`・`root_count`・`active` |
| `packages` | `{パッケージ名: 版}`。既定は次の名前だけ：`com.vrchat.base`・`com.vrchat.avatars`・`com.vrchat.worlds`・`com.vrchat.udonsharp`・`com.vrchat.clientsim`・`nadena.dev.modular-avatar`・`nadena.dev.ndmf`・`com.anatawa12.avatar-optimizer`・`jp.lilxyzw.liltoon`・`com.poiyomi.toon`・`com.coplaydev.unity-mcp`・`io.github.naoking1993.claude-creator-tools`・`vrchat.blackstartx.gesture-manager`・`com.vrcfury.vrcfury`・`jp.lilxyzw.lilavatarutils` |
| `project_kind` | `avatar` / `world` / `avatar+world` / `no_vrchat_sdk`（Avatars SDK・Worlds SDK の型が読み込まれているか） |
| `avatar_roots[]` / `world_descriptors[]` | 読み込み中のシーンの VRCAvatarDescriptor / VRC Scene Descriptor：`name`・`path`・`instance_id`・`active_in_hierarchy`・`scene`・`blueprint_id` |
| `selection` | 選択中の GameObject（無ければ null） |
| `output_dirs` | `reports`・`captures`・`snapshots` の絶対パス |
| `tool`・`tool_version`・`elapsed_ms` | ツール名、パッケージ版（`0.1.0`）、所要時間 |

`packages` の版は Package Manager の登録情報で、読み込まれている DLL の中身ではない。UdonSharp は SDK 3.4.0 以降 `com.vrchat.worlds` に含まれるので、`com.vrchat.udonsharp` が無くても U# が無いとは限らない（U# の有無は `cct_world_gimmicks list` の `udonsharp_installed`）。Editor.log のエラー、MCP サーバーの版、MCP for Unity の DLL は読まない。

**issue コード**

| コード | 重大度 | 意味 |
| --- | --- | --- |
| `PLAY_MODE` | warning | Play 中。ここでの変更は Play 終了で消える |
| `SCENE_UNSAVED` | info | 未保存の変更があるシーン（`value` にパス） |
| `PACKAGE_LIST_FAILED` | warning | パッケージ一覧を読めなかった |

**副作用**：レポートを書くだけ。

**呼び出し例**

```jsonc
// cct_environment
{}
{"tool_name": "cct_environment", "parameters": {}}
// 全パッケージを見る
{"all_packages": true}
{"tool_name": "cct_environment", "parameters": {"all_packages": true}}
```

---

## 2. cct_avatar_audit

**目的**：アバター1体の性能ランク（PC と Android/iOS）と、SDK がアップロードを止める問題、構成の注意を1回で調べる。読み取り専用。軽量化の前後、アップロードの前、ランクを決めている項目を探すときに使う。

**引数**

| 引数 | 型 | 既定 | 意味 |
| --- | --- | --- | --- |
| `target` | string | `""` | 監査するアバター（0.5）。選択中のものは、その VRCAvatarDescriptor を持つ親まで上がる（`TARGET_ADJUSTED`）。何も選んでいなければ、読み込み中のシーンで唯一の（アクティブな）アバター。Descriptor の無い対象は部品（衣装・ギミック）として階層を調べる（`DESCRIPTOR_MISSING`） |
| `platform` | string | `"all"` | `all`＝PC と Android の両方（アクティブなビルドターゲットでない側の指摘は info に下げる）、`pc`、`android`、`ios`（android と ios は同じモバイルの表）。Quest は `android`。ほかの値は `INVALID_ARGUMENT` で失敗する |
| `include_lists` | bool | true | 部品ごとの内訳（重いメッシュ・テクスチャ・PhysBone など）をレポートに入れる |
| `top_n` | int | 15 | 内訳の各リストの行数（1〜200） |
| `post_build` | bool | false | 一時クローンを NDMF（MA・AAO など NDMF のプラグイン。VRCFury は含まない）で処理して、処理後も監査する。数十秒かかることがあるので `timeout_seconds: 120` などを付ける。Play 中は使えない。NDMF のパスのログが Console に出る |

**値の出どころ**：Avatars SDK が入っていれば SDK 自身の計算（`AvatarPerformance.CalculatePerformanceStats` をリフレクションで呼ぶ。SDK Control Panel と同じ数値、`method.stats: "sdk"`）。使えないとき（型が無い、例外、Play 中）は、SDK 3.10.5 の数え方を再実装した推定（`method.stats: "estimate"`、全項目に `estimate: true`、`SDK_CALCULATOR_UNAVAILABLE`）。内訳リストはどちらの場合も推定側が作る。

**要約に残るキー**：`avatar`、`method`、`ranks`、`stats`、`upload`、`expressions`、`missing_scripts`、`missing_references`、`notes`、`post_build`（と共通の `candidates` など）。レポートにだけ入るキー：`components`、`breakdown`（内訳）、`rank_tables`（ランクの表）、`expression_parameters`（全パラメーター）、`post_build_stats`、`post_build_breakdown`。

| キー | 中身 |
| --- | --- |
| `avatar` | `name`・`path`・`instance_id`・`scene` か `asset`・`prefab`（`kind`: scene_object / prefab_instance_root / inside_prefab_instance / prefab_asset、元アセット、Variant の元）・`blueprint_id`・`active_build_target`・`transforms`・`components`・`animator`・`descriptor`（レイヤー、視点、リップシンク） |
| `method` | `stats`（sdk / estimate）、`stage`（`pre_build` / `play_mode`）、`active_build_target`、`platforms`、`sdk_version`、`fallback_reason`、`sdk_unrated_stats`、`estimated_stats` |
| `ranks.pc` / `ranks.android`（または `ranks.ios`） | `overall`（Excellent / Good / Medium / Poor / VeryPoor）、`limiting_stats[]`（総合ランクを決めている項目：`stat`・`value`・`rank`・`limit_for_better_rank`＝次のランクに必要な値・`rank_at_limit`・`min_displayed_rank_effect`）、`active_build_target`、`fallback_eligible`、`estimate` |
| `stats.<項目>` | `value`・`unit`（count 以外）・`pc`・`android` のランク・`estimate`・`note`。項目は `triangles`・`bounds_size`・`texture_memory`・`skinned_meshes`・`basic_meshes`・`material_slots`・`physbone_components`・`physbone_transforms`・`physbone_colliders`・`physbone_collision_checks`・`contacts`・`constraints`・`constraint_depth`・`animators`・`bones`・`lights`・`particle_systems`・`total_particles`・`mesh_particle_polys`・`particle_trails_enabled`・`particle_collision_enabled`・`trail_renderers`・`line_renderers`・`raycasts`・`cloths`・`cloth_vertices`・`physics_colliders`・`physics_rigidbodies`・`audio_sources` |
| `upload` | `build_target`・`platform`・`blocked`（アクティブなビルドターゲットで SDK のビルダーが止める error があるか）・`blocking[{code, count}]`・`errors_not_blocking`（MISSING_SCRIPT など止まらない error）・`may_block`・`note`。Descriptor が無ければ `applicable: false` |
| `expressions` | `custom_expressions`・`menu_asset`・`parameters_asset`・`parameters`（`total_bits`／`limit` 256・`count`・`synced`・`local_only`・`by_type`・`items[]`＝`name`・`type`・`saved`・`synced`・`default`、64件まで）・`menu`（メニューの木、各メニューの項目数、深さ）・`modular_avatar`（Menu Installer・Menu Item・Parameters・Merge Animator の数と注記） |
| `missing_scripts` | `total`・`objects`・`items[{path, count}]`（50件まで）・`guids[{guid, file_id, status, components, files, game_objects, script_asset}]`。`status` は script_not_found / class_not_loadable / class_not_in_dll / unknown |
| `missing_references` | 削除済みの物を指す参照（上限あり） |
| `notes` | `stage`・`caveat`・`build_tools`（MA・NDMF・AAO・VRCFury などの有無）・`not_checked`・`ndmf_apply_on_build` |
| `post_build` | `status`（ok / unavailable / failed）・`ndmf_version`・`ndmf.errors[]`・`method`・`ranks`・`changes{項目: {pre, post}}`・`unchanged_stats`・`missing_scripts_after`・`expressions_after`（`total_bits`・`added`・`type_changed`＝Bool が Float になった等）・`cleanup` |

`min_displayed_rank_effect`：見る人の「最低表示ランク」より悪いときに VRChat がすること。`avatar_fallback`（アバター全体がフォールバック。triangles・texture_memory・skinned_meshes・basic_meshes・material_slots・bones）、`physbones_contacts_removed`、`constraints_removed`、`animators_removed`、`lights_removed`、`particle_systems_removed`、`trail_renderers_removed`、`line_renderers_removed`、`raycasts_removed`、`cloth_removed`、`physics_colliders_removed`、`rigidbodies_removed`、`audio_sources_removed`、`none`（bounds_size はブロックしない）。

**issue コード**（重大度が「a/b」のものは条件で変わる。説明は冒頭コメントの要約）

| コード | 重大度 | 意味 |
| --- | --- | --- |
| `DESCRIPTOR_MISSING` | warning | 指定した対象に Descriptor が無い。部品として調べた（ランクは参考値） |
| `TARGET_ADJUSTED` | info | 選択・指定をアバターのルート（親、または子に1体だけあるアバター）へ移した。対象未指定で、読み込み中のシーンの唯一のアバターを選んだときも出る |
| `PLAY_MODE` | info | Play 中。SDK の計算を使わず推定値。NDMF 処理済みの可能性 |
| `SDK_CALCULATOR_UNAVAILABLE` | info | SDK の計算が無いか例外。推定値を使った |
| `SDK_IGNORE_RULE_REPLACED` | info | 別ツールが SDK の除外規則を差し替えている（SDK 値はそれに従う） |
| `CONSTRAINT_GROUPS_NOT_REFRESHED` | info | VRC Constraint のグループを更新できず、深さが多めに出る（SDK と同じ） |
| `RANK_TABLE_MISMATCH` | info | SDK の評価とこのツールの 3.10.5 の表が違う（SDK が正） |
| `BREAKDOWN_MISMATCH` | info | SDK の合計と内訳の合計が違う（その項目の内訳は概算） |
| `TEXTURE_MEMORY_PLATFORM` | info | テクスチャメモリはアクティブなビルドターゲットのインポート結果で計算 |
| `PREBUILD_ESTIMATE` | info | MA・NDMF・AAO・VRCFury がある。値はビルド前 |
| `RANK_LIMIT` | warning/info | VeryPoor（warning）か Poor（モバイル warning、PC info）。もう一方のプラットフォームでは info |
| `MOBILE_DYNAMICS_REMOVED` | warning/info | モバイルで PhysBone・Contact・Constraint が VeryPoor：全部除去される |
| `MOBILE_REMOVED_COMPONENTS` | warning/info | モバイルでは Light・Cloth・Physics・Audio は常に除去 |
| `HARD_LIMIT_EXCEEDED` | error | SDK の上限超え（PhysBone 256、コライダー 256、Contact 256、Constraint 2000、Raycast 80、VRCHeadChop 16） |
| `GLOBAL_COLLIDERS_EXCEEDED` | error | globalCollision のコライダーが4個を超える |
| `PHYSBONE_CHAIN_TOO_LONG` | error | 1つの PhysBone が 256 を超える Transform を動かす（シミュレーションされない） |
| `BOUNDS_LARGE` | warning | Bounds が 5×6×5 m を超える（はみ出す Renderer を列挙） |
| `FALLBACK_INELIGIBLE` | info | フォールバックに使えない（Humanoid かつ全プラットフォーム Good 以上が必要） |
| `MESH_NOT_READABLE` | error | モデルのメッシュの Read/Write が無効 |
| `BLENDSHAPE_NORMALS_LEGACY_OFF` | error | Blendshape Normals = Calculate で Legacy Blendshape Normals が OFF |
| `TEXTURE_MIP_STREAMING_OFF` | error | ミップマップ付きテクスチャの Streaming Mip Maps が OFF |
| `TEXTURE_MAX_SIZE_OVER_8192` | error | Max Size が 8192 を超える |
| `TEXTURE_UNCOMPRESSED` | warning/info | 非圧縮（4 MB 以上なら warning） |
| `TEXTURE_BOX_MIP_FILTER` | info | ミップの Box フィルター（SDK は Kaiser を勧める） |
| `ANDROID_INCOMPATIBLE_SHADER` | error/warning/info | モバイルの許可リストに無いシェーダー（モバイルのターゲットで error、PC ターゲットで platform=android/ios なら warning、all なら info） |
| `MISSING_SCRIPT` | error | Missing Script のある GameObject（子も数え、null で絞る前に数える） |
| `MISSING_SCRIPT_GUID` | info | 保存済みの YAML にある Missing Script の m_Script GUID と、解決できない理由 |
| `MISSING_REFERENCE` | warning | 削除済み・読み込めない物を指す参照 |
| `NOT_WHITELISTED` | error | アクティブなビルドターゲットでアバターに許されないコンポーネント |
| `NOT_WHITELISTED_MOBILE` | warning/info | PC ターゲットのとき：PC では可、Android/iOS では不可 |
| `DYNAMIC_BONE_PRESENT` | error | DynamicBone（PhysBone への変換が要る） |
| `UNITY_CONSTRAINTS` | warning/error/info | Unity の Constraint（PC warning、モバイル error、MA Convert Constraints があれば info） |
| `PIPELINE_MANAGER_COUNT` | error | PipelineManager がちょうど1つでない |
| `PARTICLE_STOP_ACTION_DESTROY` | error | Particle の stopAction = Destroy |
| `PARTICLE_ROOT_STOP_DISABLE` | error | ルートの Particle の stopAction = Disable |
| `TRAIL_AUTODESTRUCT` | error | TrailRenderer の autodestruct |
| `PARTICLE_LIGHTS` | warning | Particle の Lights モジュールに Light（無効でも数える） |
| `AUDIO_DECOMPRESS_ON_LOAD` | error | Decompress On Load で Load In Background が OFF |
| `ROOT_ANIMATOR_MISSING` | error | ルートに Animator が無い |
| `AVATAR_NOT_HUMANOID` | warning | Humanoid でない |
| `AVATAR_INACTIVE` | error | Humanoid のアバターが非アクティブ（プレハブアセットなら warning） |
| `HUMANOID_BONES_MISSING` | error | SDK が必要とするボーンが未割り当て |
| `HUMANOID_OPTIONAL_BONES_MISSING` | info | 任意のボーン（UpperChest・つま先・目・顎・指）が未割り当て |
| `HUMANOID_SPINE_HIERARCHY` | error | 肩・首が UpperChest（か Chest）の子でない |
| `HUMANOID_LIMB_CHILD_ORDER` | warning | 腕・脚の子の順番が SDK の想定と違う |
| `HUMANOID_SPLIT_HIERARCHY` | warning | Hips が両手両足の祖先でない |
| `ARMATURE_NOT_DIRECT_CHILD` | warning | avatarRoot がルートでない（SDK では error。キャッシュ値を読むので warning） |
| `AVATAR_HEIGHT` | error/warning/info | 肩の高さ 0.2 m 未満（error）、5 m 超（warning）、1.0 m 未満・2.5 m 超（info） |
| `FEET_BELOW_ORIGIN` | warning | 左足がルートより下 |
| `VISEME_MESH_MISSING` | error | Viseme Blend Shape なのに顔のメッシュが無い |
| `VIEWPOINT_SUSPICIOUS` | warning | View Position が原点・ルートより下・頭から遠い（目安） |
| `LAYER_CONTROLLER_MISSING` | warning/info | カスタマイズしたレイヤーに Controller が無い（MA Merge Animator があれば info） |
| `GESTURE_MASK_MISSING` | error | Humanoid の Gesture の先頭レイヤーに AvatarMask が無い |
| `WD_MIXED` | warning | 1つの Controller で Write Defaults の ON/OFF が混在（SDK の規則） |
| `WD_MIXED_ACROSS_CONTROLLERS` | info | Controller ごとには揃っているが互いに違う |
| `WD_OFF_EMPTY_MOTION` | warning | WD OFF で Motion が空 |
| `WD_MERGE_ANIMATOR_NOT_CHECKED` | info | MA Merge Animator の Controller は WD の検査に含めていない |
| `BLUEPRINT_ID_EMPTY` | info | Blueprint ID が空（新規アバターとしてアップロードされる） |
| `EXPRESSIONS_ASSET_MISSING` | error/warning | Menu と Parameters の片方だけが解決する（error）、両方未設定だが削除済みの参照がある（warning） |
| `PARAM_BUDGET_EXCEEDED` | error | 同期パラメーターが 256 bit を超える |
| `PARAM_COUNT_EXCEEDED` | warning | パラメーターが 8192 を超える |
| `PARAM_DUPLICATE` | warning | 同じ名前のパラメーターが複数 |
| `PARAM_EMPTY_NAME` | warning/info | 名前の無いパラメーター（同期していれば warning） |
| `PARAM_BUILTIN_NAME` | warning | 組み込みパラメーターと同じ名前 |
| `PARAM_DEFAULT_OUT_OF_RANGE` | info | 既定値が型の範囲外 |
| `MENU_TOO_MANY_CONTROLS` | warning/info | 1メニューに8項目を超える（MA Menu Installer があれば info） |
| `MENU_PARAM_UNDEFINED` | error | メニューのパラメーターが Expression Parameters に無い |
| `MENU_SUBMENU_MISSING` | warning | Sub Menu の参照先が無い |
| `MENU_PUPPET_BOOL_PARAM` | error | Puppet のサブパラメーターが Bool |
| `MENU_PUPPET_SUBPARAM_MISSING` | warning | Puppet のサブパラメーターが足りない |
| `MENU_ICON_INVALID` | error | メニューのアイコンが大きすぎる・非圧縮 |
| `MENU_RECURSIVE` | info | メニューが自分自身に戻る |
| `POST_BUILD_UNAVAILABLE` | info | post_build を頼まれたが NDMF が無い・Play 中・アバターでない |
| `POST_BUILD_FAILED` | warning | NDMF の処理が例外 |
| `POST_BUILD_NDMF_ERROR` | error/warning/info | NDMF のプラグインが出したエラー（Error / InternalError はアップロードを止める） |
| `POST_BUILD_PARTIAL` | info | NDMF だけで処理した（VRCFury など他のビルド処理は動いていない） |
| `POST_BUILD_PARAM_BUDGET_EXCEEDED` | error | 処理後の同期パラメーターが 256 bit を超える（SDK はビルド前のアセットしか見ないので止めない） |
| `NDMF_APPLY_ON_BUILD_OFF` | warning | NDMF の Apply on Build が OFF（本番のアップロードで MA・AAO が動かない） |
| `POST_BUILD_CLEANUP_FAILED` | warning | 一時シーン・クローンを片付けられなかった、または NDMF が一時アセットを書いた（消した） |
| `POST_BUILD_STRAY_OBJECTS` | info | 処理中にユーザーのシーンにできたルートを消した |
| `SCENE_DIRTIED` | warning | 監査中にシーンが変更扱いになり、戻せなかった |
| `SCENE_DIRTY_CLEARED` | info | 一時的に変更扱いになったシーンの印を戻した |
| `AUDIT_SECTION_FAILED` | warning | 1つの節が例外で抜けた。残りは有効 |

失敗コード：`TARGET_NOT_FOUND`、`AMBIGUOUS_TARGET`（アバターや同名の物が複数）、`NO_AVATAR`（何も選んでおらず、シーンにアバターが無い）、`INVALID_ARGUMENT`（platform が all・pc・android・ios のどれでもない）。候補は `data.candidates`（パスと instance ID）。

**副作用**：シーンとアセットは変えない。プレハブアセットは読み取り専用で一時的に読み込んで解放する。`post_build` は非公開のプレビューシーンにクローンを作って NDMF で処理し、生成物ごと破棄する（生成アセットは保存しない）。NDMF のログが Console に出る。

**呼び出し例**

```jsonc
// 選択中のアバターを PC とモバイルで
{}
{"tool_name": "cct_avatar_audit", "parameters": {}}
// パスで指定、Quest 版を作るので android の指摘を warning のまま見る
{"target": "/Avatar", "platform": "android"}
{"tool_name": "cct_avatar_audit", "parameters": {"target": "/Avatar", "platform": "android"}}
// NDMF 処理後の値も
{"target": "/Avatar", "post_build": true, "timeout_seconds": 120}
{"tool_name": "cct_avatar_audit", "parameters": {"target": "/Avatar", "post_build": true, "timeout_seconds": 120}}
```

---

## 3. cct_world_audit

**目的**：ワールドのシーンをアップロードの前に点検する（Worlds SDK 3.10.5、Unity 2022.3.22f1）。SDK のビルド検証を同じ条件で再実装した項目と、公式ドキュメントの推奨、コミュニティ由来の項目（印付き）を含む。ビルド・アップロード・ClientSim はしない。読み取り専用。Build & Test・アップロードの前、大きな変更の後、ビルドできない・VRChat で挙動が違うときに使う。

**引数**

| 引数 | 型 | 既定 | 意味 |
| --- | --- | --- | --- |
| `scene` | string | `"active"` | `active`（SDK がアップロードするシーン）、`all`（読み込み中の全シーン）、読み込み中のシーンのパスか名前。記述子・PipelineManager・ObjectSync・PhysBone の止まる条件は常に全シーンで判定する |
| `platform` | string | `"all"` | `all`、`pc`（PC だけのワールド。Android/iOS の検査を省く）、`android`、`ios`（モバイルは同じ制限） |
| `top_n` | int | 15 | 各節のリストの長さ（1〜500） |
| `check_references` | bool | true | 削除済みの物への参照を探す |
| `max_reference_scan` | int | 5000 | 参照を探すコンポーネントの上限。経過時間が `timeout_seconds - 10` 秒になっても止まる |
| `check_editor_log` | bool | true | Editor.log の末尾から `error CS` と UdonSharp のエラーを拾う |

大きいワールドでは `timeout_seconds: 120` などを渡す。UdonSharp のプログラムアセットの走査は `(timeout_seconds - 10) / 4` 秒で止まる。

**要約に残るキー**：`verdict`、`scenes`、`platforms`、`counts`、`project`、`descriptor`、`layers`、`lighting`、`rendering`、`mirrors`、`udon`、`gimmicks`、`android`、`not_checked`、`notes`。レポートにだけ入る：`interactables`、`colliders`、`ui`、`components`、`hotspots`、`editor_log`。

| キー | 中身 |
| --- | --- |
| `verdict` | `sdk_build_blocked`（bool）、`build_blockers[{code, count}]`（SDK と UdonSharp が実際に止める error だけ）、`upload_blockers`（直近の Android/iOS ビルドが 100 MB 超）、`other_errors`（止まらないが VRChat で壊れる error）、`note` |
| `platforms` | `requested`・`checked`・`active_build_target`・`active_target_is_mobile` |
| `counts` | GameObject・コンポーネント・Renderer・三角形（全体とアクティブ）・Material・Texture・Light・Camera・ミラー・UdonBehaviour・Pickup・Station・ObjectSync・動画プレイヤー・AudioSource・PhysBone・Contact |
| `descriptor` | スポーン、Respawn の高さ、Reference Camera、Blueprint ID |
| `gimmicks` | VRChat の動画プレイヤー、U# のクラス（名前空間ごと）、AudioLink・LTCGI・ProTV・VideoTXL・VizVid などの名前からの推定 |
| `not_checked` | 確かめていないこと（実ビルド、U# のコンパイル結果そのもの、ClientSim と実機、サーバー側の設定、アカウントの条件、クライアントが実際に除去するコンポーネント、フレームレート） |

`build_blockers` に数えるコード：`NO_SCENE_DESCRIPTOR`・`MULTIPLE_SCENE_DESCRIPTORS`・`DESCRIPTOR_NOT_IN_UPLOAD_SCENE`・`PIPELINE_MANAGER_MISSING`・`MULTIPLE_PIPELINE_MANAGERS`・`PREFAB_STAGE_SDK_COMPONENTS`・`NO_SPAWNS`・`LAYERS_NOT_SETUP`・`COLLISION_MATRIX_NOT_SETUP`・`SDK2_EVENT_HANDLER`・`SDK2_COMPONENT`・`OBJECT_SYNC_MANUAL_UDON`・`OBJECT_SYNC_WITH_POOL`・`ANDROID_TEXTURE_FORMAT_NOT_ASTC`・`SCRIPT_COMPILATION_FAILED`・`USHARP_PROGRAM_NO_SOURCE`・`USHARP_SCRIPT_NOT_IN_USHARP_ASSEMBLY`・`USHARP_DUPLICATE_PROGRAM_ASSET`（いずれも error のとき）。`upload_blockers`：`BUNDLE_TOO_LARGE_MOBILE`。

**issue コード**（出どころ：SDK＝SDK が同じ判定をする、DOC＝公式ドキュメント、COM＝コミュニティ、EST＝このツールの推定、UB＝UdonBehaviour/UdonSharp のソース、WV＝SDK の許可リスト）

| コード | 重大度 | 出どころ | 意味 |
| --- | --- | --- | --- |
| `PLAY_MODE` | warning | — | Play 中。ClientSim などが作った物も数える |
| `MULTIPLE_SCENES_LOADED` | warning | SDK | 複数シーン。SDK は全部を数え、アクティブだけを上げる |
| `SCENE_UNSAVED` / `SCENE_DIRTIED` | info / warning | SDK / — | 未保存のシーン / 監査中に変更扱いになった（起きないはず） |
| `SECTION_FAILED` | warning | — | 1つの節が例外。その節のデータは不完全 |
| `UNITY_VERSION_MISMATCH` | warning | SDK | Unity が 2022.3.22f1 でない |
| `SCRIPT_COMPILATION_FAILED` | error | EST | スクリプトのコンパイル失敗の印が立っている |
| `ANDROID_TEXTURE_FORMAT_NOT_ASTC` | error | SDK | Android ターゲットで ASTC でない |
| `LIGHTING_AUTO_GENERATE` | warning | SDK | Lighting の Auto Generate が ON |
| `GRAVITY_NOT_STRAIGHT_DOWN` / `GRAVITY_UPWARD` / `GRAVITY_ZERO` | warning | SDK | 重力の向き・0 |
| `FOG_STRIPPING_CUSTOM` | warning | SDK | Fog の Stripping が Custom |
| `COLOR_SPACE_GAMMA` | info | SDK | Gamma（ビルド時に Linear へ切り替わる） |
| `BUNDLE_TOO_LARGE_MOBILE` | error | SDK | 直近の Android/iOS ビルドが 104,857,600 B 超（アップロード拒否） |
| `BUNDLE_LARGE_PC` | info | DOC | 直近の PC ビルドが 200 MB 超 |
| `AVATAR_PACKAGES_IN_WORLD_PROJECT` | info | COM | ワールドプロジェクトにアバター用パッケージ |
| `SDK2_EVENT_HANDLER` / `SDK2_COMPONENT` | error | SDK | VRC_EventHandler / VRCSDK2 のコンポーネント |
| `EDITOR_LOG_CS_ERRORS` | info | EST | Editor.log 末尾の `error CS`（直っている可能性もある） |
| `EDITOR_LOG_USHARP_ERRORS` | warning | EST | Editor.log 末尾の UdonSharp のエラー |
| `EDITOR_LOG_UNAVAILABLE` | info | — | Editor.log を読めなかった |
| `NO_SCENE_DESCRIPTOR` / `MULTIPLE_SCENE_DESCRIPTORS` | error | SDK | 記述子が0 / 2つ以上 |
| `DESCRIPTOR_NOT_IN_UPLOAD_SCENE` | error | SDK | 記述子がアクティブなシーンに無い |
| `DESCRIPTOR_EDITOR_ONLY` | error | EST | 記述子が EditorOnly タグ（ビルドで消える） |
| `PIPELINE_MANAGER_MISSING` / `MULTIPLE_PIPELINE_MANAGERS` | error | SDK | PipelineManager が0 / 2つ以上 |
| `PIPELINE_MANAGER_NOT_ON_DESCRIPTOR` | warning | SDK | PipelineManager が記述子と別の GameObject |
| `PREFAB_STAGE_SDK_COMPONENTS` | error/warning | SDK | 開いている Prefab Mode に記述子・PipelineManager がある（合計が2以上なら error） |
| `NO_SPAWNS` | error | SDK | 有効なスポーンが無い |
| `SPAWN_NULL` | warning | EST | スポーンに空の項目 |
| `SPAWN_TOO_FAR` | warning | SDK | スポーンの座標が 1000 以上 |
| `RESPAWN_HEIGHT_ABOVE_SPAWN` | warning | SDK | スポーンが Respawn Height より下 |
| `SPAWN_NO_GROUND` | info | EST | スポーンの下にプレイヤーが立てる床が見つからない |
| `SPAWN_RADIUS_ZERO` | info | DOC | spawnRadius 0（同時に来た人が重なる） |
| `AUTO_SPATIALIZE_DEPRECATED` | warning | SDK | autoSpatializeAudioSources が ON |
| `REFERENCE_CAMERA_MISSING` | info | DOC | Reference Camera が無い |
| `REFERENCE_CAMERA_NO_CAMERA` | warning | COM | Reference Camera のオブジェクトに Camera が無い |
| `REFERENCE_CAMERA_NEAR_CLAMPED` | info | EST | near が 0.01〜0.05 の外 |
| `REFERENCE_CAMERA_OBJECT_DISABLED` | warning | SDK/EST | Reference Camera のオブジェクトに付いた他のコンポーネント（開始時に無効になる） |
| `REFERENCE_CAMERA_TAA` | warning | SDK | PostProcessLayer が TAA |
| `LAYERS_NOT_SETUP` / `COLLISION_MATRIX_NOT_SETUP` | error | SDK | レイヤー 3〜21 の名前 / 衝突マトリクス 0〜21 が VRChat の設定と違う |
| `LAYER_SDK_CHECK_UNAVAILABLE` | info | — | SDK の比較を呼べず、自前で比較した |
| `RESERVED_LAYER_USED` | warning | DOC | 予約レイヤーに物がある |
| `MIRROR_REFLECTION_LAYER_RENDERER` | info | DOC | MirrorReflection レイヤーの Renderer（ミラーにだけ映る） |
| `LIGHTMAP_NOT_BAKED` / `LIGHTMAP_OUTDATED` / `LIGHTMAP_UV_MISSING` | warning / info / info | DOC / EST / COM | 未ベイク / 一部にライトマップが無い / UV2 が無い |
| `REALTIME_SHADOW_LIGHTS` | warning | DOC/EST | 影付きのリアルタイムライトが2つ以上 |
| `BAKED_LIGHT_NOT_BAKED` | warning | COM | Baked/Mixed のライトが焼かれていない |
| `NO_LIGHT_PROBES` | info | COM | ベイクしたのにライトプローブが無い |
| `REFLECTION_PROBE_NOT_BAKED` / `REFLECTION_PROBE_EVERY_FRAME` | info / warning | COM / EST | 反射プローブ未ベイク / 毎フレーム更新 |
| `OCCLUSION_NOT_BAKED` | info/warning | DOC | オクルージョン未ベイク（モバイルで warning） |
| `MISSING_SHADER` | warning | EST | エラーシェーダー（ピンク） |
| `MATERIAL_SLOT_EMPTY` | info | EST | 空のマテリアルスロット |
| `LARGE_TEXTURE` | warning | SDK | Max Size が 8192 超 |
| `TEXTURE_MIP_BOX_FILTER` | info | SDK | ミップの Box フィルター |
| `SUBSTANCE_MATERIAL` | warning | SDK | .sbsar のマテリアル |
| `PARTICLE_BILLBOARD_ROLL` | info | SDK | Billboard の Particle で allowRoll |
| `TMP_RECOMMENDED` | info | SDK | UI.Text・TextMesh があり TextMeshPro が無い |
| `UI_DEFAULT_SHADER` | info | SDK | UI/Default のまま |
| `EXTRA_ACTIVE_CAMERA` / `CAMERA_RENDER_TEXTURE_ACTIVE` | warning / info | COM/EST / COM | 画面に描く余分なカメラ / 毎フレーム RenderTexture に描くカメラ |
| `PP_VOLUME_ON_MAIN_CAMERA` | warning | SDK | Camera.main の下に PostProcessVolume |
| `PP_LAYER_MISSING` / `PP_VOLUME_LAYER_EMPTY` / `PP_VOLUME_NOT_IN_LAYER` / `PP_HEAVY_EFFECT` | info | COM/EST/DOC | ポストプロセスの設定（Layer が無い / volumeLayer が Nothing / Volume が対象レイヤー外 / 重い効果） |
| `MIRROR_ENABLED_AT_START` / `MIRROR_MULTIPLE_ENABLED` | warning | DOC | 開始時にミラーが ON / 2枚以上 ON |
| `MIRROR_NO_RENDERER` | warning | SDK | ミラーに Renderer・Material が無い |
| `MIRROR_REFLECT_EVERYTHING` / `MIRROR_REFLECT_UIMENU` / `MIRROR_NO_LOCAL_PLAYER` | info | DOC/COM | Reflect Layers の設定 |
| `MIRROR_LAYER_NOT_RENDERED` | warning | DOC | ミラー自体が MirrorReflection レイヤー |
| `UDON_NO_PROGRAM` / `UDON_PROGRAM_MISSING_REFERENCE` | error | UB | プログラムが無い / 削除済みのプログラムを指す |
| `UDON_PROGRAM_SOURCE_MISSING` / `UDON_PROGRAM_NOT_COMPILED` | warning | UB | ソースが無く古い版が動く / コンパイル済みのものが無い |
| `UDON_SYNC_CONFLICT` | warning | UB | 1つの GameObject に Manual と Continuous |
| `UDON_BUILTIN_MESH_REFERENCE` | warning | SDK | Udon の公開変数が組み込みの Mesh を指す |
| `OBJECT_SYNC_MANUAL_UDON` / `OBJECT_SYNC_WITH_POOL` | error | SDK | VRCObjectSync と Manual の Udon / ObjectPool が同じ物に |
| `USHARP_PROXY_NO_BACKING` | warning | UB | U# の裏の UdonBehaviour が無い |
| `USHARP_PROGRAM_NO_SOURCE` | error | UB | U# のプログラムアセットにソースが無い |
| `USHARP_SCRIPT_NOT_IN_USHARP_ASSEMBLY` | error | UB | U# Assembly Definition の無い asmdef の中の U#（U# のコンパイルが止まる） |
| `USHARP_SCRIPT_IN_NON_USHARP_ASSEMBLY` | info | UB | 同上だがプログラムアセットが無い（止まらない） |
| `USHARP_ASSEMBLY_DEFINITION_NO_SOURCE` | warning | UB | U# Assembly Definition に元の asmdef が無い |
| `USHARP_DUPLICATE_PROGRAM_ASSET` | error | UB | 1つのスクリプトを2つ以上のプログラムアセットが指す |
| `USHARP_SCAN_TRUNCATED` | info | — | U# の走査が時間で止まった |
| `PICKUP_NO_RIGIDBODY` / `PICKUP_NO_COLLIDER` | error / warning | SDK | Pickup に Rigidbody / Collider が無い |
| `PICKUP_NOT_SYNCED` / `PICKUP_COLLIDES_WITH_PLAYER` | info | DOC | 同期していない / プレイヤーとぶつかるレイヤー |
| `STATION_NO_COLLIDER` / `STATION_NO_INTERACT_UDON` | info | DOC | Station に Collider / Udon が無い |
| `MESH_COLLIDER_NONCONVEX_RIGIDBODY` | warning | EST | 動く Rigidbody の下に凹型の MeshCollider |
| `OBJECT_POOL_EMPTY_ENTRY` | warning | EST | ObjectPool に空の項目 |
| `PORTAL_NOT_AT_ROOT` / `PORTAL_NO_DESTINATION` | warning | DOC | ポータルがルートに無い / 行き先が無い |
| `PEDESTAL_NO_BLUEPRINT` | warning | DOC | ペデスタルに blueprintId が無い |
| `VIDEO_PLAYER_TOO_MANY` / `VIDEO_PLAYER_AUTOPLAY_MULTIPLE` | warning | DOC | 動画プレイヤーが3台以上 / 2台以上が自動再生 |
| `VIDEO_PLAYER_NO_OUTPUT` / `VIDEO_PLAYER_NO_AUDIO` | warning / info | EST | 出力先 / 音の出口が無い |
| `VIDEO_PLAYER_URL_NOT_ALLOWLISTED` / `VIDEO_PLAYER_HTTP_MOBILE` | info / warning | DOC | 既定 URL が許可リスト外 / http:// |
| `AUDIO_NO_SPATIAL` / `AUDIO_ONSP_DEPRECATED` | warning | SDK | VRC Spatial Audio Source が無い / ONSPAudioSource |
| `PHYSBONE_NO_PARENT` / `PHYSBONE_SHAPES_OVER_LIMIT` / `CONTACTS_OVER_LIMIT` | warning | SDK | PhysBone のルートに親が無い / PhysBone＋コライダー 1024 超 / Contact 1024 超 |
| `UI_SHAPE_NO_CANVAS` / `UI_SHAPE_ON_UI_LAYER` / `UI_SHAPE_NOT_WORLD_SPACE` / `UI_SHAPE_NO_RAYCASTER` / `UI_NO_EVENT_SYSTEM` | warning | DOC | VRC Ui Shape の設定 |
| `UI_CANVAS_NO_UI_SHAPE` / `UI_NAVIGATION_NOT_NONE` / `UI_SCROLL_SENSITIVITY` | info | DOC | 操作できる Canvas に Ui Shape が無い / Navigation が None でない / スクロール感度 |
| `MISSING_SCRIPT` | error | EST | Missing Script |
| `MISSING_REFERENCE` / `MISSING_PREFAB` | warning | EST | 削除済みの物への参照 / 元のプレハブが無い |
| `NOT_WHITELISTED` | warning（ProBuilder は info） | WV | SDK3 ワールドの許可リストに無いコンポーネント（SDK は検査せず、クライアントが除去すると推定） |
| `NOT_WHITELISTED_DERIVED` | warning | WV/EST | 許可された型を継承したプロジェクト内の型 |
| `EDITOR_ONLY_COMPONENTS` | info | WV | IEditorOnly のコンポーネント |
| `REFERENCE_SCAN_TRUNCATED` / `SERIALIZE_FAILED` / `SDK_FIELD_NOT_FOUND` / `ISSUES_TRUNCATED` | info | — | 走査の打ち切り / 読めないコンポーネント / SDK の版違いでフィールドが無い（その検査は省く） / 1コード20件を超えた |
| `ANDROID_SHADER_NOT_SUPPORTED` | warning（一部 info） | SDK | モバイルの許可リストに無いシェーダー |
| `ANDROID_POST_PROCESSING` / `ANDROID_REALTIME_LIGHT` / `ANDROID_TRIANGLE_BUDGET` / `ANDROID_UNSUPPORTED_COMPONENT` | warning | DOC | モバイル：ポストプロセス無効 / リアルタイムライト / 25万三角形超 / Cloth・Dynamic Bone |
| `ANDROID_LARGE_TEXTURE` / `ANDROID_TRANSPARENT_MATERIALS` / `ANDROID_GPU_INSTANCING_OFF` | info | DOC | モバイル：1024 px 超 / 透過・カットアウト / GPU Instancing OFF |

`ANDROID_*` はワールドを Quest・モバイルにも上げる場合だけ関係がある。PC だけなら `platform: "pc"` で省く。

失敗コード：`WORLDS_SDK_MISSING`（Worlds SDK が無い。アバタープロジェクト。`avatars_sdk_present` が付く）、`INVALID_ARGUMENT`（platform の綴り違いなど。Quest は `android`）、`SCENE_NOT_LOADED`（`loaded_scenes` に読み込み中のシーン）、`NO_LOADED_SCENE`。

**副作用**：なし（レポートだけ）。

**呼び出し例**

```jsonc
// アクティブなシーンを PC だけで
{"platform": "pc"}
{"tool_name": "cct_world_audit", "parameters": {"platform": "pc"}}
// 大きいワールドを全プラットフォームで
{"platform": "all", "timeout_seconds": 120}
{"tool_name": "cct_world_audit", "parameters": {"platform": "all", "timeout_seconds": 120}}
```

---

## 4. cct_capture_views

**目的**：対象の前方（対象の +Z。アバターの一部なら、そのアバターのルートの前方）を基準に、決まった角度から PNG を描く。前後で同じカメラ・ポーズ・照明にそろえて見た目を比べる。材質調整・軽量化・衣装や BlendShape の編集の前後に使う。

**引数**（`compare_with` があると、省いた引数は前回の撮影から引き継ぐ）

| 引数 | 型 | 既定 | 意味 |
| --- | --- | --- | --- |
| `target` | string | `""`（選択中） | 撮る GameObject（0.5）。何も選んでいなければ `TARGET_NOT_FOUND`（アバターを自動では選ばない） |
| `views` | string | `"front,back,left,right"` | カンマ区切り。front、back、left、right、front_left、front_right、back_left、back_right、top、face_front、face_left45、face_right45（left/right は対象自身の左右、face_* は常に頭部の構図）。プリセット `turnaround`＝front,front_right,right,back,left,front_left、`face`＝face_front,face_left45,face_right45 |
| `resolution` | int | 768 | 正方形の一辺（256〜2048） |
| `mode` | string | `"isolated"` | `isolated`：スクリプトを外したクローンを非表示のプレビューシーンで中立の照明（キー＋フィル＋環境光）で描く。開いているシーンのライト・スカイボックス・他の物・ライトマップ・ポストプロセスは写らない。NDMF/MA のプレビューも写らない（編集時の階層）。`scene`：開いているシーンをそのまま一時カメラで描く |
| `background` | string | `"solid"` | `solid`（`background_color`）、`transparent`（アルファ付き PNG、1ビュー2回描く）、`skybox`（scene のみ） |
| `background_color` | string | `"#808080"` | sRGB の #RRGGBB |
| `fov` | float | 25 | 縦の画角（5〜90度） |
| `padding` | float | 0.08 | 片側の余白（0〜0.4） |
| `framing` | string | `"auto"` | face 以外のビューの構図。`auto`（全身。Humanoid の骨格から遠い Renderer は構図から外す）、`upper`（腰から上）、`face`（頭） |
| `contact_sheet` | bool | true | 全ビューを1枚に並べた `contact_sheet.png` も書く |
| `label` | string | 対象の名前 | 出力フォルダー名に付ける文字（日本語可） |
| `compare_with` | string | `""` | 前回の撮影のフォルダー、その `manifest.json`、Captures の下のフォルダー名、または `latest`（同じ対象の最新。無ければ全体の最新で `COMPARE_TARGET_DIFFERS`） |
| `reuse_cameras` | bool | true | 比較時、構図の範囲が変わっても（メッシュを消した等）前回のカメラ位置を使って画素をそろえる |
| `light_follow_camera` | bool | false | isolated：ライトを各カメラに合わせて向ける（false だと後ろからのビューはフィルと環境光だけ） |
| `keep_scripts` | bool | false | isolated：クローンの MonoBehaviour を残す（[ExecuteAlways] が動きうる。Play 中は全部の Awake/OnEnable が動く）。VRChat の PhysBone・Contact・Constraint は常に外す |

比較の条件：`resolution`・`mode`・`background`・`background_color`・`fov`・`padding`・`framing`・`light_follow_camera`・`keep_scripts` を前回と違う値で明示すると `COMPARE_MISMATCH` で比較しない（新しい撮影は書く）。前後を比べるときは、これらを渡さずに引き継がせる。

時間：1ビューはふつう1秒未満。1024 を超える解像度で多くのビュー、`transparent`、`compare_with`、Editor 起動後の最初の撮影（シェーダーを同期コンパイルする）は 30 秒を超えうるので `timeout_seconds: 120` を渡す。打ち切られても Unity が最後まで撮ることがあるので、再送の前に Captures の最新フォルダーを確かめる。

**要約に残るキー**：`capture_dir`、`contact_sheet`、`manifest`、`views`、`compare_with`、`comparisons`、`comparison_units`、`target`、`params`、`details`。レポートにだけ：`files`、`framing`、`render`。

| キー | 中身 |
| --- | --- |
| `capture_dir` / `contact_sheet` / `manifest` | 出力フォルダー、一覧画像、manifest の絶対パス |
| `views[]` | `view`・`file`（PNG の絶対パス）・`framing`・`camera_source`・`camera`（`position`・`euler`・`fov`・`distance`） |
| `params` | 実際に使った引数（引き継いだ値を含む） |
| `comparisons[]` | ビューごと：`view`、`changed_pct_foreground`・`mean_abs_diff_foreground`（背景色から2階調以内の画素を両方の画像で除いた値。こちらを優先。`background: "skybox"` では null）、`foreground_pct`、`changed_pct`・`mean_abs_diff`（画面全体）、`changed_pixels`、`max_diff`、`changed_bbox`（[x0, y0, x1, y1]、画素単位・左上原点・x1 と y1 は含まない。変化が無ければ null）、`compare_image`（左が前・右が後）、`diff_image`（後の画像を暗くし、変化を赤で示す）、`previous_file` |
| `comparison_units` | 8 bit sRGB の階調（0〜255）で、知覚的な色差（ΔE）ではない。「変化」はどれかのチャンネルが 8 を超えて違う画素 |
| `manifest.json` | 全引数、各ビューのカメラ（ワールドと対象基準の両方）、Unity の版、色空間、グラフィックス API。次の `compare_with` が読む |

**issue コード**

| コード | 重大度 | 意味 |
| --- | --- | --- |
| `TARGET_NOT_FOUND` | error（失敗） | 対象が見つからない |
| `AMBIGUOUS_TARGET` | error（失敗） | 対象の指定に複数が当てはまる。`details.candidates` に候補 |
| `INVALID_ARGUMENT` | error（失敗） | 有効なビューが1つも無い |
| `UNKNOWN_VIEW` | warning | 知らないビュー名・プリセット名を無視した |
| `PARAM_ADJUSTED` | info/warning | 引数を範囲に丸めた・既定値にした |
| `MODE_FALLBACK` | warning | scene を使えない（プレハブアセット・Prefab Mode）か、isolated で skybox → 変えた |
| `TARGET_INACTIVE` | warning | 対象が非アクティブ。isolated はクローンを有効にして撮る（scene は失敗） |
| `NO_RENDERERS` | error（失敗） | 有効な Mesh/SkinnedMeshRenderer が無い |
| `NO_HUMANOID_HEAD` | warning | Humanoid の頭が無く、face/upper を範囲の割合で決めた |
| `FRAMING_OUTLIERS_EXCLUDED` | info | 骨格から遠い Renderer を構図から外した（描画はする） |
| `SCRIPTS_STRIPPED` | info | クローンからスクリプトを外した（PhysBone・Constraint・MA は動かない） |
| `SCRIPTS_KEPT` / `STRIP_INCOMPLETE` | warning | keep_scripts でスクリプトが動いた可能性 / 外せなかったスクリプトがある |
| `BAKE_FAILED` | warning | スキンメッシュの現在の形を焼けなかった（Play 中で Read/Write 無効など） |
| `TARGET_HAS_LIGHTS` | info | 対象の Light も撮影を照らした |
| `EDITOR_ONLY_VISIBLE` | info | EditorOnly の下の Renderer が写っている（アップロードで消える） |
| `LIGHTING_OVERRIDE_FAILED` | warning | プレビューの照明に切り替えられず、開いているシーンの環境光が混じったかもしれない |
| `PLAY_MODE` | info | Play 中に撮った（実行時の状態） |
| `SRP_ACTIVE` | warning | SRP が有効（このツールは Built-in 用） |
| `TEXTURE_STREAMING` | warning | ミップストリーミングで一部のテクスチャが粗いまま |
| `STATIC_BATCHED` | warning | 静的バッチ済みの Renderer（Play 中）。scene か編集モードで撮る |
| `MSAA_UNAVAILABLE` | info | MSAA が使えず縁がギザギザ |
| `RENDER_FAILED` / `WRITE_FAILED` | error | 描画 / 書き込みの失敗（1枚も撮れなければ失敗） |
| `CLEANUP_FAILED` | warning | 一時物を片付けられなかった |
| `SCENE_DIRTIED` / `ASSET_DIRTIED` | warning | 撮影中にシーン / プレハブアセットが変更扱いになった（起きないはず） |
| `COMPARE_NOT_FOUND` | error | compare_with の manifest を読めない。比較しない |
| `COMPARE_MISMATCH` | error | 比較の条件が違う。比較しない |
| `COMPARE_MISSING_VIEW` | warning | 片方にしか無いビュー |
| `COMPARE_TARGET_DIFFERS` | info/warning | 前回は別の物を撮っていた（latest が別の物に落ちたら warning） |
| `COMPARE_FILE_MISSING` | warning | 前回の PNG が無い・読めない・大きさが違う |
| `COMPARE_CAMERA_REUSED` | info | 構図が変わったので前回のカメラを使った（はみ出す部分は写らない） |
| `COMPARE_CAMERA_DIFFERS` | warning | reuse_cameras=false でカメラが動いた。差に構図のずれが入る |

知っておく限界（冒頭コメント）：`_Time` で動くシェーダー（lilToon のスクロール・まばたき等）は撮るたびに違う。ポストプロセスは無い。シーンのスクリプトが設定したグローバルなシェーダー値（VRC Light Volumes、AudioLink 等）は isolated のクローンにも届く。scene モードはカメラと対象の間にある物も写す。

**副作用**：`Library/ClaudeCreatorTools/Captures/<id>_<label>/` にファイルを書くだけ。シーンとアセットは変えない。一時物は HideAndDontSave で作って finally で消す。

**呼び出し例**

```jsonc
// 作業前：選択中を4方向
{"label": "作業前"}
{"tool_name": "cct_capture_views", "parameters": {"label": "作業前"}}
// 作業後：作業前のフォルダーと比べる（引数は引き継ぐ）
{"compare_with": "C:/.../Library/ClaudeCreatorTools/Captures/20261025-101500-1a2b_作業前", "label": "作業後", "timeout_seconds": 120}
{"tool_name": "cct_capture_views", "parameters": {"compare_with": "C:/.../Library/ClaudeCreatorTools/Captures/20261025-101500-1a2b_作業前", "label": "作業後", "timeout_seconds": 120}}
// 顔3方向
{"target": "/Avatar", "views": "face"}
{"tool_name": "cct_capture_views", "parameters": {"target": "/Avatar", "views": "face"}}
// 6方向・腰から上
{"target": "/Avatar", "views": "turnaround", "framing": "upper"}
{"tool_name": "cct_capture_views", "parameters": {"target": "/Avatar", "views": "turnaround", "framing": "upper"}}
// シーンの照明で（ワールドのライティング）
{"target": "/Room", "mode": "scene", "views": "front,front_left,front_right"}
{"tool_name": "cct_capture_views", "parameters": {"target": "/Room", "mode": "scene", "views": "front,front_left,front_right"}}
```

---

## 5. cct_snapshot

**目的**：危ない編集の前に、ファイルそのもの（.meta も。GUID とインポート設定が残る）をバイト単位で退避し、後で差分を見て戻せるようにする。MCP の削除はエディタの Undo に載らず、シーンのコピーだけでは後から変えた共有の Material・Mesh・AnimationClip を戻せないため。

流れ：`create` → 編集 → `diff`（何が変わったか）→ `restore`（ドライラン）→ ユーザーに見せる → `restore` を `confirm: true`。

**引数**

| 引数 | 型 | 既定 | 使う action | 意味 |
| --- | --- | --- | --- | --- |
| `action` | string | （必須） | — | `create`・`list`・`diff`・`restore`・`delete` |
| `label` | string | `""` | create | manifest に残す説明（日本語可）。id には ASCII の部分だけが入る |
| `assets` | string[] | — | create / restore | create：`Assets/` の下のファイル・フォルダー（フォルダーは中身ごと）。restore：戻す範囲（フォルダーを指定すると下の全部。省くと全部） |
| `include_open_scenes` | bool | true | create | 読み込み中のファイルのあるシーンも退避する（未保存の変更はメモリから写す） |
| `include_dependencies` | bool | false | create | 依存（`AssetDatabase.GetDependencies` の再帰と未保存シーンの参照）も `Assets/` の下から退避する |
| `dependency_filter` | string | `"editable"` | create | `editable`＝.unity .prefab .mat .anim .controller .overrideController .mask .asset .playable .physicMaterial .renderTexture .flare .signal .lighting だけ、`all`＝テクスチャ・モデル・スクリプト・音も |
| `max_total_mb` | double | 500 | create | 見積もりがこれを超えたらコピーせずに中止（`SIZE_LIMIT`、`details.largest` に大きいファイル） |
| `id` | string | — | diff / restore / delete | create・list が返す id（restore の取り消しには `pre_restore_snapshot_id`） |
| `confirm` | bool | false | restore / delete | true のときだけ実際に変える。無ければドライラン（`CONFIRM_REQUIRED`） |
| `limit` | int | 50 | list | 返す件数（新しい順、1〜1000） |
| `max_entries` | int | 50 | create / diff / restore | 返すリストの各上限（1〜5000）。件数は全体を数え、超えた分は `<キー>_omitted`。全件はレポートの `full_lists` |

FBX の中のメッシュや画像を変えるときは、`dependency_filter: "all"` か、そのファイルを `assets` に直接入れる（`editable` には入らない）。`Packages/` は退避しない（`SKIPPED_PACKAGE`）。

**action ごとの出力（要約に残るキー）**

| action | キー |
| --- | --- |
| create | `id`・`label`・`created_at`・`snapshot_dir`・`manifest_path`・`file_count`・`folder_count`・`total_bytes`・`total_mb`・`incomplete`・`scenes[]`（`scene_state`: disk / in_memory）・`file_types`（拡張子ごとの数）・`skipped` |
| list | `snapshots_dir`・`snapshots[]`（`id`・`label`・`created_at`・`kind`: manual / pre-restore・`file_count`・`total_mb`）・`total_count`・`total_mb_all`・`omitted` |
| diff | `counts`（unchanged・modified・deleted・meta_changed・error・added）・`changes[]`（変わったファイルだけ。unchanged は載らない。未保存の変更があるシーンは unchanged でも載る。`status`、YAML の `lines_added`・`lines_removed`、`snapshot_file`＝退避したコピーの絶対パス）・`added`（退避したフォルダーに増えたファイル） |
| restore（ドライラン） | `dry_run: true`・`would_overwrite`・`would_recreate`・`would_restore_meta`・`would_reimport`・`would_reload_scenes`・`would_create_folders`・`unchanged_count` |
| restore（confirm） | `dry_run: false`・`pre_restore_snapshot_id`・`restored`・`recreated`・`meta_restored`・`reimported`・`reloaded_scenes`・`created_folders`・`failed`・`created_by_original_restore` |
| delete | `dry_run`・`deleted`・`label`・`file_count`・`total_mb` |

restore の動き：まず今の状態（未保存のシーンを含む）を自動で退避して `pre_restore_snapshot_id` を返す（これを restore すると取り消せる。ただし restore が作り直したファイルは取り消しで消えない：`RECREATED_FILES_REMAIN`）。次に影響するシーンを閉じ、ファイルと .meta を書き、再インポートし、シーンを同じ Hierarchy の位置で開き直す。メモリ上の未保存の変更は捨てる。追加されたファイルは消さない。スクリプトを戻すと、呼び出しの後でコンパイルとドメインリロードが起きる（`SCRIPTS_RESTORED`）ので、MCP がつながり直すまで次を呼ばない。Play 中・コンパイル中・インポート中・ライトベイク中は断る（`EDITOR_BUSY`）。

**issue コード**

| コード | 重大度 | 意味 |
| --- | --- | --- |
| `CONFIRM_REQUIRED` | warning | ドライラン。何も変えていない。confirm=true で呼び直す |
| `SNAPSHOT_NOT_FOUND` / `INVALID_ID` / `INVALID_ARGUMENT` | 失敗 | id が無い / id に使えない文字 / action などが不正 |
| `MANIFEST_INVALID` | 失敗 / warning | manifest.json が無い・読めない（list では warning） |
| `NOTHING_TO_SNAPSHOT` / `SIZE_LIMIT` | 失敗 | 退避する物が無い / max_total_mb 超（何もコピーしていない） |
| `PATH_OUTSIDE_PROJECT` / `NOT_ASSET_PATH` / `INVALID_PATH` | error | 絶対パスや `..` / Assets の外 / 不正な文字。飛ばした |
| `SKIPPED_PACKAGE` | info / warning | Packages/ は VCC が管理するので退避しない |
| `ASSET_NOT_FOUND` | warning | パスが存在しない |
| `UNTITLED_SCENE_SKIPPED` | warning | 無題のシーンはファイルが無いので退避できない |
| `SCENE_CAPTURED_FROM_MEMORY` | info | 未保存のシーンをメモリの状態で退避した |
| `SCENE_CAPTURE_FAILED` / `SCENE_STATE_CHANGED` | warning | メモリの状態を写せずディスクの版にした / 写す処理でシーンのパスや変更の印が変わった |
| `PLAY_MODE_SCENE_FROM_DISK` | warning | Play 中はディスクのシーンを写す |
| `ASSET_HAS_UNSAVED_CHANGES` | warning | ファイルに入っていない未保存の変更がある |
| `PREFAB_STAGE_UNSAVED` | warning / 失敗 | Prefab Mode に未保存の変更（restore は断る） |
| `DEPENDENCIES_FAILED` | warning | 依存の取得が例外 |
| `COPY_FAILED` | error / 失敗 | コピーできないファイル（1つもできなければ失敗） |
| `TEMP_CLEANUP_FAILED` | warning | 一時コピー・一時フォルダーを消せなかった |
| `SCENE_HAS_UNSAVED_CHANGES` | warning | diff：シーンのメモリとディスクが違う（保存してから diff） |
| `FILES_ADDED` | info | 退避したフォルダーに増えたファイル |
| `PROJECT_ROOT_DIFFERS` | warning | 別のプロジェクトのフォルダーで作った退避 |
| `BLUEPRINT_ID_CHANGES` | warning | .unity/.prefab の Blueprint ID が退避と違う（戻すとアップロード先が変わる） |
| `NOT_IN_SNAPSHOT` | warning / 失敗 | 指定したパスが退避に無い |
| `SNAPSHOT_CORRUPT` | error | 退避したコピーが無い・SHA-256 が違う |
| `FILE_READ_ONLY` | error | 戻し先が読み取り専用 |
| `GUID_CONFLICT` | warning | 退避の GUID を今は別のアセットが使っている |
| `EDITOR_BUSY` | 失敗 | Play 中・コンパイル中・インポート中・ベイク中は restore を断る |
| `PRE_RESTORE_SNAPSHOT` | info | 自動退避の id |
| `PRE_RESTORE_FAILED` | 失敗 | 今の状態を退避できず中止した |
| `RECREATED_FILES_REMAIN` | warning | 取り消しの restore：元の restore が作り直したファイルが残る |
| `FILE_RESTORED` / `FILE_RECREATED` / `META_RESTORED` | info | 上書きした / 作り直した / .meta だけ戻した |
| `SCENE_RELOADED` / `SCRIPTS_RESTORED` / `ASSET_REIMPORTED` | info | シーンを読み直した / スクリプトを戻した（コンパイルが続く） / 未保存の変更を捨てるため再インポートした |
| `RESTORE_FAILED` | error / 失敗 | 書き込み・インポートの失敗 |
| `RESTORE_VERIFY_FAILED` | error | 書いた後の SHA-256 が違う |
| `GUID_MISMATCH_AFTER_RESTORE` | warning | インポート後の GUID が退避と違う |
| `ASSET_STILL_DIRTY` | warning | 再インポート後も未保存の変更が残る |
| `SCENE_CLOSE_FAILED` | 失敗 | シーンを閉じられず、何も書いていない |
| `SCENE_REOPEN_FAILED` | error / warning | シーンを開き直せなかった（手で開く） |
| `DELETE_FAILED` | 失敗 | 退避フォルダーを消せなかった |

**副作用**：create は `<プロジェクト>/ClaudeCreatorTools/Snapshots/<id>/` に書く。未保存のシーンを写すときは `Assets/__ClaudeCreatorToolsTemp` に一時コピーを作って消す（開いているシーンのパスと未保存の状態は変わらない。他のパッケージのシーン保存フックは Ctrl+S と同じく動く）。restore と delete は confirm=true のときだけファイルを変える。

**呼び出し例**

```jsonc
// 作業前：変える Material フォルダーとプレハブ、開いているシーン
{"action": "create", "label": "衣装削除前", "assets": ["Assets/Avatar/Materials", "Assets/Avatar/Avatar.prefab"]}
{"tool_name": "cct_snapshot", "parameters": {"action": "create", "label": "衣装削除前", "assets": ["Assets/Avatar/Materials", "Assets/Avatar/Avatar.prefab"]}}
// 依存（編集可能な種類）もまとめて
{"action": "create", "label": "軽量化前", "include_dependencies": true, "timeout_seconds": 240}
{"tool_name": "cct_snapshot", "parameters": {"action": "create", "label": "軽量化前", "include_dependencies": true, "timeout_seconds": 240}}
// 一覧と差分
{"action": "list", "limit": 10}
{"tool_name": "cct_snapshot", "parameters": {"action": "list", "limit": 10}}
{"action": "diff", "id": "20261025-101500-1a2b"}
{"tool_name": "cct_snapshot", "parameters": {"action": "diff", "id": "20261025-101500-1a2b"}}
// 復元：ドライラン → ユーザーの同意 → confirm
{"action": "restore", "id": "20261025-101500-1a2b"}
{"tool_name": "cct_snapshot", "parameters": {"action": "restore", "id": "20261025-101500-1a2b"}}
{"action": "restore", "id": "20261025-101500-1a2b", "confirm": true}
{"tool_name": "cct_snapshot", "parameters": {"action": "restore", "id": "20261025-101500-1a2b", "confirm": true}}
```

---

## 6. cct_animator_inspect

**目的**：AnimatorController の表と、VRChat 向けの検査。`menu → parameter → MA Merge Animator → layer/state/transition/driver → clip binding → object` の鎖を静的にたどる。読み取り専用。

**入力の決まり方**：`controller` を渡せばそのアセット。`target`（アバター）なら VRCAvatarDescriptor の Playable Layers から `layer` の Controller を選ぶ。既定（isDefault）のレイヤーは VRChat 内蔵の Controller が使われるので、代わりに SDK 同梱のサンプル（`Packages/com.vrchat.avatars/Samples/AV3 Demo Assets/Animation/Controllers`、NDMF が代用するもの）を調べて `SDK_DEFAULT_LAYER` を出す。MA Merge Animator は一覧にし、同じレイヤーに統合されるものも調べる。Descriptor が無ければ Animator の Controller か MA Merge Animator を調べる。

**引数**

| 引数 | 型 | 既定 | 意味 |
| --- | --- | --- | --- |
| `controller` | string | `""` | AnimatorController / AnimatorOverrideController のパス・GUID・ユニークなアセット名。target と一緒なら、そのアバターで使われているかを確かめる |
| `target` | string | `""` | アバターか物（0.5）。Project で Controller を選んでいればそれを使う |
| `layer` | string | `"fx"` | `fx`・`gesture`・`action`・`base`・`additive`・`sitting`・`tpose`・`ikpose`・`all` |
| `include_ma` | bool | target があれば true | MA Merge Animator を一覧にして、そのレイヤーの分も調べる |
| `layers` | string[] | 全部 | 調べるアニメーターのレイヤー名か番号（WD の Controller 全体の判定は全レイヤーで行う） |
| `include_transitions` | bool | true | graph に遷移を入れる（検査は常に行う） |
| `check_bindings` | bool | target があれば true | クリップのカーブのパスをアバターのルート（Relative の MA は relative root）から確かめる |
| `max_states` | int | 400 | graph に書くステートの上限（0〜20000） |
| `find_parameter` | string | `""` | パラメーター名（大文字小文字を区別する。一致が無く大文字小文字だけ違う名前があれば `case_insensitive_matches` に出る）：宣言、条件、BlendTree、speed/time、Driver の読み書き、Play Audio をすべて列挙 |
| `find_binding` | string | `""` | カーブのパスと `Component.property` に対する部分一致（大文字小文字を区別しない）。`path:property` で両方。例 `Body:blendShape.smile` で顔のシェイプを書くクリップとステートを列挙 |

走査は `timeout_seconds × 0.6`（最低8秒）で止まり `TRUNCATED` を出す。大きい FX では `timeout_seconds: 120` を渡すか `layers` で絞る。検索結果は要約に50行、レポートの `find_all` に最大2000行。

**要約に残るキー**：`source`、`playable_layers`、`controllers`、`layers`、`parameters`、`graph_preview`、`bindings`、`ma_merge_animators`、`find`、`notes`（と各 `_omitted`・`_total`）。レポートにだけ：`graph`（全ステート・Motion・Behaviour・遷移）、`clips`（クリップの一覧）、`controllers_all`・`layers_all`・`parameters_all`・`ma_merge_animators_all`（要約の上限を超えたときだけ）、`ma_merge_blend_trees`、`find_all`（検索結果が要約の50行を超えたときだけ）。

| キー | 中身 |
| --- | --- |
| `source` | `mode`・`layer`・`stage`（`pre_build` / `play_mode`）・`observed_at`・`target`・`avatar`・`customize_animation_layers`・`expression_parameters`・`build_tools`（MA と NDMF の版） |
| `playable_layers[]` | Descriptor の各レイヤー：`type`・`is_default`・`controller`・`sdk_default_asset`・`mask`・`inspected_as` |
| `controllers[]` | 調べた Controller（id は fx・gesture…、MA は ma1…） |
| `layers[]` | 重み・ブレンド・マスク・同期・WD の ON/OFF・ステート数・MA の WD 予測 |
| `parameters[]` | Controller のパラメーターと、Expression Parameter・組み込みの型 |
| `graph` のステート | `name`・`path`・`default`・`motion`・`speed`・`speed_param`・`motion_time_param`・`mirror`・`mirror_param`・`cycle_offset`・`cycle_offset_param`・`wd`・`tag`・`behaviours`（Parameter Driver、Tracking・Layer・Playable Layer・Locomotion Control、Temporary Pose Space、Play Audio）・`transitions`（条件付き）。既定値のキーは省かれる：`speed` は 1 以外のときだけ（無ければ speed 1）、`speed_param`・`motion_time_param`・`mirror_param`・`cycle_offset_param` は Inspector でパラメーターを有効にしたときだけ入る |
| `find` | `find.parameter`（find_parameter）：`declared_in[]`（どの Controller に `type`・`default` で宣言されているか）、`expression`（Expression Parameter の `type`・`synced`・`saved`・`default`）、`builtin`（組み込みなら型）、`uses_total`、`uses[]`（`controller`・`kind`・`at`・`detail`）。`find.binding`（find_binding）：`matches_total`、`matches[]`（`clip`・`asset`・`path`・`component`・`property`・`constant`・`values`・`exists`・`used_by`）、`clips_searched`。どちらも要約は50行まで（超えると `full_list` の案内が付き、全件はレポートの `find_all`） |

issue の `path` は `<controller id>:<レイヤー>/<ステートのパス>`（遷移は ` -> <行き先>`、`[AnyState]`・`[Entry]`・`[Exit]`）か、MA の部品なら階層パス。`asset` は Controller（Binding の issue はクリップ）。

**issue コード**

| コード | 重大度 | 意味 |
| --- | --- | --- |
| `TARGET_ADJUSTED` / `ARG_ADJUSTED` | info | 対象をアバターのルートへ移した・Project の Controller を使った / 知らない layer を fx にした |
| `PLAY_MODE` | info | Play 中。NDMF 処理済み（統合後の Controller、MA の部品は消えている）の可能性 |
| `SDK_DEFAULT_LAYER` | info | 既定レイヤー。SDK のサンプルを調べた（推定） |
| `SDK_DEFAULT_CONTROLLER_NOT_FOUND` | warning | 既定レイヤーのサンプルが無い |
| `LAYER_CONTROLLER_MISSING` | warning | カスタマイズしたレイヤーに Controller が無い |
| `OVERRIDE_CONTROLLER_IN_PLAYABLE_LAYER` | warning/info | Playable Layer に Override Controller（MA があれば info） |
| `CONTROLLER_REUSED` | warning | 同じ Controller を複数のレイヤーで使っている |
| `CONTROLLER_UNREADABLE` | warning | ベースの無い Override Controller など |
| `LAYER_NO_STATE_MACHINE` | warning | ステートマシンの無いレイヤー |
| `PARAM_DUPLICATE` | warning | 同じパラメーター名が2回 |
| `PARAM_UNDEFINED` | warning/info | 条件・BlendTree・Driver などのパラメーターが Controller に無い |
| `PARAM_TYPE_MISMATCH` | warning/info | 型の不一致（`value.source`：usage＝使い方と型、expression・builtin＝Expression Parameter・組み込みの型と違う、conversion_lossless＝損失の無い変換）。MA があり NDMF が直すものは info |
| `PARAM_TRIGGER` | info | Trigger のパラメーター（非推奨） |
| `EXPRESSION_PARAM_NOT_IN_ANIMATOR` | info | layer=all：どの Controller にも無い Expression Parameter |
| `WD_MIXED` / `WD_MIXED_ACROSS_CONTROLLERS` | warning/info / info | WD の混在（SDK の規則） / Controller 間で違う |
| `EMPTY_STATE_WD_OFF` / `DIRECT_BLENDTREE_WD_OFF` / `ADDITIVE_LAYER_WD_OFF` | warning/info | WD OFF で空 / Direct BlendTree / Additive レイヤー |
| `MISSING_MOTION` / `MISSING_BEHAVIOUR` | warning | 削除済みの Motion / スクリプトの無い Behaviour |
| `TRANSITION_NEVER_FIRES` / `TRANSITION_NO_DESTINATION` | warning | 条件も Exit Time も無い遷移 / 行き先の無い遷移 |
| `ANYSTATE_SELF_LOOP` | warning/info | Can Transition To Self で Trigger 以外：条件が真の間入り直す |
| `UNREACHABLE_STATE` | info | どこからも来ないステート |
| `LAYER_WEIGHT_ZERO` / `LAYER_EMPTY` | info | 重み0で上げる Layer Control が無い / ステートの無いレイヤー |
| `ACTION_WEIGHT_NEVER_RAISED` / `ACTION_WEIGHT_NOT_LOWERED` | info / warning/info | Action を上げる・下げる Playable Layer Control が無い |
| `TRACKING_NOT_RESTORED` / `LOCOMOTION_NOT_RESTORED` / `POSE_SPACE_NOT_RESTORED` | warning/info | Animation にした部位を Tracking に戻すステートが無い / 移動を戻さない / Pose Space を抜けない（warning になるのはアバターを layer=all・layers 無し・include_ma・時間切れ無しで調べたときだけ。目・口・指は意図して Animation のままにする仕組みが多いので常に info） |
| `DRIVER_BUILTIN_DESTINATION` / `DRIVER_COPY_BUILTIN_SOURCE` / `DRIVER_ADD_ON_BOOL` | warning | Driver が組み込みに書く / 組み込みから Copy / Bool に Add |
| `DRIVER_IN_NON_LOCAL` / `DRIVER_VALUE_OUT_OF_RANGE` / `DRIVER_COPY_TYPE_CONVERSION` | info | Local Only でない Add・Random / 範囲外の値 / 型の違う Copy |
| `LAYER_CONTROL_BASE_LAYER` / `LAYER_CONTROL_INDEX_OUT_OF_RANGE` | warning | Layer Control がレイヤー0 / 範囲外 |
| `PLAY_AUDIO_SOURCE_MISSING` / `PLAY_AUDIO_PARAM_NOT_IN_EXPRESSIONS` / `PLAY_AUDIO_ON_STATE_MACHINE` | warning / warning / info | Play Audio の設定 |
| `BINDING_PATH_MISSING` / `BINDING_COMPONENT_MISSING` / `BINDING_BLENDSHAPE_MISSING` | warning/info | カーブのパスが無い / コンポーネントが無い / BlendShape が無い |
| `NON_TRANSFORM_IN_NON_FX` / `FX_TRANSFORM_ANIMATION` | warning / info | FX 以外で Transform 以外を動かす / FX で Transform を動かす |
| `MA_MERGE_ANIMATOR_NO_ANIMATOR` | warning | Controller の無い Merge Animator |
| `MA_MULTIPLE_REPLACE` | error | 1つのレイヤーに Replace が複数（MA のビルドエラー） |
| `MA_REPLACE_MODE` / `MA_EDITOR_ONLY` | info | Replace でアバターの Controller が捨てられる / EditorOnly の下で統合前に消える |
| `MA_RELATIVE_ROOT_UNRESOLVED` / `MA_PATH_MODE_MISMATCH` | warning | relative root を解決できない / Relative と Absolute の取り違え |
| `MA_PARAM_TYPE_COERCED` | info/warning | 同名で型が違い MA が Float にする（片方が Trigger なら warning） |
| `MA_WD_FORCED_ON` / `MA_WD_NOT_MATCHED` | info / warning/info | MA が WD を ON にする / WD がアバターと合わない |
| `MA_LAYER_CONTROL_UNSUPPORTED` | warning | MA の Controller の Layer Control が別の Playable を指す |
| `FIND_NO_MATCH` / `TRUNCATED` | info | 検索に一致なし / graph の上限・時間で打ち切り |
| `SCENE_DIRTIED` / `INSPECT_SECTION_FAILED` | warning | 呼び出し中にシーンが変更扱い / 1つの節が例外 |

失敗コード（候補は `data.candidates`）：`CONTROLLER_NOT_FOUND`、`AMBIGUOUS_CONTROLLER`、`TARGET_NOT_FOUND`、`AMBIGUOUS_TARGET`、`NO_TARGET`（何も指定・選択されておらず、アバターがちょうど1体ではない）、`NO_CONTROLLER`、`LAYER_NOT_FOUND`。

**副作用**：なし。

**呼び出し例**

```jsonc
// 選択中のアバターの FX
{"layer": "fx"}
{"tool_name": "cct_animator_inspect", "parameters": {"layer": "fx"}}
// 全レイヤーでパラメーターを追う
{"target": "/Avatar", "layer": "all", "find_parameter": "VRCEmote", "timeout_seconds": 120}
{"tool_name": "cct_animator_inspect", "parameters": {"target": "/Avatar", "layer": "all", "find_parameter": "VRCEmote", "timeout_seconds": 120}}
// 顔のシェイプを書いているクリップとステート
{"target": "/Avatar", "layer": "all", "find_binding": "Body:blendShape.smile"}
{"tool_name": "cct_animator_inspect", "parameters": {"target": "/Avatar", "layer": "all", "find_binding": "Body:blendShape.smile"}}
// Controller アセットだけ、レイヤーを絞って
{"controller": "Assets/Avatar/FX.controller", "layers": ["Face", "Hand"]}
{"tool_name": "cct_animator_inspect", "parameters": {"controller": "Assets/Avatar/FX.controller", "layers": ["Face", "Hand"]}}
```

---

## 7. cct_dependency_report

**目的**：改造したギミック・衣装を別のアバター・プロジェクトへ移す前に依存を調べる。アセットを消す・置き換える前に「誰が使っているか」を調べる。読み取り専用。

移植の流れ：シーンのギミックのルートに `scene_references` → 独立プレハブとして保存（これは別の操作） → そのプレハブかフォルダーに `dependencies`（`export_root` と `declared_packages` 付き）。

**引数**

| 引数 | 型 | 既定 | 使う mode | 意味 |
| --- | --- | --- | --- | --- |
| `mode` | string | `"dependencies"` | — | `dependencies`・`reverse`・`scene_references` |
| `assets` | string[] | Project の選択 | dependencies / reverse | `Assets/...`・`Packages/...` のパス、フォルダー（中の全ファイル）、32桁の GUID |
| `export_root` | string | `assets` に入れたフォルダー | dependencies | 書き出す範囲のフォルダー（カンマ区切り）。外の `Assets/` の依存は `OUTSIDE_EXPORT_ROOT`。存在しないフォルダーを渡すと `INVALID_ARGUMENT` で失敗する。`export_root` を渡さず `assets` にもフォルダーが無ければ、内外の判定をしない（`NO_EXPORT_ROOT`） |
| `include_scripts` | bool | true | dependencies | false で `Assets/` の .cs/.dll を外す（スクリプトが要るパッケージは常に出す） |
| `declared_packages` | string[] | — | dependencies | 移す先にあると分かっているパッケージ（名前か `com.vrchat.*` のような前方一致）。ほかは `PACKAGE_DEPENDENCY` の warning |
| `target` | string | scene_references では選択中 | scene_references / dependencies | scene_references：調べるシーンの GameObject（省くと Hierarchy の選択。何も選んでいなければ `TARGET_NOT_FOUND`）。dependencies：任意。直接参照するアセットを入力に足すシーンの物か、プレハブのパス（`target` を渡すと Project の選択は使わない） |
| `search_root` | string | `"Assets"` | reverse | 探すフォルダー（カンマ区切り、例 `Assets,Packages/com.example`）。存在しないフォルダーは `INVALID_ARGUMENT` |
| `scan_open_scenes` | bool | true | reverse | 読み込み中のシーンと Prefab Mode のメモリ上の参照も探す |
| `scan_code` | bool | false | reverse | .cs/.shader/.hlsl/.cginc/.compute の中の GUID 文字列も探す |
| `include_inbound` | bool | true | scene_references | 対象の外から対象の中を指す参照も出す |
| `max_components` | int | 20000 | reverse / scene_references | 走査するシーンのコンポーネントの上限（100〜2000000） |
| `max_file_mb` | double | 50 | 全部 | これより大きい入力以外のファイルは GUID・Missing の走査から外す（0 で無制限） |
| `large_file_mb` | double | 4 | dependencies | これより大きい依存を `LARGE_FILE` で知らせる |
| `max_entries` | int | 50 | 全部 | 返すリストの各上限（1〜2000）。件数は全体を数える |
| `time_budget_seconds` | double | `timeout_seconds - 5` | 全部 | 走査を早めに止める（延ばせない。延ばすなら `timeout_seconds`） |

走査は `timeout_seconds - 5` 秒（既定 25 秒）で止まる。プロジェクト全体の reverse や大きいフォルダーは `timeout_seconds: 240` を渡す。Edit モードで呼ぶ（`PLAY_MODE`）。

**要約に残る主なキー**

| mode | キー |
| --- | --- |
| 共通 | `mode`・`asset_serialization`・`time_budget`・`issues_capped`・`conclusive`（false＝どこかの走査が途中まで。「Missing 0」「使われていない」を確定扱いしない）・`scan` |
| dependencies | `inputs`・`target_assets`・`export_roots`・`totals`・`by_type`・`inside_export_root`・`assets_outside_root`（`Assets/A/B` ごとの MB と `referenced_from`）・`required_packages[]`（`name`・`version_installed`＝この PC で入っている版で最低版ではない・`installed_via`＝vpm など・`depends_on`・`referenced_by`）・`builtin`・`missing_guids`（ファイルと行と欄）・`missing_scripts`・`missing_prefabs`・`missing_references`・`asmdefs`・`large_files`・`unsaved`・`dependency_list_path`（TSV：group, input, path, guid, type, bytes。group は inside / outside / package:<名前> / builtin / excluded_script、input は入力なら 1）・`dependency_list_rows` |
| reverse | `targets`・`search_roots`・`user_count`・`users`・`users_by_type`・`internal_reference_count`・`per_target`・`unused_targets`・`scene_user_count`・`scene_users`・`scenes_scanned` |
| scene_references | `target`・`is_prefab_asset`・`components_scanned`・`asset_reference_count`・`external_reference_count`・`external_references`・`path_resolved_references`・`scene_only_objects`・`missing_reference_count`・`missing_script_count`・`missing_prefab_count`・`inbound_reference_count`・`inbound_references`・`prefab`（オーバーライドの状態） |

reverse で拾えないもの：バイナリのファイル、コードからの名前での使用（`Shader.Find`・`Resources.Load`）。書き出し済みの `.unitypackage` の中身は読まない。

**issue コード**

| コード | 重大度 | 意味 |
| --- | --- | --- |
| `MISSING_GUID` | error / warning | YAML・.meta の `guid:` がどのアセットにも解決しない（.meta のインポート設定だけなら warning） |
| `MISSING_SCRIPT` / `MISSING_PREFAB` | error | Missing Script / 元のプレハブが無いネストしたプレハブ |
| `MISSING_REFERENCE` | warning | Missing の参照（削除済み、または GUID は合うが fileID が違う） |
| `SCRIPT_CLASS_NOT_FOUND` | warning / info | クラスを解決できないスクリプト（コンパイルエラー・名前の不一致） |
| `EXTERNAL_SCENE_REFERENCE` | error | 対象の中から外のシーンの物への参照（プレハブにすると None になる） |
| `PATH_RESOLVED_REFERENCE` | info | 同上だが MA がパスで解決し直す参照 |
| `SCENE_ONLY_OBJECT` | warning | シーンやメモリにしか無い Mesh・Material などへの参照 |
| `INBOUND_SCENE_REFERENCE` | info | 外から対象の中への参照（書き出されない） |
| `PREFAB_HAS_OVERRIDES` / `PART_OF_PREFAB_INSTANCE` | info | オーバーライドのあるプレハブのインスタンス（元のプレハブを書き出すと変更が落ちる） / インスタンスの中の子（新しいプレハブとして保存する） |
| `OUTSIDE_EXPORT_ROOT` | warning | export_root の外の `Assets/` の依存 |
| `ASMDEF_REQUIRED` | warning | 書き出す範囲の外の .asmdef でコンパイルされるスクリプト |
| `PACKAGE_DEPENDENCY` | info / warning | パッケージへの依存（`declared_packages` を渡していてそこに無い、または VPM 管理外の埋め込み・ローカルのパッケージなら warning。Unity 組み込みのモジュールは出さない） |
| `SCENE_IN_DEPENDENCIES` | warning | .unity が依存に入った |
| `NO_EXPORT_ROOT` | info | export_root もフォルダーの入力も無く、内外の判定を省いた |
| `LARGE_FILE` | info | large_file_mb より大きい依存 |
| `BINARY_SERIALIZATION` / `BINARY_FILES_SKIPPED` | warning / info | Asset Serialization が Force Text でない / バイナリのファイルを GUID 走査しなかった |
| `SCAN_INCOMPLETE` | warning | 大きすぎ・読めないファイル、上限、時間で途中まで |
| `UNSAVED_CHANGES` | warning | 未保存の変更（ファイルの結果はディスクの状態） |
| `INPUT_NOT_FOUND` | error | 指定したパス・GUID が無い |
| `TARGET_USED` / `TARGET_UNUSED` / `USED_IN_OPEN_SCENE` / `NAME_BASED_REFERENCE_POSSIBLE` | info | reverse：使われている / 使われていない（`conclusive` を見る） / シーンのメモリで使われている / コードから名前で使われうる |
| `PACKAGE_LIST_FAILED` / `FILE_LIST_WRITE_FAILED` | warning | パッケージ一覧を読めない / TSV を書けない |
| `ISSUES_CAPPED` | info | 1コード100件を超えた分は `issues_capped` に数だけ |
| `SCENE_DIRTIED` / `PLAY_MODE` | warning | 実行中にシーンが変更扱い / Play 中（実行時のコピーを見ている） |

失敗コード：`INVALID_ARGUMENT`（mode の綴り違い、存在しない `export_root`・`search_root`）、`NO_INPUT`、`TARGET_NOT_FOUND`、`AMBIGUOUS_TARGET`（`details` に候補）、`DEPENDENCIES_FAILED`。

**副作用**：Assets・Packages・シーンは変えない。レポートと、dependencies では TSV を Reports に書く。Missing の走査はプレハブ・アセットを最大1000個エディタのメモリに読み込む（次のアンロードで解放）。

**呼び出し例**

```jsonc
// 1) シーンのギミックのシーン外参照
{"mode": "scene_references", "target": "/Avatar/Gimmick"}
{"tool_name": "cct_dependency_report", "parameters": {"mode": "scene_references", "target": "/Avatar/Gimmick"}}
// 2) 書き出すフォルダーの依存
{"mode": "dependencies", "assets": ["Assets/MyGimmick"], "export_root": "Assets/MyGimmick", "declared_packages": ["com.vrchat.*", "nadena.dev.*"]}
{"tool_name": "cct_dependency_report", "parameters": {"mode": "dependencies", "assets": ["Assets/MyGimmick"], "export_root": "Assets/MyGimmick", "declared_packages": ["com.vrchat.*", "nadena.dev.*"]}}
// 3) 消す前に使用元を探す
{"mode": "reverse", "assets": ["Assets/Old/Outfit.mat"], "timeout_seconds": 240}
{"tool_name": "cct_dependency_report", "parameters": {"mode": "reverse", "assets": ["Assets/Old/Outfit.mat"], "timeout_seconds": 240}}
```

---

## 8. cct_world_gimmicks

**目的**：パッケージの `Samples~/WorldGimmicks` にある UdonSharp のワールドギミック8種を一覧にし、ワールドプロジェクトへ取り込む。サンプル：`CctLocalToggle`（自分だけの ON/OFF）、`CctGlobalToggle`（全員で共有、Manual 同期・後から入った人にも反映）、`CctMirrorSwitcher`（HQ/LQ/OFF の切替）、`CctTeleporter`、`CctPickupResetter`（ObjectSync 対応）、`CctAudioVolume`（UI スライダー、PlayerData で保存）、`CctAreaToggle`（エリアにいる間だけ表示）、`CctPlayerCounter`（人数と参加・退出のログ）。

**引数**

| 引数 | 型 | 既定 | 意味 |
| --- | --- | --- | --- |
| `action` | string | `"list"` | `list`（読み取り専用）、`import`（書き込み） |
| `dest` | string | `"Assets/ClaudeWorldGimmicks"` | 取り込み先。asmdef/asmref のあるフォルダーの中、Editor フォルダーの中、`Assets/Plugins`（Standard Assets も）は不可 |
| `confirm` | bool | false | import：true のときだけ書く。無ければドライラン |
| `overwrite` | bool | false | import：取り込み先にあるサンプルのファイルを上書きする（既存の .meta は常に残す） |
| `create_program_assets` | bool | true | import：各スクリプトの隣に UdonSharpProgramAsset（`<名前>.asset`）を作る |
| `only` | string[] | 全部 | import：取り込むサンプルのクラス名（大文字小文字を区別しない。README は常に入る） |

**出力（要約に残るキー）**：`action`、`udonsharp_installed`、`udonsharp_program_asset_type`、`vrchat_worlds_sdk`、`vrchat_worlds_sdk_version`、`requirements`（`{名前: 有無}`：TextMeshPro、uGUI、PlayerData、NetworkCallable）、`missing`、`dest`、`dest_exists`、`dest_problem`、`asmdef`、`samples[]`（`name`・`summary`・`sync_mode`・`requires`・`missing_requirements`・`source`＝パッケージ内のスクリプトの絶対パス、冒頭に日本語の設定手順・`target`・`imported`・`program_asset`・`found_elsewhere`・`found_elsewhere_program_asset`）、`sample_count`、`imported_count`、`program_asset_count`、`importable`（今取り込めるサンプル名。そのまま `only` に渡せる）、`dry_run`、`plan[{target, action}]`、`program_assets_planned`、`files_written`、`files_failed`、`files_skipped[{target, reason}]`、`program_assets_created`、`next_steps`（日本語の次の手順）。

取り込んだ後は `<dest>/README.md` を読む：ギミックごとに、スクリプトを付ける GameObject、Inspector の欄とシリアライズ名（MCP で設定するとき用）、Collider・Is Trigger・レイヤー、UI の OnClick / OnValueChanged の配線（`UdonBehaviour.SendCustomEvent` と正確なメソッド名）、ClientSim と Build & Test（2クライアント）の確認項目。

**issue コード**

| コード | 重大度 | 意味 |
| --- | --- | --- |
| `INVALID_ARGUMENT` | 失敗 | action が list・import でない |
| `SAMPLES_NOT_FOUND` | 失敗 | パッケージか Samples~/WorldGimmicks が無い |
| `INVALID_DEST` | 失敗 | dest が Assets の下のフォルダーでない（絶対パス、`..`、隠し・`~`、不正な文字） |
| `NO_UDONSHARP` | 失敗 / info | UdonSharp が無い（アバタープロジェクト）。import を断る |
| `MISSING_DEPENDENCY` | 失敗 / warning | Worlds SDK か、選んだサンプルが使うパッケージが無い。断る（`only` から外せば残りは取り込める。失敗の結果に `missing` と `importable`） |
| `PLAY_MODE` | 失敗 | Play 中は import を断る |
| `DEST_HAS_ASMDEF` / `DEST_SPECIAL_FOLDER` | 失敗 / warning | 取り込み先が asmdef の下 / Editor・Plugins の下（U# がコンパイルしない） |
| `UNKNOWN_SAMPLE` | 失敗 | `only` に知らない名前 |
| `CONFIRM_REQUIRED` | warning | ドライラン。何も書いていない |
| `SAMPLE_EXISTS_ELSEWHERE` | warning | 同じスクリプトが別の場所にある（list で報告、import ではそのサンプルを飛ばす。同じクラスが2つあるとコンパイルできない） |
| `FILE_EXISTS` / `META_KEPT` / `META_MISSING` | info | 既存のファイルを飛ばした / 既存の .meta を残した / .meta の無いファイル（新しい GUID になる） |
| `FILE_OVERWRITTEN` | warning | overwrite=true で上書きした |
| `GUID_IN_USE` | warning | サンプルの GUID を別のアセットが使っている。.meta をコピーしない |
| `COPY_FAILED` | error | コピーできないファイル |
| `COMPILE_PENDING` | info | 書いた後、Unity と UdonSharp がこの呼び出しの後でコンパイルする |
| `PROGRAM_ASSET_CREATED` / `PROGRAM_ASSET_EXISTS` | info | プログラムアセットを作った / すでにある |
| `PROGRAM_ASSET_TYPE_MISSING` / `PROGRAM_ASSET_FAILED` | warning | プログラムアセットの型が無い / 作れなかった（手で作る。next_steps） |
| `ALREADY_IMPORTED` / `PARTIALLY_IMPORTED` | info | list：全部 / 一部が取り込み済み |

**副作用**：list は無し。import は confirm=true のときだけ、`Samples~/WorldGimmicks`（か `only` の分）を .meta ごと `dest` にコピーし、`AssetDatabase.Refresh` し、プログラムアセットを作る（別の場所にあるコピーでプログラムアセットが無いものにも作る）。その後 Unity がコンパイルするので、次の呼び出しは `EDITOR_BUSY` が出なくなるまで待つ。

**呼び出し例**

```jsonc
// 一覧（先に必ず）
{"action": "list"}
{"tool_name": "cct_world_gimmicks", "parameters": {"action": "list"}}
// ドライラン
{"action": "import", "only": ["CctTeleporter"]}
{"tool_name": "cct_world_gimmicks", "parameters": {"action": "import", "only": ["CctTeleporter"]}}
// ユーザーの同意の後
{"action": "import", "only": ["CctTeleporter"], "confirm": true}
{"tool_name": "cct_world_gimmicks", "parameters": {"action": "import", "only": ["CctTeleporter"], "confirm": true}}
```
