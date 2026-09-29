# サンプル解析: {サンプル名}

<!-- このファイルは第1段の出力。数値は inspect_hair.py の JSON からのみ転記する。
     表記規則: 実測=値＋JSONキー / 推定=値＋ルール名＋根拠 / 一般則候補=「仮説(N=1)」
     対応スクリプト: inspect_hair.py 0.2.1 -->

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
- 長さ（頭幅比）: `length_geo_norm`（中央値・範囲）、直線度 `straightness`
- 曲がり / ねじれ: `turn_total_deg` / `twist_total_deg`（twist_v1）
- 毛先分岐率: `tip_split_fraction`
- 根元の判定に使われたルール: `strands[].root_rule_used` の内訳（head_weight が多数なら根元判定は信頼できる）

### 2b. 幅プロファイル（板の中膨れ。width_profile_v1）

taper 値だけでは板の形は決まらないので、**5 点のプロファイルをそのまま転記する。**

| 項目 | 値 | JSON キー |
|---|---|---|
| 幅プロファイル中央値 [t0,t25,t50,t75,t100]（根元=1.0） | | `aggregates.width_profile_median` |
| 中膨れ率 w50/max(w0,w100)（>1.1 で中膨れ） | | `width_bulge_ratio`（中央値・範囲） |
| 中細り率 w50/min(w0,w100)（<0.9 で中細り） | | `width_waist_ratio` |
| テーパ曲線の凸性 w50/((w0+w100)/2) | | `width_curvature_ratio`（中膨れの判定には使わない） |
| 形の内訳 / 最頻 | | `width_profile_counts` / `width_profile_mode` |
| 根元/毛先幅比（taper） | | `root_tip_width_ratio` |
| 幅の絶対値（頭幅比） root / mid / tip | | `root_width_norm` / `mid_width_norm` / `tip_width_norm` |

## 3. 領域分類（推定 region_v1）

| 領域 | 房数 | 長さ中央値 | 根元幅中央値 | taper | 中膨れ率 | turn° | twist° | ピッチ | 備考 |
|---|---|---|---|---|---|---|---|---|---|
| bangs | | | | | | | | | |
| side_+X / side_-X | | | | | | | | | |
| back | | | | | | | | | |
| top / ahoge | | | | | | | | | |
| unclassified | | | | | | | | | 目視で再分類した結果を書く |

- 目視との食い違い: （あれば、閾値校正の材料として verified-facts.md にも書く）
- 長さ系統: `length_class_guess`（tip_z_norm_min={値}）

### 3b. レイヤー（radial 分位点。layer_v2）

**層は水平半径 `*_radial_h`（頭皮面=1.0）で測る。** 3D 半径には z が入るので、
頭皮に密着した長い房が外側レイヤーに化ける。3D 半径も出力されるが層の判定には使わない。
**p10–p90 の幅が「何層重ねているか」の代理値。**

| 項目 | p10 | median | p90 | JSON キー |
|---|---|---|---|---|
| root_radial_h（水平・層の判定用） | | | | `aggregates.root_radial_h` |
| mid_radial_h（水平・層の判定用） | | | | `aggregates.mid_radial_h` |
| tip_radial_h（水平・層の判定用） | | | | `aggregates.tip_radial_h` |
| mid_radial（3D・参考） | | | | `aggregates.mid_radial` |

- レイヤー内訳（scalp / mid / outer / below_head）: `aggregates.layer_counts`
- 領域別の mid_radial_h 中央値: `by_region[].mid_radial_h.median`
- `below_head` は頭部より下まで垂れて層が定義できない房。**層の議論から除くこと**（房数は書く）

## 4. 全体構成（実測: エンベロープ・房ピッチ・対称性）

### 4a. エンベロープ（envelope_v1）

| 項目 | 値 | JSON キー |
|---|---|---|
| bbox x / y / z（頭幅=1） | | `envelope.bbox_norm` |
| 中心オフセット | | `envelope.center_offset_norm` |
| 最大張り出し帯 / その r_p90 | | `envelope.widest_band` / `silhouette_ratio` |
| 頭部基準の出所 | | `envelope.head_source` / `degenerate_under_fallback` |
| 縦横比（代用時はこれだけ書ける） | | `envelope.bbox_aspect_zx` / `bbox_aspect_yx` |
| 上端 / 下端 z_norm | | `envelope.top_z_norm` / `bottom_z_norm` |

帯別の水平半径（`envelope.horiz_radius_by_z`。1.0 = 頭皮面）:

| 帯 | 頂点比 | r_p50 | r_p90 | r_max |
|---|---|---|---|---|
| crown | | | | |
| upper | | | | |
| eye | | | | |
| jaw | | | | |
| neck | | | | |
| shoulder | | | | |

- `length_class_guess` と `widest_band` が食い違ったか: （食い違いは分類ルールの校正材料。両方書く）

### 4b. 房ピッチ（spacing_v1）／対称性（mirror_v1）

| 項目 | 値 | JSON キー |
|---|---|---|
| 房ピッチ中央値（頭幅比） | | `aggregates.root_spacing_norm.median` |
| 根元幅/ピッチ（>1 で重なる） | | `aggregates.root_pitch_ratio` |
| ミラー一致率 / 対象房数 | | `mirror.matched_fraction` / `mirror.considered` |
| 正中房の数 | | `mirror.midline_strand_count` |
| ミラー軸 | | `mirror.axis` |

- 房ごとの相手: `strands[].mirror_role`（matched / unmatched / midline）と `mirror_partner_id`
- **Mirror モディファイアが未適用のまま `use_evaluated=False` で測ると、片側しか見ていないので
  この節と 4a の数値は実物と一致しない。** 警告が出ていないか確認し、出ていたら
  `use_evaluated=True` で測り直す

## 5. UV 規約（実測）

- 長手方向の UV 軸 / 毛先の向き: `uv[].along_mode` / `tip_dir_mode`（uv_direction_v1）
- 占有率 / 共有ペア数: `uv[].coverage_est` / `shared_bbox_pairs`
- テクスチャの使い方（観察）: 

## 6. ボーン / ウェイト（実測）

- 1房あたりボーン数 / 1頂点あたりボーン数: `weights.groups_per_strand` / `groups_per_vertex_mean`
- 根元の支配グループ / 毛先の支配グループ: `weights.root_dominant_mode`（`root_dominant_fraction`）/ `tip_dominant_mode`
- ボーン階層: `bones[].matched_bones` / `chain_roots` / `chain_root_parent_mode` / `max_chain_depth` / `depth_hist`

### 6b. 根元固定帯（root_lock_v1）

| 項目 | 値 | JSON キー |
|---|---|---|
| 検出した Head 系グループ | | `weights.head_groups_detected` |
| Head が届いている房の割合 | | `weights.head_influenced_fraction` |
| 根元固定帯 root_lock_t（中央値・範囲） | | `weights.root_lock_t` |
| Head 影響が消える head_release_t | | `weights.head_release_t` |
| Head ウェイトの落ち方 [t0,t25,t50,t75,t100] | | `weights.head_weight_profile_median` |

- 落ち方の形（線形 / 段 / 急落）の観察: 

## 7. 法線（実測）

- 放射方向との角度: geo `normals.geo_radial_mean_deg` / custom `custom_radial_mean_deg`
- custom と geo の差: `custom_vs_geo_mean_deg`
- 判断: 球体転写らしい / 幾何法線のまま / 判定不能（根拠の数値を添える）

## 8. 構築法の推定

| 仮説 | 支持するデータ | 矛盾するデータ | 確度 |
|---|---|---|---|
| 例: 平面カードをカーブに沿わせて配置 | flat_card 100%、grid_fit 0.9、列数 4 固定 | ねじれが大きい房がある | 中 |

## 9. 再現レシピ（patterns.md の語彙で。特定商品の頂点配置はなぞらない）

1. 
2. 
3. 

bpy 骨子のパラメータ（房数・幅・長さ・曲がりを引数化）:
```python
PARAMS = {
    "regions": {"bangs": {"count": 0, "length_norm": (0.0, 0.0), "root_width_norm": 0.0,
                          "mid_radial_h": (0.0, 0.0)}},
    "cross_section": "flat_card", "columns": 4, "rows": 12,
    # 幅は taper 1 値ではなく 5 点プロファイルで持つ（板の中膨れを落とさないため）
    "width_profile": (1.0, 0.0, 0.0, 0.0, 0.0),   # [t0,t25,t50,t75,t100] 根元=1.0
    # taper 1 値では板の形が決まらない。中膨れかどうかは中央と「両端のどちらか」の比で決まる
    "turn_deg": (0, 0), "twist_deg": (0, 0),
    "spacing": {"root_pitch_norm": 0.0, "root_pitch_ratio": 0.0},
    "mirror": {"enabled": False, "midline_count": 0},
    "envelope": {"widest_band": None, "silhouette_ratio": 0.0, "bottom_z_norm": 0.0},
    "uv": {"along": "V", "tip_dir": "-V"},
    "weights": {"root": "Head", "root_lock_t": 0.0, "release_t": 0.0, "chain_depth": 0},
}
```

## 10. 一般則候補（3件以内・すべて仮説(N=1)）

1. 仮説(N=1): 
2. 仮説(N=1): 
3. 仮説(N=1): 

既存 patterns.md との矛盾: 

第2段への申し送り（patterns.md に受け皿が無かった項目）: 
<!-- 受け皿が無くて書けなかった項目は、patterns.md の「第2段の昇格候補台帳」に積む。
     ここに書いただけでは次回に残らない。 -->

## 11. 往復検証の記録（第3段で追記）

- 再生成 JSON: 
- compare_hair_json ★項目とレシピへの反映: 
