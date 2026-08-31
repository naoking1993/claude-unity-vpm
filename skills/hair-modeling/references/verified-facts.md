# 検証済み実データ集（hair-modeling）

形式: 事実 / 出典 / 検証日。
記憶ベースの値をここに書かないこと。実測または一次情報のみ。
未確定の前提は末尾の「未検証の前提」に置き、実測で確定したら上の節へ移す。

---

## 環境

- Blender バージョン: 未実測
- 実行経路（GUI / Blender MCP 実装名・バージョン / background）: 未実測
- Blender 同梱 Python の numpy 有無: 未実測
- OS / ホスト: 未実測
- **スクリプトの自己テストを回した環境（Blender ではない）**: CPython 3.11.15 / numpy 2.4.6 /
  Linux コンテナ / 出典: `python3 inspect_hair.py --selftest` 実行 / 2026-08-30

## スクリプト挙動（inspect_hair.py）

- 0.1.0: 合成データ（4列×12行カード / 6角×10行筒 / 毛先分岐ストリップ / 退化島）の
  自己テストを通過。列数・行数・断面・region・UV方向・根元/毛先の支配グループ・毛先分岐数が
  期待どおり / 出典: `--selftest` 実行（Blender 外・numpy 2.4） / 2026-08-30
- 0.2.1: ねじれ推定は幅軸ベース。曲げのみのカード（4列×12行）で twist 0.0°・turn 16.2°、
  軸まわりに 90° ねじったカードで twist 81.8°・turn 0.0° /
  出典: `--selftest` の [twist] ケース（0.2.1 で追加。それ以前は自己テストにねじれたカードが無く、0.1.0 が記録していた 83° は再現できなかった） / 2026-08-31
- 0.1.0: 根元判定は「頭部中心に最も近い端」を採用しない（頭皮に沿う房で中間が最近点になり破綻した）。
  現行は ①Head系ウェイト支配の端 → ②1.3倍以上太い端 → ③高い端 の順 / 同上 / 2026-08-30
- 0.2.1: 自己テスト 91 項目すべて通過。追加した合成データは「中央が膨らむ板」「左右ミラー配置の 2 枚」
  「軸まわりに 90° ねじった板」と、判定ルールを関数レベルで直接検査する回帰テスト /
  出典: `--selftest` 実行（CPython 3.11.15 / numpy 2.4.6・Blender 外） / 2026-08-31
- 0.2.1: 中膨れ板（幅 = 0.02·(1+1.2·sin(πs))、4列×12行）で
  `width_profile_norm` = [1.0, 1.425, 1.635, 1.425, 1.0]、`width_bulge_ratio` = 1.635、
  `width_profile_guess` = mid_bulge。先細り板（幅 = 0.03·(1−0.7s)）では
  **[1.0, 0.809, 0.682, 0.491, 0.363]**、`width_bulge_ratio` 0.682、`width_waist_ratio` 1.851、
  taper_linear / 同上 / 2026-08-31
  ※ 0.2.0 はここに [1.0, 0.827, 0.685, 0.473, 0.37] と書いていたが、
  これは板単体ではなく **カード＋筒の集計中央値**（`aggregates.width_profile_median`）で、
  房単体の値として引用したのは誤りだった。集計値と個体値を取り違えないこと
- 0.2.1: 左右ミラー配置の 2 枚（±0.05、頭幅 0.2）で `mirror.matched_fraction`=1.0、
  `root_spacing_norm.median`=0.5、`root_pitch_ratio`=0.3、
  `envelope.bbox_norm.x`=0.65、`bottom_z_norm`=−0.583、`widest_band`="eye" / 同上 / 2026-08-31
- 0.2.1: 根元だけ Head=1.0 の板で `root_lock_t`=0.2、`head_release_t`=0.95、
  `head_weight_profile`=[1.0, 0.727, 0.455, 0.273, 0.0]（設計どおり線形に落ちる） / 同上 / 2026-08-31
- 0.2.1: **numpy 非搭載環境でも `--selftest` が完走する**（0 failures / 14 skipped）。
  numpy が要るのは PCA を使う幅・厚み・断面。numpy 無しでも出るのは
  レイヤー（`*_radial_h`）・エンベロープ・房ピッチ `root_spacing_norm`・ミラー・
  根元固定帯・UV 方向 /
  出典: numpy を import 不可にして `selftest()` を実行 / 2026-08-31
  ※ ただし `root_pitch_ratio` は「根元幅中央値 ÷ ピッチ中央値」で根元幅が PCA 依存のため
  numpy 無しでは None になる。0.2.0 はこれを「房ピッチは numpy 無しでも算出できた」と
  一括りに書いていたが、比の方は出ない
- Blender 実機での動作: **未実測**（0.1.0 から変わらず。原則0 の初回実測プロトコルは未実施）

## スクリプト挙動（compare_hair_json.py）

- 0.2.1: 先細り板（幅 0.03·(1−0.7s)）と中膨れ板（幅 0.02·(1+1.2·sin(πs))）を
  同一の頭部基準（half=(0.1,0.1,0.12)）で解析した JSON 同士の比較で ★14 件 / 未測定 5 件。
  差は `width_profile_median[1..4]` / `width_profile_mode` / `width_profile_counts` /
  `width_bulge_ratio` / `width_waist_ratio` / `root_tip_width_ratio` / `envelope.bbox_norm.x`。
  0.1.0 の比較項目だけでは mid/tip 幅の数値差としてしか現れず、
  「板の形が違う」ことを名指しできなかった /
  出典: 上記 2 つの合成メッシュから生成した JSON での実行（Blender 外） / 2026-08-31
  ※ 房が 1 本ずつなので `root_spacing_norm` と `mirror.matched_fraction` は未測定になる（正常）

## 閾値校正（RULES）

- region_v1 / cross_section_v1 / grid_v1 / length_class_v1 の閾値: 未校正（初版の当て推量）
- 0.2.0 で追加した閾値も**すべて未校正**（初版の当て推量）。実測サンプルが 3 件たまるまでは
  判定ラベル（`*_guess`）より生の数値を信用すること:

  | 設定キー | 既定値 | 効くルール |
  |---|---|---|
  | `layer_bounds` | [1.05, 1.25] | layer_v2（scalp / mid / outer。**水平半径**に対する閾値） |
  | `layer_below_z` | -1.0 | layer_v2（これ未満の z_norm は below_head＝層を定義しない） |
  | `bulge_bounds` | [0.9, 1.1] | width_profile_v2（中細り / 中膨れ。**中央 vs 両端**の比） |
  | `taper_bounds` | [0.8, 1.3] | width_profile_v2（先細り / 毛先広がり） |
  | `mirror_tol_norm` / `mirror_length_tol` / `mirror_midline_norm` | 0.06 / 0.15 / 0.08 | mirror_v1 |
  | `root_lock_weight` / `root_lock_fraction` / `weight_bins` | 0.99 / 0.9 / 20 | root_lock_v1 |
  | `ENVELOPE_BANDS` の z_norm 境界 | 1.2 / 0.6 / 0 / −0.6 / −1.2 / −2.0 / −3.0 | envelope_v1 |

## アバター別の頭部基準・前方向

| アバター | head_object / head_vertex_group | front_axis | 備考 | 検証日 |
|---|---|---|---|---|
| （未記録） | | | | |

## FBX インポートで残るもの / 消えるもの

- 未実測

## 既知エラーと対処

- **0.1.0 からの性能バグ（0.2.1 で修正）**: `analyze_island` が島ごとにメッシュ全体の
  `edge_set` / `boundary_edge_set` を走査しており、計算量が Θ(島数 × 辺数) だった。
  実測（4列×12行のカードを並べた合成メッシュ）: 修正前は房 25/50/100/200 で 0.06/0.10/0.27/0.64 秒
  （房数 2 倍で約 2.4 倍＝超線形）。修正後は 0.04/0.09/0.19/0.39 秒で、
  房 400 → 0.79 秒、房 800 → 1.78 秒と線形。
  対処: `analyze_mesh` で辺を 1 パス島ごとに振り分けて渡す /
  出典: 合成メッシュでのベンチ / 2026-08-31
- **0.2.0 のバグ（0.2.1 で修正）**: Head 系グループ名を生の部分一致（`"head" in name`）で
  判定していたため、髪の PhysBone（HeadHair_01 / Hair_Head_02）や Headband まで Head 系になった。
  房全体が頭部固定に見えて `root_lock_t` が 1.0 に飽和し、
  「頭に固定された髪」という正反対の読みになる。
  対処: 語単位の一致＋髪系トークンの除外（`head_group_v2`）/ 出典: 回帰テスト / 2026-08-31
- **0.2.0 のバグ（0.2.1 で修正）**: 頭部基準が `hair_union_bbox_fallback` のとき、
  髪は自分の bbox をちょうど埋めるので `bbox_norm.x`=1.0 / `top_z_norm`=+1 /
  `bottom_z_norm`=-1 / `widest_band`="crown" が**形状によらず必ずその値になる**。
  実測: 縦長・横長・立方の 3 形状すべてで同じ値。
  「同一設定同士なら比較可」という警告のままでは、この定数が転記される。
  対処: 該当項目を None にし、`degenerate_under_fallback` と縦横比
  （`bbox_aspect_zx` / `bbox_aspect_yx`）だけを残す / 出典: 3 形状での再現 / 2026-08-31
- **0.2.0 の判定バグ（0.2.1 で修正・最重要）**: `width_profile_v1` の中膨れ率を
  w50/((w0+w100)/2)（両端の *平均* との比）で定義していた。これは中膨れではなく
  **テーパ曲線の凸性**なので、単調に細るだけの板が誤分類された。実測:
  幅 1.00→0.30→0.10 の板が 0.545 で `waisted`、1.00→0.90→0.10 の板が 1.636 で `mid_bulge`。
  対処: 中膨れは w50/max(w0,w100)、中細りは w50/min(w0,w100) で判定する（`width_profile_v2`）。
  凸性は `width_curvature_ratio` として別に出す /
  出典: `width_profile_guess` を 7 通りの幅で直接呼んだ再現。回帰テストを `--selftest` に追加 / 2026-08-31
- **0.2.0 の判定バグ（0.2.1 で修正・最重要）**: `layer_v1` が 3D 半径 `mid_radial` で層を決めていた。
  3D 半径には z 成分が入るため、**頭皮に密着した長い房のほうが外側に膨らんだ短い房より
  「外側」と判定され、層の順序が反転した**。実測（頭 half=(0.1,0.1,0.12)）:
  頭皮沿いで下に垂れた房の中点 (0.09, 0, −0.18) が radial 1.749 → outer、
  実際に外へ膨らんだ房 (0.14, 0, 0) が 1.400 → outer で同格。
  対処: 層は水平半径 `mid_radial_h`=sqrt(x_n²+y_n²) で測り、頭部より下（z_norm<−1.0）の房は
  `below_head` として層の判定から外す（`layer_v2`）/ 出典: 同上 / 2026-08-31
- **0.2.0 のバグ（0.2.1 で修正）**: エンベロープを対象メッシュの全頂点で計算していたため、
  面に属さない孤立頂点 1 個で bbox と top/bottom_z_norm が決まってしまった。
  自己テストの合成データ（孤立頂点 1 個を含む）で
  bbox x/y/z = 1.575/2.2/1.85・top_z_norm = 2.5 → 修正後 0.215/1.21/0.75・0.667。
  対処: `mesh_surface_points()` で面に属する頂点だけを渡し、除外数を warnings に出す /
  出典: `--selftest` の [multi] ケース。回帰テストを追加 / 2026-08-31
- **0.2.0 のバグ（0.2.1 で修正）**: `_head_group_indices` はヒントの**部分一致**、
  `_choose_root_end` は**完全一致 or "head" を含む**で Head 系グループを判定しており、
  基準がずれていた。頂点グループ「後頭部」で前者だけが Head 系と判定し、
  根元の判定と根元固定帯の測定が別々の端を見る可能性があった。
  対処: `is_head_group_name()` に一本化（完全一致 or "head" を含む、の厳しい方に統一）/
  出典: 「後頭部」を含む group_names での再現 / 2026-08-31
- **0.2.0 のバグ（0.2.1 で修正）**: compare_hair_json が「片方の JSON にキーが無い」を
  「0 件 vs N 件」として扱い ★ を付けていた。**同じメッシュの 0.1.0 JSON と 0.2.0 JSON を
  比較するだけで偽のレシピ欠落が並ぶ**。逆に aggregates が丸ごと空でも ★3 件しか出ず、
  最大の欠落が最も静かに通っていた。
  対処: ブロック不在は「未測定」として数え、フッタに未測定件数と script_version 不一致を出す。
  実測: 同一メッシュの旧新比較が ★2 → ★0/未測定 50、空 aggregates が ★3 → 未測定 52 /
  出典: 合成 JSON での再現 / 2026-08-31
- **0.2.0 のバグ（0.2.1 で修正）**: `compare_hair_json.py a.json b.json --md` のように
  `--md` を末尾に置くと IndexError。また `--md` の値が位置引数に混ざり、
  `--md out.md a.json b.json` の順だと out.md を入力として読もうとした。
  対処: 引数を順に走査して `--md` の値を取り除く / 出典: 同上 / 2026-08-31
- **0.2.0 のバグ（0.2.1 で修正）**: `envelope_min_band_points` を 0 にすると
  空の帯に `_stats([])`（count だけの dict）が渡り KeyError('median')。
  また対象メッシュの頂点が 0 のとき、頭部 bbox フォールバックが
  `min() arg is an empty sequence` で落ちた。対処: それぞれ下限 1 と早期リターンを追加 /
  出典: 直接呼び出しでの再現 / 2026-08-31
- **0.1.0 の持ち越しバグ（0.2.0 で修正）**: numpy が無い環境で UV のある房を解析すると、
  `analyze_island` が `along` / `tip_dir` キーを作らないまま返し、
  `analyze_mesh` の UV サマリーで `KeyError: 'along'` になった。
  原則0 の手順2（`--selftest` で numpy の有無を記録する）はまさに numpy 非搭載環境で走り得るので実害がある。
  対処: ピアソン相関を numpy 非依存の `_corr()` に置き換え、numpy 分岐そのものを廃止 /
  出典: numpy を import 不可にした再現 / 2026-08-30
- **0.1.0 の持ち越しバグ（0.2.0 で修正）**: `--selftest` 自体が numpy 前提で、
  幅の検査 `abs(s["width_norm"]["t0"] - 0.15)` が None との減算になり TypeError で止まった。
  対処: `check_np()` を追加し、PCA 依存の 11 項目は評価せず SKIP 表示にして完走させる /
  出典: 同上 / 2026-08-30
- 0.2.0 開発中: `head_weight_profile` を t の単一帯から取ると、行数の少ないメッシュ
  （12行×4列のカード）では狙った帯が空になり None が混ざり、
  `_profile_median` が全行を捨てて None を返した。
  対処: 近い非空帯を外側に探して埋める実装に変更 /
  出典: `--selftest` の再現 / 2026-08-30
- 0.2.0 開発中: Head 系ウェイトが 1 つも届かない房を混ぜると `root_lock_t` の中央値が
  0 に引っ張られ、「根元固定していない髪」と読み違える。
  対処: `head_influenced`（Head ウェイト最大値 ≥0.5）で分離し、
  集計は届いている房だけ、割合は `head_influenced_fraction` で別途出す /
  出典: 合成データ（カード＋筒）での再現 / 2026-08-30

---

## 未検証の前提（実測で確定させること）

- 前方向は `-Y`（Blender 標準でキャラが -Y を向く）と仮定。アバターのインポート設定で変わり得る。
- Blender 4.1 以降は `Mesh.corner_normals`、それ以前は `calc_normals_split()` を使う分岐を入れている。
  どちらの経路が通るかは未実測。
- FBX にはカスタム法線・ウェイト・シェイプキーが残り、モディファイア・カーブは残らない
  （一般的知識。未実測）。
- `Object.to_mesh(preserve_all_data_layers=True, depsgraph=...)` で評価後メッシュの頂点グループ・
  UV が保持される（API ドキュメント上の仕様。未実測）。
- Head ウェイト ≥0.5 の頂点 bbox を頭部基準にする方式で、首の混入がどの程度出るかは未実測
  （縦横比 z/x が 0.6〜1.6 を外れると警告が出る）。
- 0.2.0: 房ピッチとミラー判定は房数の総当たり（O(n²)）。`max_pairwise_strands`=2000 を超えると
  省略して `spacing_skipped_reason` を出す。実アセットの房数で何秒かかるかは未実測。
- 0.2.0: エンベロープは対象メッシュの**全頂点**（退化島も含む）で計算する。
  髪以外のオブジェクトを targets に混ぜると silhouette が狂う。実アセットでの影響は未実測。
- 0.2.1: Head 系グループ名の判定（`head_group_v2`）は「ヒント完全一致」「除外トークン
  (hair/髪/kami/ahoge) を語として含めば非 Head」「head またはヒントを *語として* 含む」。
  実測: Head / J_Bip_C_Head / 頭 / HEAD → Head 系、
  HeadHair_01 / Hair_Head_02 / Headband / Hair_01 / 髪_01 / 後頭部 → 非 Head 系 /
  出典: `--selftest` の回帰テスト / 2026-08-31。
  実アセットの命名（特に "head" を語として含む髪ボーン）での誤検出率は未実測。
- 0.2.1: **Mirror モディファイアが未適用のまま `use_evaluated=False`（既定）で測ると、
  片側の形状しか見ていない。** mirror・envelope・房数・房ピッチが実物と一致しないので、
  0.2.1 で警告を出すようにした。VRChat 向けヘアでの Mirror 使用率は未実測。
- 0.2.1: `hair_union_bbox_fallback` という文字列は `warnings` の本文には現れず、
  `head.source` と `aggregates.envelope.head_source` にだけ入る。
  代用かどうかはこの 2 つで判定すること（警告文は日本語の散文）。
- 0.2.1: 房ピッチとミラーの O(房数²) 2 重ループが実アセットの房数（数百〜千）で
  Blender 内で何秒かかるかは未実測。`max_pairwise_strands`=2000 の妥当性も未検証。
