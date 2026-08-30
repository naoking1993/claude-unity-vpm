#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
compare_hair_json.py — inspect_hair.py の出力 JSON 2 つ（元アセット vs 再生成）を比較する（第3段 往復検証用）

使い方: python compare_hair_json.py original.json regenerated.json [--md out.md]

出力: 集計値の並置表（Markdown）。差が大きい行に ★ を付ける。
      「差が大きい」の閾値は相対差 25%（数値）/ 不一致（カテゴリ）。閾値は校正対象。
      ★ はレシピの欠落候補＝samples/<名前>.md に追記すべき項目。
Blender 不要。

0.2.0: inspect_hair 0.2.0 が出す第2段の昇格候補指標
       （幅プロファイル／radial 分位点／エンベロープ／房ピッチ／ミラー／根元固定帯）を比較対象に追加。
       レシピが幅プロファイルやレイヤーを取りこぼしていないかは、この表の★で判定する。
"""
import sys
import json

COMPARE_VERSION = "0.2.0"

# aggregates 直下のスカラ（key, サブキー or None）
NUM_KEYS = [
    ("strand_count", None),
    ("verts_per_strand", "median"),
    ("length_geo_norm", "median"),
    ("length_geo_norm", "min"),
    ("length_geo_norm", "max"),
    ("straightness", "median"),
    ("turn_total_deg", "median"),
    ("twist_total_deg", "median"),
    ("root_width_norm", "median"),
    ("mid_width_norm", "median"),
    ("tip_width_norm", "median"),
    ("root_tip_width_ratio", "median"),
    ("rows_estimate", "median"),
    ("grid_fit_fraction", None),
    ("tip_split_fraction", None),
    ("root_radial", "median"),
    ("mid_radial", "median"),
    ("tip_z_norm_min", None),
    # --- 0.2.0 追加 ---
    ("width_bulge_ratio", "median"),      # 板の中膨れ
    ("root_radial", "p10"),               # radial 分位点（レイヤーの内外の広がり）
    ("root_radial", "p90"),
    ("mid_radial", "p10"),
    ("mid_radial", "p90"),
    ("tip_radial", "median"),
    ("root_spacing_norm", "median"),      # 房ピッチ
    ("root_pitch_ratio", None),
]

# ネストしたスカラ（ドット区切り）。数値なら相対差、そうでなければ一致判定
PATH_KEYS = [
    "envelope.bbox_norm.x",
    "envelope.bbox_norm.y",
    "envelope.bbox_norm.z",
    "envelope.radial.p90",
    "envelope.radial.max",
    "envelope.silhouette_ratio",
    "envelope.widest_band",
    "envelope.top_z_norm",
    "envelope.bottom_z_norm",
]

# 要素ごとに比較する数値リスト
PROFILE_KEYS = [
    "width_profile_median",                  # [t0,t25,t50,t75,t100]（根元幅=1.0）
    "weights.head_weight_profile_median",    # 根元→毛先の Head ウェイト
]

CAT_KEYS = ["columns_estimate_mode", "length_class_guess", "width_profile_mode"]
DICT_KEYS = ["cross_section_counts", "region_counts", "width_profile_counts", "layer_counts"]
BLOCK_KEYS = ["weights", "normals", "mirror"]
THRESH = 0.25


def get(agg, key, sub):
    v = agg.get(key)
    if sub is not None:
        v = (v or {}).get(sub) if isinstance(v, dict) else None
    return v


def dig(d, path):
    cur = d
    for part in path.split("."):
        if not isinstance(cur, dict):
            return None
        cur = cur.get(part)
    return cur


def fmt(v):
    if v is None:
        return "-"
    if isinstance(v, float):
        return "%.4g" % v
    if isinstance(v, list):
        return "[" + ", ".join(fmt(x) for x in v) + "]"
    return str(v)


def row(lines, label, va, vb):
    """1 行追加して、★ を付けたら 1 を返す。"""
    if va is None or vb is None:
        lines.append("| %s | %s | %s | - | 未測定 |" % (label, fmt(va), fmt(vb)))
        return 0
    if isinstance(va, bool) or isinstance(vb, bool) or not isinstance(va, (int, float)) \
            or not isinstance(vb, (int, float)):
        mark = "" if va == vb else "★"
        lines.append("| %s | %s | %s | - | %s |" % (label, fmt(va), fmt(vb), mark))
        return 1 if mark else 0
    rel = abs(vb - va) / max(abs(va), 1e-9)
    mark = "★" if rel > THRESH else ""
    lines.append("| %s | %s | %s | %.0f%% | %s |" % (label, fmt(va), fmt(vb), rel * 100, mark))
    return 1 if mark else 0


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    md_out = None
    if "--md" in sys.argv:
        md_out = sys.argv[sys.argv.index("--md") + 1]
    if len(args) < 2:
        print(__doc__)
        sys.exit(1)
    A = json.load(open(args[0], encoding="utf-8"))
    B = json.load(open(args[1], encoding="utf-8"))
    a, b = A["aggregates"], B["aggregates"]
    lines = ["| 項目 | 元 | 再生成 | 相対差 | 判定 |", "|---|---|---|---|---|"]
    flagged = 0

    for key, sub in NUM_KEYS:
        label = key + ("." + sub if sub else "")
        flagged += row(lines, label, get(a, key, sub), get(b, key, sub))

    for path in PATH_KEYS:
        flagged += row(lines, path, dig(a, path), dig(b, path))

    for path in PROFILE_KEYS:
        pa, pb = dig(a, path), dig(b, path)
        if not isinstance(pa, list) or not isinstance(pb, list) or len(pa) != len(pb):
            flagged += row(lines, path, pa, pb)
            continue
        for i, (va, vb) in enumerate(zip(pa, pb)):
            flagged += row(lines, "%s[%d]" % (path, i), va, vb)

    for key in CAT_KEYS:
        flagged += row(lines, key, a.get(key), b.get(key))

    for key in DICT_KEYS:
        da, db = a.get(key) or {}, b.get(key) or {}
        for k in sorted(set(da) | set(db), key=str):
            va, vb = da.get(k, 0), db.get(k, 0)
            rel = abs(vb - va) / max(abs(va), 1)
            mark = "★" if rel > THRESH else ""
            flagged += 1 if mark else 0
            lines.append("| %s[%s] | %s | %s | %.0f%% | %s |" % (key, k, va, vb, rel * 100, mark))

    skip = {p.split(".", 1)[1] for p in PROFILE_KEYS if "." in p}
    for key in BLOCK_KEYS:
        wa, wb = a.get(key) or {}, b.get(key) or {}
        for k in sorted(set(wa) | set(wb)):
            if k in skip:
                continue  # PROFILE_KEYS で要素ごとに比較済み
            va, vb = wa.get(k), wb.get(k)
            if isinstance(va, dict) or isinstance(vb, dict):
                va = (va or {}).get("median")
                vb = (vb or {}).get("median")
            flagged += row(lines, "%s.%s" % (key, k), va, vb)

    head = ("compare_hair_json v%s\n元: %s (head=%s, script=%s)\n再生成: %s (head=%s, script=%s)\n"
            "★=相対差>%d%% または不一致 → レシピの欠落候補\n") % (
        COMPARE_VERSION,
        args[0], A.get("head", {}).get("source"), A.get("meta", {}).get("script_version"),
        args[1], B.get("head", {}).get("source"), B.get("meta", {}).get("script_version"),
        THRESH * 100)
    text = head + "\n" + "\n".join(lines) + "\n\n★ 件数: %d" % flagged
    print(text)
    if md_out:
        with open(md_out, "w", encoding="utf-8") as f:
            f.write(text + "\n")


if __name__ == "__main__":
    main()
