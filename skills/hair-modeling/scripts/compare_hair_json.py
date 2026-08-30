#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
compare_hair_json.py — inspect_hair.py の出力 JSON 2 つ（元アセット vs 再生成）を比較する（第3段 往復検証用）

使い方: python compare_hair_json.py original.json regenerated.json [--md out.md]

出力: 集計値の並置表（Markdown）。差が大きい行に ★ を付ける。
      「差が大きい」の閾値は相対差 25%（数値）/ 不一致（カテゴリ）。閾値は校正対象。
Blender 不要。
"""
import sys
import json

NUM_KEYS = [
    ("strand_count", None),
    ("verts_per_strand", "median"),
    ("length_geo_norm", "median"),
    ("length_geo_norm", "min"),
    ("length_geo_norm", "max"),
    ("straightness", "median"),
    ("turn_total_deg", "median"),
    ("turn_net_deg", "median"),
    ("turn_per_length_deg", "median"),
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
]
CAT_KEYS = ["columns_estimate_mode", "length_class_guess"]
# turn 系は中心線が引けた房でしか出ない。母集団が違えば中央値は比べられないので、まず被覆率を見る
COVERAGE_KEYS = ["turn_measured_fraction"]
DICT_KEYS = ["cross_section_counts", "region_counts"]
THRESH = 0.25


def get(agg, key, sub):
    v = agg.get(key)
    if sub is not None:
        v = (v or {}).get(sub) if isinstance(v, dict) else None
    return v


def fmt(v):
    if v is None:
        return "-"
    if isinstance(v, float):
        return "%.4g" % v
    return str(v)


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
        va, vb = get(a, key, sub), get(b, key, sub)
        label = key + ("." + sub if sub else "")
        if va is None or vb is None:
            lines.append("| %s | %s | %s | - | 未測定 |" % (label, fmt(va), fmt(vb)))
            continue
        den = max(abs(va), 1e-9)
        rel = abs(vb - va) / den
        mark = "★" if rel > THRESH else ""
        flagged += 1 if mark else 0
        lines.append("| %s | %s | %s | %.0f%% | %s |" % (label, fmt(va), fmt(vb), rel * 100, mark))
    for key in CAT_KEYS:
        va, vb = a.get(key), b.get(key)
        mark = "" if va == vb else "★"
        flagged += 1 if mark else 0
        lines.append("| %s | %s | %s | - | %s |" % (key, fmt(va), fmt(vb), mark))
    for key in COVERAGE_KEYS:
        va, vb = a.get(key), b.get(key)
        if va is None and vb is None:
            continue
        mark = "★" if (va is None or vb is None or abs(vb - va) > 0.1) else ""
        flagged += 1 if mark else 0
        lines.append("| %s | %s | %s | - | %s |" % (key, fmt(va), fmt(vb), mark))
    for key in DICT_KEYS:
        da, db = a.get(key) or {}, b.get(key) or {}
        for k in sorted(set(da) | set(db), key=str):
            va, vb = da.get(k, 0), db.get(k, 0)
            den = max(abs(va), 1)
            rel = abs(vb - va) / den
            mark = "★" if rel > THRESH else ""
            flagged += 1 if mark else 0
            lines.append("| %s[%s] | %s | %s | %.0f%% | %s |" % (key, k, va, vb, rel * 100, mark))
    for key in ("weights", "normals"):
        wa, wb = a.get(key) or {}, b.get(key) or {}
        for k in sorted(set(wa) | set(wb)):
            va, vb = wa.get(k), wb.get(k)
            if isinstance(va, dict):
                va, vb = va.get("median"), (vb or {}).get("median")
            if isinstance(va, (int, float)) and isinstance(vb, (int, float)):
                rel = abs(vb - va) / max(abs(va), 1e-9)
                mark = "★" if rel > THRESH else ""
                lines.append("| %s.%s | %s | %s | %.0f%% | %s |" % (key, k, fmt(va), fmt(vb), rel * 100, mark))
            else:
                mark = "" if va == vb else "★"
                lines.append("| %s.%s | %s | %s | - | %s |" % (key, k, fmt(va), fmt(vb), mark))
            flagged += 1 if mark else 0
    va_ = (A.get("meta") or {}).get("script_version")
    vb_ = (B.get("meta") or {}).get("script_version")
    warn = ""
    if va_ != vb_:
        warn = ("\n!! script_version が違う (%s vs %s)。turn_total_deg / straightness は 0.2.0 で定義が変わった"
                "（turn_v1→turn_v2）ので、版をまたいだ比較は無効。古い方を取り直すこと。\n" % (va_, vb_))
    head = "元: %s (head=%s, v%s)\n再生成: %s (head=%s, v%s)\n★=相対差>%d%% または不一致 → レシピの欠落候補\n%s" % (
        args[0], A.get("head", {}).get("source"), va_,
        args[1], B.get("head", {}).get("source"), vb_, THRESH * 100, warn)
    text = head + "\n" + "\n".join(lines) + "\n\n★ 件数: %d" % flagged
    print(text)
    if md_out:
        with open(md_out, "w", encoding="utf-8") as f:
            f.write(text + "\n")


if __name__ == "__main__":
    main()
