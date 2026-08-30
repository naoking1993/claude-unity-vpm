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
- 0.1.0: ねじれ推定は幅軸ベース。曲げのみのカードで 0°、90° ねじりカードで 83° / 同上 / 2026-08-30
- 0.1.0: 根元判定は「頭部中心に最も近い端」を採用しない（頭皮に沿う房で中間が最近点になり破綻した）。
  現行は ①Head系ウェイト支配の端 → ②1.3倍以上太い端 → ③高い端 の順 / 同上 / 2026-08-30
- 0.2.0: 自己テスト 55 項目すべて通過（0.1.0 の 22 項目 + 第2段の昇格候補指標 33 項目）。
  追加した合成データは「中央が膨らむ板」と「左右ミラー配置の 2 枚」 /
  出典: `--selftest` 実行（CPython 3.11.15 / numpy 2.4.6・Blender 外） / 2026-08-30
- 0.2.0: 中膨れ板（幅 = 0.02·(1+1.2·sin(πs))）で
  `width_profile_norm` = [1.0, 1.425, 1.635, 1.425, 1.0]、`width_bulge_ratio` = 1.635、
  `width_profile_guess` = mid_bulge。先細り板（幅 = 0.03·(1−0.7s)）では
  [1.0, 0.827, 0.685, 0.473, 0.37]、中膨れ率 1.0、taper_linear / 同上 / 2026-08-30
- 0.2.0: 左右ミラー配置の 2 枚（±0.05、頭幅 0.2）で `mirror.matched_fraction`=1.0、
  `root_spacing_norm.median`=0.5、`root_pitch_ratio`=0.3、
  `envelope.bbox_norm.x`=0.65、`bottom_z_norm`=−0.583、`widest_band`="eye" / 同上 / 2026-08-30
- 0.2.0: 根元だけ Head=1.0 の板で `root_lock_t`=0.2、`head_release_t`=0.95、
  `head_weight_profile`=[1.0, 0.727, 0.455, 0.273, 0.0]（設計どおり線形に落ちる） / 同上 / 2026-08-30
- 0.2.0: **numpy 非搭載環境でも `--selftest` が完走する**（0 failures / 11 skipped）。
  numpy が要るのは PCA を使う幅・厚み・断面だけで、レイヤー（radial）・エンベロープ・
  房ピッチ・ミラー・根元固定帯・UV 方向は numpy 無しでも算出できた /
  出典: numpy を import 不可にして `selftest()` を実行 / 2026-08-30
- Blender 実機での動作: **未実測**（0.1.0 から変わらず。原則0 の初回実測プロトコルは未実施）

## スクリプト挙動（compare_hair_json.py）

- 0.2.0: 先細り板と中膨れ板の JSON を比較して ★13 件。差は
  `width_profile_median[1..4]` / `width_profile_mode` / `width_profile_counts` /
  `width_bulge_ratio` / `root_tip_width_ratio` / `envelope.bbox_norm.x` に出た。
  0.1.0 の比較項目だけでは mid/tip 幅の数値差としてしか現れず、
  「板の形が違う」ことを名指しできなかった /
  出典: 合成 JSON 2 件での実行（Blender 外） / 2026-08-30

## 閾値校正（RULES）

- region_v1 / cross_section_v1 / grid_v1 / length_class_v1 の閾値: 未校正（初版の当て推量）
- 0.2.0 で追加した閾値も**すべて未校正**（初版の当て推量）。実測サンプルが 3 件たまるまでは
  判定ラベル（`*_guess`）より生の数値を信用すること:

  | 設定キー | 既定値 | 効くルール |
  |---|---|---|
  | `layer_bounds` | [1.05, 1.25] | layer_v1（scalp / mid / outer） |
  | `bulge_bounds` | [0.85, 1.15] | width_profile_v1（中膨れ / 中細り） |
  | `taper_bounds` | [0.8, 1.3] | width_profile_v1（先細り / 毛先広がり） |
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
- 0.2.0: `head_group_hints` は部分一致（`"head" in name`）なので、
  "Headband" のようなグループも Head 系として拾われ得る。実アセットでの誤検出率は未実測。
