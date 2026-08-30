# サンプル解析: {サンプル名}

<!-- このファイルは第1段の出力。数値は inspect_hair.py の JSON からのみ転記する。
     表記規則: 実測=値＋JSONキー / 推定=値＋ルール名＋根拠 / 一般則候補=「仮説(N=1)」 -->

## 0. 前提（実測）

| 項目 | 値 |
|---|---|
| 解析日 / script_version / Blender | {YYYY-MM-DD} / {meta.script_version} / {meta.blender_version} |
| 対象オブジェクト | {objects[].name} |
| 出所 | FBX インポート / .blend 付属 / 自作（どれか） |
| 基準アバター / 頭部基準 | {アバター名} / {head.source}（width={head.width}） |
| front_axis / geometry_source | {meta.config.front_axis} / base or evaluated |
| JSON パス | {json} |
| 警告 | {warnings}（fallback があれば正規化値は比較不可と明記） |

## 1. 構造（実測）

- オブジェクト数・親子・コレクション: 
- 頂点/辺/面、面種別: `objects[].verts/faces/face_types`
- マテリアルスロット / UV マップ: `objects[].material_slots` / `uv_layers`
- 頂点グループ数 / シェイプキー: `objects[].vertex_groups` / `shape_keys`
- モディファイア（種類と主要設定）: `objects[].modifiers`（FBX 由来なら「無し＝情報欠落」と書く）
- カスタム法線 base / evaluated: `objects[].custom_normals_base` / `custom_normals_evaluated`
- 命名規則（観察）: 

## 2. 房の分解（実測＋推定）

- 房数（島数）: `aggregates.strand_count`（退化島 `degenerate_islands`）
- 1房あたり頂点数: `verts_per_strand`（中央値・範囲）
- 断面: `cross_section_counts`（推定 cross_section_v1）
- 列数 / 行数: `columns_estimate_mode` / `rows_estimate`（推定 grid_v1、`grid_fit_fraction`）
- 幅プロファイル（頭幅比）: root `root_width_norm` / mid `mid_width_norm` / tip `tip_width_norm`、
  根元/毛先比 `root_tip_width_ratio`
- 長さ（頭幅比）: `length_geo_norm`（中央値・範囲）、直線度 `straightness`（straightness_v2）
- 曲がり: `turn_total_deg` / `turn_net_deg` / `turn_per_length_deg`（turn_v2）。
  turn≒net なら C字、turn≫net なら S字・波打ち。`turn_measured_fraction` が 1.0 未満なら
  中心線が引けなかった房がある（その房は turn を持たない＝母集団が違う）。
  `centerline_node_count` の中央値が 8 なら断面数が少ない粗い房で、turn は過小に出ている
- ねじれ: `twist_total_deg`（twist_v1。**面内曲がりが混入する未検証値。数値で語らない**）
- 毛先分岐率: `tip_split_fraction`
- 根元の判定に使われたルール: `strands[].root_rule_used` の内訳（head_weight が多数なら根元判定は信頼できる）

## 3. 領域分類（推定 region_v1）

| 領域 | 房数 | 長さ中央値 | 根元幅中央値 | 備考 |
|---|---|---|---|---|
| bangs | | | | |
| side_+X / side_-X | | | | |
| back | | | | |
| top / ahoge | | | | |
| unclassified | | | | 目視で再分類した結果を書く |

- 目視との食い違い: （あれば、閾値校正の材料として verified-facts.md にも書く）
- レイヤー: `root_radial` / `mid_radial`（1.0=頭部楕円面。mid が大きいほど外側レイヤー）
- 長さ系統: `length_class_guess`（tip_z_norm_min={値}）

## 4. UV 規約（実測）

- 長手方向の UV 軸 / 毛先の向き: `uv[].along_mode` / `tip_dir_mode`（uv_direction_v1）
- 占有率 / 共有ペア数: `uv[].coverage_est` / `shared_bbox_pairs`
- テクスチャの使い方（観察）: 

## 5. ボーン / ウェイト（実測）

- 1房あたりボーン数 / 1頂点あたりボーン数: `weights.groups_per_strand` / `groups_per_vertex_mean`
- 根元の支配グループ / 毛先の支配グループ: `weights.root_dominant_mode`（`root_dominant_fraction`）/ `tip_dominant_mode`
- ボーン階層: `bones[].matched_bones` / `chain_roots` / `chain_root_parent_mode` / `max_chain_depth` / `depth_hist`
- 根元固定の作り方（観察）: 

## 6. 法線（実測）

- 放射方向との角度: geo `normals.geo_radial_mean_deg` / custom `custom_radial_mean_deg`
- custom と geo の差: `custom_vs_geo_mean_deg`
- 判断: 球体転写らしい / 幾何法線のまま / 判定不能（根拠の数値を添える）

## 7. 構築法の推定

| 仮説 | 支持するデータ | 矛盾するデータ | 確度 |
|---|---|---|---|
| 例: 平面カードをカーブに沿わせて配置 | flat_card 100%、grid_fit 0.9、列数 4 固定 | ねじれが大きい房がある | 中 |

## 8. 再現レシピ（patterns.md の語彙で。特定商品の頂点配置はなぞらない）

1. 
2. 
3. 

bpy 骨子のパラメータ（房数・幅・長さ・曲がりを引数化）:
```python
PARAMS = {
    "regions": {"bangs": {"count": 0, "length_norm": (0.0, 0.0), "root_width_norm": 0.0}},
    "cross_section": "flat_card", "columns": 4, "rows": 12,
    "taper_ratio": 0.0, "turn_deg": (0, 0),  # turn_v2 の total（範囲）
    "uv": {"along": "V", "tip_dir": "-V"},
    "weights": {"root": "Head", "chain_depth": 0},
}
```

## 9. 一般則候補（3件以内・すべて仮説(N=1)）

1. 仮説(N=1): 
2. 仮説(N=1): 
3. 仮説(N=1): 

既存 patterns.md との矛盾: 

## 10. 往復検証の記録（第3段で追記）

- 再生成 JSON: 
- compare_hair_json ★項目とレシピへの反映: 
