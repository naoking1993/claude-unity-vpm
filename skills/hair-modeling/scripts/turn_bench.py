#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""turn_bench.py — inspect_hair.py の turn 計測を「中心線が既知の合成カード」で検証する（Blender 不要）

なぜ要るか: turn は samples とレシピに数値で載る量なので、値が正しいことと、
房の行数（＝断面数）を変えても値が動かないこと（解像度非依存）を、実測で示せる必要がある。
selftest には代表4ケースだけ入れてある。全形状×全解像度の表が要るときはこちらを使う。

使い方: python turn_bench.py            … 一覧表（真値・実測・偏差）
        python turn_bench.py --json     … 機械可読

真値は中心線の接線の総回転量 ∫|dθ| を高解像度で数値積分したもの。
カードは中心線に沿って幅を持たせただけなので、中心線の真値がそのまま房の真値になる。
"""
import sys
import os
import math
import json

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import inspect_hair as ih  # noqa: E402

HEAD = {"center": (0.0, 0.0, 0.0), "half": (0.1, 0.1, 0.12), "width": 0.2, "source": "turn_bench"}
ROWS = [6, 8, 12, 24, 48]


def true_turn(center_fn, N=200000):
    prev, tot, h = None, 0.0, 1.0 / N
    for k in range(N + 1):
        s = k * h
        a = center_fn(min(1.0, s + h * 0.5))
        b = center_fn(max(0.0, s - h * 0.5))
        d = ih._norm(ih._sub(a, b))
        if prev is not None:
            ang = ih._angle_deg(prev, d)
            if ang:
                tot += ang
        prev = d
    return tot


def straight(s):
    return (0.0, -0.09, 0.08 - 0.18 * s)


def parabola(s):          # selftest のカードと同じ中心線
    return (0.0, -0.09 - 0.02 * s - 0.03 * s * s, 0.08 - 0.15 * s)


def arc60(s):
    a = math.radians(60.0) * s
    return (0.0, -0.09 + 0.12 * (1 - math.cos(a)), 0.08 - 0.12 * math.sin(a))


def arc150(s):
    a = math.radians(150.0) * s
    return (0.0, -0.06 + 0.08 * (1 - math.cos(a)), 0.08 - 0.08 * math.sin(a))


def scurve(s):
    return (0.0, -0.09 + 0.03 * math.sin(2 * math.pi * s), 0.08 - 0.18 * s)


def helix(s):
    a = 2 * math.pi * s
    return (0.02 * math.sin(a), -0.09 + 0.02 * (1 - math.cos(a)), 0.08 - 0.18 * s)


SHAPES = [("straight", straight), ("parabola", parabola), ("arc60", arc60),
          ("arc150", arc150), ("scurve", scurve), ("helix", helix)]


def measure(center_fn, rows, cols=4):
    mesh = ih._curve_card("Hair_bench", rows, cols, center_fn)
    return ih.analyze_mesh(mesh, HEAD, dict(ih.DEFAULTS))[0][0]


def main():
    out = {"script_version": ih.SCRIPT_VERSION, "rows": ROWS, "shapes": {}}
    for name, fn in SHAPES:
        tt = true_turn(fn)
        vals = [measure(fn, r).get("turn_total_deg") for r in ROWS]
        good = [v for v in vals if v is not None]
        out["shapes"][name] = {
            "true_turn_deg": round(tt, 2),
            "measured": vals,
            "max_abs_err_deg": round(max(abs(v - tt) for v in good), 1) if good else None,
            "spread_deg": round(max(good) - min(good), 1) if good else None,
        }
    if "--json" in sys.argv:
        print(json.dumps(out, ensure_ascii=False, indent=1))
        return 0
    print("inspect_hair v%s / turn_total_deg （カード行数を変えても値が動かないことが要件）" % ih.SCRIPT_VERSION)
    print("形状        真値" + "".join("  行%-3d" % r for r in ROWS) + "   最大誤差   ばらつき")
    for name, _ in SHAPES:
        d = out["shapes"][name]
        print("%-11s %6.1f" % (name, d["true_turn_deg"])
              + "".join("%7s" % ("-" if v is None else "%.1f" % v) for v in d["measured"])
              + "%9s %10s" % (d["max_abs_err_deg"], d["spread_deg"]))
    print("\n行数6は断面が6枚しかない房＝過小に出るのが正常（埋めずにそのまま記録する）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
