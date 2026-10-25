# 工程ごとの手順

各専門スキルの工程で `cct_*` をどう使うかの手順。何を直すか、何を合格とするかは専門スキルが決める。ここに書くのは、呼ぶ順番、そろえる条件、結果のどこを読むか。引数とコードの意味は [ツールの仕様](tool-reference.md)。

どの手順も、Unity を MCP で操作する規律（計画を1〜3行で示す、1件で試す、失敗後は再送せず読み戻す）は `unity-mcp-operations` に従う。

実機で確かめた範囲（2026-10-25、[台帳](verified-facts.md)）：`cct_environment`、アバターの4方向の撮影、`cct_avatar_audit`（`post_build` なし）、`cct_snapshot` の create・diff と restore のドライラン、`cct_world_gimmicks` の list と import のドライラン、`cct_world_audit`。比較撮影（`compare_with`）、`post_build`、restore・delete・import の実行、`cct_animator_inspect`、`cct_dependency_report` は実機で呼んだ記録が無い。それらを使う手順は、コードから組み立てたもの。

## 目次

- 1. 結果を小さく読む（共通）
- 2. 前後比較の条件をそろえる（共通）
- 3. 軽量化の前後（vrchat-avatar-optimization）
- 4. 材質・肌の比較（vrchat-avatar-material-tuning）
- 5. 表情・ジェスチャーの鎖（vrchat-expression-debugging）
- 6. ギミックの移植（vrchat-gimmick-packaging）
- 7. 手持ち小物の導入と握り位置（vrchat-hand-prop-setup）
- 8. 破壊的操作の前後（unity-mcp-operations 原則2）
- 9. ワールドの点検とギミック（vrchat-world-creation）
- 10. BPM・State の速度（vrchat-bpm-controls）

---

## 1. 結果を小さく読む（共通）

Claude に見えるのは戻り値と、ディスクから読んだファイルだけ。会話に積む量を減らすと、精度も保てる。

1. **要約から読む**。`data.message` → `data.issue_counts` → `data.issues` の上から → ツールの要約キー。たいていはここで判断できる。
2. **issue が多いとき**は `max_issues` を増やす前に、レポートを Grep する。`max_issues: 100` や `detail: "full"` で全部を会話に流さない。Claude Code は1回のツール結果が大きすぎると切り詰めたりファイルに退避したりする（既定の上限 25,000 トークン。Claude Code のドキュメントの記載を2026-10-24の調査で写したもの）ので、全文を流すより `report_path` を読む方が確実。
3. **レポートを開くのは要約で足りないときだけ**。`report_path` は整形 JSON（辞書はキーごとに改行、数値・文字列だけの配列は1行）なので、Grep で絞れる。
   - あるコードの issue だけ：Grep の pattern `"code": "MISSING_SCRIPT"`、`-B 1 -A 5`、path に `report_path`。
   - ある節だけ：Grep で `"breakdown":` の行番号を取り、Read の `offset` と `limit`（100〜200行）で読む。
   - Read で丸ごと開かない。`cct_avatar_audit` の `breakdown`・`rank_tables`、`cct_animator_inspect` の `graph`、`cct_world_audit` の `components`・`editor_log` は長い。
4. **画像は1枚から**。`cct_capture_views` は PNG の絶対パスを返す。まず `contact_sheet` だけを Read で開く。細部が要るときだけ個別のビュー（`views[].file`）を開く。比較では、`changed_pct_foreground` が大きいビューの `compare_image`（左が前・右が後）を開き、場所が分からないときだけ `diff_image`（変化を赤で示す）を開く。全部の PNG を開かない。
5. **id とパスを控える**。スナップショットの `id`、撮影の `capture_dir`、`report_path` は次の工程で使うので、報告の文に残しておく。instance ID はドメインリロードで変わるので控えない（階層パスを控える）。
6. **同じ状態を2回読まない**。状態を変える操作をしていなければ、同じツールを同じ引数で呼び直さない。

## 2. 前後比較の条件をそろえる（共通）

- **対象**：同じ GameObject を階層パスで指定する。似た名前の服のルートや、前回の instance ID を対象にしない。`cct_environment` の `avatar_roots` から選ぶ。
- **段階**：`cct_avatar_audit` は `post_build` の有無をそろえる（ビルド前どうし、NDMF 処理後どうし）。Play 中の値（`method.stage: "play_mode"`）とエディタの値を比べない。
- **プラットフォーム**：`platform` とアクティブなビルドターゲットをそろえる。テクスチャメモリはアクティブなビルドターゲットのインポート結果で計算される（`TEXTURE_MEMORY_PLATFORM`）。
- **撮影**：2回目以降は `compare_with` だけを渡し、ほかの撮影条件は引き継がせる。条件を変えたいときは、新しい基準を撮り直す（`COMPARE_MISMATCH` になる）。`isolated` と `scene` の画像を比べない。
- **比べられない差**：`_Time` で動くシェーダー（lilToon のスクロール・まばたきなど）は撮るたびに違う。そこに出る差は変更の結果ではない。

## 3. 軽量化の前後（vrchat-avatar-optimization）

専門スキルの決まり：作業前の性能値と問題箇所の画像を残す。比較はアバター・カメラ・ポーズ・表示状態・処理段階をそろえる。変更する共有の Mesh・Material・AnimationClip もコピーする。

1. `cct_environment` で接続先と対象のアバターのパスを確かめる。
2. 計画を示す（対象、何を変えるか、戻し方）。
3. `cct_snapshot` の `create`：変える予定の Material・AnimationClip・プレハブを `assets` に入れ、`include_open_scenes: true`（既定）。どれが変わるか分からなければ `include_dependencies: true`。モデル（FBX）やテクスチャを変えるなら、そのファイルを `assets` に入れるか `dependency_filter: "all"`。`id` を控える。
4. `cct_avatar_audit`：`target` と `platform` を決めて呼ぶ。MA・AAO がある（`PREBUILD_ESTIMATE`）なら、比較の段階を決めて `post_build: true` も呼ぶ（`timeout_seconds: 120`）。控えるもの：`ranks.<platform>.overall`、`limiting_stats`（どの項目が総合ランクを決めているか、次のランクに必要な値 `limit_for_better_rank`）、`upload.blocked`、`report_path`。
5. `cct_capture_views`：全身は既定（4方向）か `views: "turnaround"`、`label: "作業前"`。問題の箇所は別に撮る：指先・胸元は `framing: "upper"`（腰から上なので膝・足は入らない）、膝・足は既定の全身構図を高い `resolution` で。もっと寄るときは、その部位の Renderer を持つ物（服や靴のメッシュ）を `target` にして `mode: "scene"` で撮る（isolated で部品だけを対象にすると、クローンはその部品だけになり、体が写らないので貫通は見えない）。`contact_sheet` を見て、問題の箇所が写っているかを確かめる（撮れたことは写っている証明ではない）。`capture_dir` を控える。
6. 専門スキルの手順で1つずつ直す。
7. `cct_avatar_audit` を同じ引数で呼び、総合ランク・`limiting_stats`・値を前と比べる。三角形が減ったことを改善と決めつけない（外観が崩れていないかを次で見る）。
8. `cct_capture_views` を `compare_with`＝手順5の `capture_dir`、`label: "作業後"` で呼ぶ。`comparisons[]` の `changed_pct_foreground` を見る。変えていない部位で差が大きいビューは、`compare_image` を開いて確かめる。
9. 報告：変更（スナップショットの id 付き）、観測した改善（前後の値と段階）、撮影の比較、確かめていないこと（SDK ビルド、エミュレーター、実機）。

`cct_avatar_audit` は、非アクティブな物や無効なコンポーネントも数える（`notes.caveat`、docs と同じ）。Inspector で無効にしただけの物は減らない。

## 4. 材質・肌の比較（vrchat-avatar-material-tuning）

専門スキルの決まり：同じモデル・姿勢・表情・カメラ・背景・照明・露出で基準と候補を撮る。直前の承認状態を比較基準に残す。SceneView の色だけを根拠にしない。

1. 基準を撮る。条件は最初に明示して、以後は引き継がせる。

   ```jsonc
   {"target": "/Avatar", "views": "front,front_left,front_right", "resolution": 1024, "label": "承認済み"}
   {"tool_name": "cct_capture_views", "parameters": {"target": "/Avatar", "views": "front,front_left,front_right", "resolution": 1024, "label": "承認済み"}}
   ```

   顔だけなら `views: "face"`。`mode` は既定の `isolated`（中立の照明で再現しやすい）。ワールドの照明での見え方を見たいときだけ `mode: "scene"` で別の基準を撮る。
2. 承認済みの `capture_dir` を控える。候補を撮るたびに、`compare_with` にはこの基準のフォルダーを渡す。`latest` を使うと直前の候補と比べてしまう。
3. 部位ごとの数値は、専門スキルの `scripts/compare_regions.py` に、基準と候補の同じビューの PNG を渡す（同じ条件で撮っていれば寸法も同じ）。ユーザーの PC に Pillow と NumPy があるかは未確認。`comparisons[]` の値は画面全体・前景全体の平均で、部位の色差ではない。
4. 肌が暗いときは、`light_follow_camera` を変えた画像と比べない（照明の条件が変わる）。影・RimShade・光沢・発光の切り分けは専門スキルの手順で行い、撮影はその前後をそろえて撮るだけにする。
5. 既存アバターの更新では Blueprint ID を保つ。変更の前に `cct_snapshot create`（対象のシーンかプレハブを含める）、保存した後に `diff` を呼び、`BLUEPRINT_ID_CHANGES` が無いことを確かめる。`cct_environment` の `avatar_roots[].blueprint_id` でも見られる。

## 5. 表情・ジェスチャーの鎖（vrchat-expression-debugging）

専門スキルの鎖：`menu/input -> parameter (default, saved, current) -> MA mapping -> merged playable controller -> transition/driver -> clip binding -> object/component -> visible or world-space result`。cct で読めるのは、このうちビルド前の静的な部分だけ。

1. `cct_avatar_audit`（`target` 指定）の `expressions` を読む：
   - `parameters.items[]`：各パラメーターの `type`・`default`・`saved`・`synced`。`total_bits`／`limit`（256）。
   - `menu`：メニューの木と各メニューの項目数（8を超えると `MENU_TOO_MANY_CONTROLS`）。
   - `modular_avatar`：MA の Menu Installer・Menu Item・Parameters・Merge Animator の数。Menu Installer があれば、編集時にメニューが空でもビルド時に作られる。
2. `cct_animator_inspect` で、パラメーターがどこで使われるかを追う：

   ```jsonc
   {"target": "/Avatar", "layer": "all", "find_parameter": "VRCEmote", "timeout_seconds": 120}
   {"tool_name": "cct_animator_inspect", "parameters": {"target": "/Avatar", "layer": "all", "find_parameter": "VRCEmote", "timeout_seconds": 120}}
   ```

   `find.parameter` を読む：`declared_in`（どの Controller にどの型・既定値で宣言されているか）、`expression`（Expression Parameter の型・synced・saved・既定値）、`uses`（遷移の条件・BlendTree・speed/time・Parameter Driver の読み書き・Play Audio。各行に `controller`・`kind`・`at`）。名前は大文字小文字を区別する（綴りの揺れは `case_insensitive_matches`）。
3. 見た目を誰が書いているかを追う（「勝手に顔が変わる」）：`find_binding: "Body:blendShape.<シェイプ名>"` で、`find.binding.matches` にそのシェイプを書くクリップ（`clip`・`asset`）・定数値（`constant`・`values`）・使っているステート（`used_by`）が並ぶ。Tracking Control も書き込み元なので、`TRACKING_NOT_RESTORED` を見る。
4. `layers` の表で、レイヤーの重み・WD・同期を見る。`ma_merge_animators` で、MA が統合する Controller・レイヤーの種類・パスのモード・relative root・統合の順を見る。
5. 結論の分類：ここで得たものは「Observed（静的な構成）」。症状の再現（Reproduced）や修正の確認（Verified fix）にはならない。

読めないもの：統合後の Controller（ビルド前には無い）、実行時の値（current）、保存された値（LocalAvatarData）、Gesture Manager・エミュレーター・実機での挙動。Play 中で NDMF の Apply on Play が有効なら、シーンのアバターは処理済みで、`cct_animator_inspect` は統合後の Controller を見る（`source.stage: "play_mode"`、`PLAY_MODE`）。そのときは「Play 中の処理済みアバターを静的に読んだ」と書く。

`TRACKING_NOT_RESTORED`・`LOCOMOTION_NOT_RESTORED`・`ACTION_WEIGHT_NOT_LOWERED`・`POSE_SPACE_NOT_RESTORED` が warning になるのは、アバター全体を見たとき（`layer: "all"`、`layers` で絞らない、`include_ma` が有効、時間切れなし）だけ。部分的に見たときの info は、別の Controller が戻している可能性がある。`TRACKING_NOT_RESTORED` の目・口・指は、意図して Animation のままにする仕組みが多いので常に info。

## 6. ギミックの移植（vrchat-gimmick-packaging）

専門スキルの成功判定：①再利用プレハブに Missing Script と元シーンへの参照が無い ②参照が同梱範囲・明示した外部依存・Unity 組み込みのどれかに解決する ③導入先で MA/NDMF が正しく統合する ④書き出したアーカイブの全ファイル一覧が想定と一致する。

1. **①の前半（シーンの上）**：動いているシーンのギミックのルートに `scene_references`。

   ```jsonc
   {"mode": "scene_references", "target": "/Avatar/Gimmick"}
   {"tool_name": "cct_dependency_report", "parameters": {"mode": "scene_references", "target": "/Avatar/Gimmick"}}
   ```

   - `EXTERNAL_SCENE_REFERENCE`（error）：ギミックの外のシーンの物を指す参照。プレハブにすると None になる。内部の参照か Humanoid のボーン指定に替える。
   - `PATH_RESOLVED_REFERENCE`（info）：MA がパスで解決し直すので、多くは問題ない。
   - `PREFAB_HAS_OVERRIDES`：元の製品プレハブを書き出すと、このインスタンスの変更が落ちる。新しいプレハブとして保存する。
   - `MISSING_SCRIPT`・`MISSING_REFERENCE`・`SCENE_ONLY_OBJECT`。
2. 独立プレハブとして保存する（cct の外の操作。保存は破壊的操作の規律に従う）。
3. **①の後半と②**：プレハブかフォルダーに `dependencies`。

   ```jsonc
   {"mode": "dependencies", "assets": ["Assets/MyGimmick"], "export_root": "Assets/MyGimmick", "declared_packages": ["com.vrchat.*", "nadena.dev.*"]}
   {"tool_name": "cct_dependency_report", "parameters": {"mode": "dependencies", "assets": ["Assets/MyGimmick"], "export_root": "Assets/MyGimmick", "declared_packages": ["com.vrchat.*", "nadena.dev.*"]}}
   ```

   - `export_root` は存在するフォルダーにする（無いフォルダーは `INVALID_ARGUMENT` で失敗する）。
   - `OUTSIDE_EXPORT_ROOT`：書き出す範囲の外の依存（`assets_outside_root` にフォルダーごとの MB と参照元）。範囲に入れるか、外部依存として明示するかを決める。
   - `required_packages[]`：必要なパッケージと、この PC で入っている版（`version_installed`）。これは検証した版で、最低対応版ではない。
   - `MISSING_GUID`（ファイルと行と欄）、`ASMDEF_REQUIRED`、`LARGE_FILE`、`SCENE_IN_DEPENDENCIES`。
   - `dependency_list_path` の TSV は全依存の一覧（列は group・input・path・guid・type・bytes。group が `inside` の行が同梱、`outside` が範囲外の `Assets/`、`package:<名前>` が外部依存、`builtin` が Unity 組み込み、`include_scripts: false` で外したスクリプトは `excluded_script`）。④で、書き出したアーカイブの中身と突き合わせる想定一覧に使う。
   - `conclusive: false` なら途中まで（`SCAN_INCOMPLETE` が理由）。`timeout_seconds` を上げて呼び直すまで「欠落なし」としない。
4. コードからの名前・固定 GUID での参照（導入補助、`Shader.Find`、`Resources.Load`）は依存一覧に出ない。専門スキルの手順で別に確かめる。
5. **④**：書き出した `.unitypackage` の中身は cct では読めない。専門スキルの方法で中身の一覧を取り、TSV と突き合わせる。
6. **③（導入先）**：導入先のアバターで `cct_avatar_audit` を `post_build: true` で呼び（`MISSING_SCRIPT`、`post_build.ndmf.errors`、`expressions_after`）、`cct_animator_inspect` を `layer: "all"` で呼ぶ（`MA_*` と、導入先のルートから見た `BINDING_PATH_MISSING`）。これは NDMF だけの処理で、別プロジェクトへの実インポートや VRChat 実機の確認ではない。
7. 元のアセットを消す・置き換える前は `reverse` で使用元を確かめる（`TARGET_UNUSED` でも `conclusive` を見る）。

## 7. 手持ち小物の導入と握り位置（vrchat-hand-prop-setup）

専門スキルの決まり：Missing Script は子まで数え、`m_Script` の GUID を導入済みスクリプトと照合する。ビルド後の欠落0は依存がそろった証明ではない。Humanoid の指の割り当てを確かめる。合格の目視は、発光・パーティクルを止めた握り姿勢で、両手を近くから、正面で重なるなら側面からも。

1. 導入の前に `cct_snapshot create`（開いているシーンと、変更するプレハブ）。
2. 導入の後、`cct_avatar_audit`：
   - `MISSING_SCRIPT`（GameObject のパスと数）と `MISSING_SCRIPT_GUID`（保存済みの YAML の m_Script GUID と、`script_not_found`・`class_not_loadable`・`class_not_in_dll`・`unknown` の別）。`missing_scripts.guids[]` に GUID とファイル。script_not_found なら、その GUID のスクリプトを含むパッケージが入っていない。
   - `HUMANOID_OPTIONAL_BONES_MISSING`（指などの任意ボーン）。必要な指のアニメーションに効くかは専門スキルで判断する。
   - `post_build.missing_scripts_after` が0でも、上の判定の代わりにしない（NDMF は Missing Script を黙って消す。ツールの注記もそう書く）。
3. 製品のプレハブ自体の欠落を見るなら、`cct_dependency_report` の `dependencies` をそのプレハブに（`MISSING_GUID` のファイルと行）。
4. 握り位置の画像。発光・パーティクルを止める、握り姿勢にする、は cct の機能ではない（専門スキルの手順で用意する）。
   - 両手と側面：`{"target": "/Avatar", "views": "turnaround", "framing": "upper", "resolution": 1024}`。isolated はアバターのクローンを、今の姿勢を焼いて撮る（Play 中の握り姿勢も写る。Play 中に Read/Write が無効なメッシュは焼けない：`BAKE_FAILED`）。
   - 近接：`{"target": "/Avatar/.../Penlight", "mode": "scene", "views": "front,left,right"}`。構図は小物の範囲で決まり、scene はシーンをそのまま描くので、握っている手も写る。isolated で小物だけを対象にすると、手はクローンに入らず写らない。
   - `face` プリセットは頭部の構図。手元の確認には使わない。持ったときに表情が崩れるかを見るときだけ使う（原因の追跡は `vrchat-expression-debugging`）。
5. 位置を直した後は、同じ近接の撮影を `compare_with` で撮り、`compare_image` で前後を並べて見る。

## 8. 破壊的操作の前後（unity-mcp-operations 原則2）

破壊的操作＝削除、上書き、一括変更、インポート設定の変更、シーン・プレハブの保存。MCP の GameObject 削除は Undo に載らない。

1. 計画を示す（対象、操作、破壊的であること、戻し方＝スナップショット）。
2. 削除・置き換えなら、先に `cct_dependency_report` の `reverse` で使用元を確かめる。
3. `cct_snapshot create`：
   - `assets` に変わるファイル（フォルダー可）。開いているシーンは既定で入る。
   - 結果で確かめること：`file_count`、`scenes[].scene_state`（in_memory なら未保存の変更ごと写した）、`ASSET_HAS_UNSAVED_CHANGES`（ファイルに入っていない変更がある。保存は書き込みなので、ユーザーに確かめてから）、`SKIPPED_PACKAGE`、`incomplete`。
   - `SIZE_LIMIT` で止まったら、`details.largest` を見て範囲を絞るか、ユーザーに `max_total_mb` を上げてよいか聞く。
4. 操作する（一括なら1件で試し、ユーザーが目視してから残り）。
5. `cct_snapshot diff`（同じ id）：`counts` と `changes` で、変えるつもりのファイルだけが変わったかを見る。`SCENE_HAS_UNSAVED_CHANGES` は「シーンがまだ保存されていない」。diff はディスクのファイルを比べる。
6. 戻すと決まったら：
   1. `restore` を `confirm` なしで呼ぶ（ドライラン）。`would_overwrite`・`would_recreate`・`would_reimport`・`would_reload_scenes` と、メモリ上の未保存の変更が捨てられること、追加されたファイルは消えないこと、を見せる。
   2. ユーザーが同意したら `confirm: true`。`assets` で範囲を絞れる。
   3. 結果：`pre_restore_snapshot_id`（取り消し用）、`restored`・`recreated`・`failed`、`RESTORE_VERIFY_FAILED`・`SCENE_REOPEN_FAILED` の有無。`SCRIPTS_RESTORED` ならコンパイルとドメインリロードが続くので、MCP がつながり直すまで次を呼ばない。
   4. 同じ id で `diff` を呼び、戻した範囲が変更なしになったかを確かめる（`changes` には変わったファイルだけが載るので、戻したパスが `changes` に無く、`counts.modified`・`deleted`・`meta_changed` が想定どおり減ったことを見る）。`BLUEPRINT_ID_CHANGES` が出ていたら、アップロード先が変わっていないかをユーザーと確かめる。
7. restore の取り消しは `pre_restore_snapshot_id` を restore する（これもドライランから）。restore が作り直したファイルは消えない（`RECREATED_FILES_REMAIN`）。
8. 不要になったスナップショットは `list` で見せ、同意を得て `delete`（ドライラン → `confirm: true`）。

## 9. ワールドの点検とギミック（vrchat-world-creation）

1. `cct_environment`：`project_kind` が `world` か `avatar+world` であること、`world_descriptors`。
2. `cct_world_audit`：対象のプラットフォームを決めて呼ぶ（PC だけなら `platform: "pc"`）。大きいワールドは `timeout_seconds: 120`。
   1. `verdict` を先に読む：`build_blockers`（SDK・UdonSharp が実際に止める）→ `upload_blockers` → `other_errors`（止まらないが壊れる）。
   2. 次に warning。`ANDROID_*` はモバイルにも上げる場合だけ。`NOT_WHITELISTED` は SDK が検査しない推定（クライアントが消す）。
   3. `NOT_WHITELISTED` を直す前に、重大度とレポートの `components.not_whitelisted[].reason` を見る。`stripped_at_build`（iwaSync3 など、ビルド時にパッケージ自身が消す）は info で対応不要。`scene_processor_may_strip` は推定なので、消えるかどうかはそのパッケージの説明で確かめる。`not_in_allowlist` だけが warning。EditorOnly タグを付けるのは、issue の文がそう勧めているときだけ（`shipping_component_in_subtree` がある物に付けると、子の Udon・AudioSource ごとビルドから消える）。`tool_version` が 0.1.0 なら iwaSync3 の4種と `VIDEO_PLAYER_NO_AUDIO` は誤検知で、EditorOnly の助言に従わない。
   4. 0.1.1 で条件を絞ったもの（`MULTIPLE_SCENES_LOADED`＝info なら空のシーンだけ、`UI_NO_EVENT_SYSTEM`、`STATION_NO_COLLIDER`、`PICKUP_NOT_SYNCED`、`VIDEO_PLAYER_NO_AUDIO`、`LIGHTMAP_UV_MISSING`）は、出ていても直す前に文面の条件を読む。出なかった理由はレポートの `interactables.stations.no_collider_but_used_from_other_udon`、`interactables.pickups.not_synced_udon_controlled`、`interactables.video_players.items[].audio_sources_at_build`、`lighting.lightmaps.uv0_fallback_ok` にある。
   5. PhysBone・Contact の数を報告するときは、どちらの数え方かを書く（`counts`・`interactables.dynamics.in_scope_shipped` はアップロードするシーンで EditorOnly を除く数、`interactables.dynamics.physbones` などは SDK と同じ読み込み中の全シーン）。
   6. コードと `vrchat-world-creation` の章の対応は、そのスキルの「負荷とプラットフォーム」12章（目安。正はこのスキルの [ツールの仕様](tool-reference.md)）。
   7. `not_checked` を報告に写す。
3. ギミックの取り込み：
   1. `cct_world_gimmicks` の `list`：`udonsharp_installed`、`vrchat_worlds_sdk_version`、`samples[].missing_requirements`、`importable`、`dest_problem`、`found_elsewhere`。
   2. `import` をドライランで（`only` に `importable` から選んだ名前）。`plan` と `program_assets_planned` を見せる。
   3. 同意を得て `confirm: true`。`files_written`・`files_skipped`・`program_assets_created`・`next_steps`。
   4. Unity のコンパイルを待つ（`EDITOR_BUSY` が出なくなるまで）。もう一度 `list` で `imported` と `program_asset` を確かめる。
   5. `<dest>/README.md` を読み、配置と配線を `vrchat-world-creation` のレシピどおりに行う（cct の外の操作）。
   6. もう一度 `cct_world_audit` で `UDON_*`・`USHARP_*`・`EDITOR_LOG_USHARP_ERRORS` を見る。
4. ライティングの前後：ベイクはユーザーに押してもらう（長い処理を1回の MCP 呼び出しでしない）。前後を `cct_capture_views` の `mode: "scene"` で撮り、`compare_with` で比べる。`isolated` はライトマップを外すので、ライティングの比較に使わない。家具や部屋を `target` にすると `front` はその物の +Z から見た画で、見た目の正面とは限らない（ワールドの椅子では背もたれ側が写った）。最初の撮影の `contact_sheet` で向きを確かめ、報告ではビューの名前でなく写っている側で書く。
5. 購入アセットや既存のギミックを消す・置き換える前は、`cct_dependency_report` の `reverse` と `cct_snapshot create`。

## 10. BPM・State の速度（vrchat-bpm-controls）

1. `cct_animator_inspect` を対象のレイヤーで呼ぶ（`layer: "action"` や `"fx"`、`layers` で絞る）。
2. `report_path` を Grep：State の名前（`"name": "<State名>"`、`-A 20`）で、`speed`・`speed_param`・`motion_time_param`・`motion` を読む。既定値のキーは省かれる：`speed` が無ければ speed 1、`speed_param`・`motion_time_param` が無ければその State ではパラメーターが無効（Inspector の Parameter のチェックが外れている）。
3. 2つの Controller を比べる機能は無い。それぞれで呼び、レポートの同じ State を見比べる。
4. 同期ビットの使用量は `cct_avatar_audit` の `expressions.parameters.total_bits`（ビルド前）と `post_build.expressions_after.total_bits`（NDMF 処理後）。
5. 実際の再生速度（観測した倍率）は静的解析では分からない。専門スキルの実行時の手順で測る。
