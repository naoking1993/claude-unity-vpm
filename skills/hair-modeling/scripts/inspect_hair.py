#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
inspect_hair.py — 髪型メッシュ解析（hair-modeling スキル 第0段の固定資産）

目的: 髪型メッシュから「実測できる構造情報」を毎回同じ手順で JSON に落とす。
      目視・記憶に頼った記述を排除し、セッション間の解析ばらつきを潰す。

使い方
  A) Blender GUI  : Text Editor に読み込んで実行（選択中のメッシュが対象）
  B) Blender MCP  : execute 系 tool で以下を実行
        INSPECT_HAIR_CONFIG = {"targets": ["Hair"], "head_object": "Face",
                               "out": "C:/tmp/hair_inspect.json"}
        exec(open(r"path/to/inspect_hair.py", encoding="utf-8").read())
     → 標準出力に 10 行程度のサマリーだけ返る。JSON はファイルへ。
     ※ exec 経由では __name__ が "__main__" とは限らないため、INSPECT_HAIR_CONFIG を
       必ず定義してから exec する（既定値でよければ空 dict {} でも可）。それが実行トリガー。
  C) バックグラウンド:
        blender -b file.blend --python inspect_hair.py -- \
            --targets Hair,Hair_Back --head Face --out out.json
  D) 自己テスト（Blender 不要）: python inspect_hair.py --selftest

設定キー（DEFAULTS 参照）: targets / head_object / head_vertex_group /
  head_bbox_override / front_axis / use_evaluated / out / max_strands_detail

出力の読み方
  - 数値は全て実測。ただし接尾辞 _guess / _estimate の項目は
    ルールに基づく推定で、ルール文字列を meta.rules に同梱する。
  - 長さ・幅の *_norm は「頭部バウンディングボックス幅 = 1.0」で正規化した値。
  - 位置の *_norm は頭部中心を原点、各軸の半径で割った値（頭部楕円体 = 半径 1）。
  - 前方向は front_axis（既定 -Y）を仮定している。要検証。

0.2.0 で追加した集計（第2段=横断統合が patterns.md へ昇格させるための指標）
  - 幅プロファイル: strands[].width_profile_norm / width_bulge_ratio / width_profile_guess
                    → aggregates.width_profile_median / width_profile_counts（板の中膨れ）
  - レイヤー      : aggregates.root_radial / mid_radial / tip_radial の p10/p25/median/p75/p90 と
                    strands[].layer_guess → aggregates.layer_counts
  - エンベロープ  : aggregates.envelope（髪全体の bbox・水平半径の z 帯別プロファイル・最大張り出し帯）
  - 房ピッチ      : strands[].root_spacing_norm → aggregates.root_spacing_norm / root_pitch_ratio
  - 左右対称性    : aggregates.mirror（ミラー相手の一致率・正中房数）
  - 根元固定帯    : strands[].groups.root_lock_t / head_release_t / head_weight_profile
                    → aggregates.weights.root_lock_t ほか
  - 領域別        : aggregates.by_region に taper/twist/turn/radial/layer/pitch を追加
"""
import sys
import os
import json
import math
import heapq
import datetime
import tempfile
from collections import defaultdict, Counter

try:
    import numpy as np
except Exception:  # Blender 同梱 Python には numpy がある。無い環境では PCA 系が None になる
    np = None

SCRIPT_VERSION = "0.2.0"

DEFAULTS = {
    # 対象オブジェクト名リスト。None → 選択中メッシュ → 名前ヒント一致メッシュ の順で探す
    "targets": None,
    "hair_name_hints": ["hair", "Hair", "HAIR", "髪", "kami", "Kami"],
    # 頭部基準。優先順: head_bbox_override > head_object > head_vertex_group > 自動探索 > 髪全体bbox
    "head_object": None,             # 頭部のみのメッシュ名（Body 全体だと幅が狂う）
    "head_vertex_group": None,       # ["Body", "Head"] のように (オブジェクト, 頂点グループ)
    "head_bbox_override": None,      # {"center": [x,y,z], "half": [rx,ry,rz]}
    "head_group_hints": ["Head", "head", "J_Bip_C_Head", "頭", "HEAD"],
    # Head 系判定から除くトークン。髪側のボーン（Hair_Head_02 等）を頭部と誤認しないため
    "head_group_exclude_tokens": ["hair", "髪", "kami", "ahoge", "アホ毛"],
    "head_weight_threshold": 0.5,
    # 前方向の仮定（Blender 標準はキャラが -Y を向く）。"-Y" / "+Y" / "-X" / "+X"
    "front_axis": "-Y",
    # True: モディファイア適用後メッシュで幾何解析（Subsurf/Mirror/Data Transfer の結果を見たいとき）
    "use_evaluated": False,
    "out": None,                     # JSON 出力先。None → .blend と同じ場所 or temp
    "max_strands_detail": 400,       # 房ごとの詳細を JSON に載せる上限（集計は全件で行う）
    "tip_split_t": 0.85,             # 毛先分岐判定に使う t の下限
    "root_band_frac": 0.12,          # 根元帯（t の幅）
    "band_half_width": 0.07,         # 25/50/75% 帯の半幅
    # --- 第2段 昇格候補指標のパラメータ（0.2.0 追加。閾値はいずれも未校正＝初版の当て推量） ---
    "layer_bounds": [1.05, 1.25],    # mid_radial_h(水平半径): 未満=scalp / 未満=mid / 以上=outer
    "layer_below_z": -1.0,           # 房の中点 z_norm がこれ未満なら頭部を離れており層は未定義
    "bulge_bounds": [0.9, 1.1],      # 中央/両端 の比。以下=中細り / 以上=中膨れ
    "taper_bounds": [0.8, 1.3],      # 根元/毛先幅比: 以下=flare / 以上=taper_linear
    "envelope_min_band_points": 3,   # z 帯を報告する最小頂点数
    "mirror_tol_norm": 0.06,         # ミラー相手とみなす根元距離（頭幅比）
    "mirror_length_tol": 0.15,       # ミラー相手とみなす長さの相対差
    "mirror_midline_norm": 0.08,     # |root_norm[横軸]| がこれ未満なら正中房（対称判定の分母から除外）
    "max_pairwise_strands": 2000,    # 房ピッチ・ミラーの総当たり計算を行う上限房数
    "root_lock_weight": 0.99,        # 根元固定とみなす Head 系ウェイト
    "root_lock_fraction": 0.9,       # 帯内でその条件を満たす頂点の割合
    "weight_bins": 20,               # 根元固定帯を測る t の分割数
}

RULES = {
    "region_v1": (
        "root_norm=(頭部中心原点・半径正規化)。f=前方向成分, s=横方向成分, z=上下。"
        "|s|>=0.7→side_±X | z>0.75&|s|<0.35&|f|<0.5→top(毛先が根元より0.2以上高ければ ahoge) | "
        "f>0.35&z>0.2→bangs | f<-0.35→back | f>0.35→front_low | それ以外→unclassified"
    ),
    "cross_section_v1": (
        "mid_thickness_ratio(=50%帯の厚み/幅)<0.25→flat_card | "
        "それ以外で boundary_vertex_fraction<0.35 または root_ring_closed→closed_tube | それ以外→unknown"
    ),
    "grid_v1": (
        "閉じた根元リングがあれば列数=リング頂点数。無ければ正則四角格子を仮定し、"
        "頂点数n・境界頂点数bから w+r=(b+4)/2, w*r=n の二次方程式で列数w・行数rを解く。"
        "非正則トポロジでは None"
    ),
    "length_class_v1": (
        "全房の毛先 z_norm 最小値: >-0.6→very_short | >-1.2→short(顎まで) | >-2.5→medium(首〜肩) | それ以下→long"
    ),
    "twist_v1": (
        "5帯それぞれの断面PCAで得た幅軸を直線とみなし、隣接帯間の角度(0〜90°)を合計。"
        "法線軸の回転（＝曲げ）は含まないが、面内曲がりは混入する"
    ),
    "uv_direction_v1": (
        "房内ループの t(根元0→毛先1) と u,v の相関係数を比較し、絶対値が大きい軸を「房の長手方向のUV軸」とする。"
        "符号が負なら毛先に向かって減少"
    ),
    # --- 0.2.0 追加（第2段 昇格候補指標） ---
    "width_profile_v2": (
        "5帯の幅から判定する。中膨れ率 bulge=w50/max(w0,w100)（中央が"
        "『両端のどちらよりも』太いか）、中細り率 waist=w50/min(w0,w100)、先細り比 taper=w0/w100。"
        "bulge>=bulge_bounds[1]→mid_bulge（板の中膨れ） | waist<=bulge_bounds[0]→waisted（中細り） | "
        "taper>=taper_bounds[1]→taper_linear | taper<=taper_bounds[0]→flare（毛先広がり） | それ以外→uniform。"
        "width_profile_norm は根元幅=1.0 に正規化した [t0,t25,t50,t75,t100]。"
        "※v1 は w50/((w0+w100)/2)（両端の平均との比）で判定していたが、これは中膨れではなく"
        "テーパ曲線の凸性であり、単調に細るだけの板を mid_bulge/waisted と誤判定した。"
        "凸性は width_curvature_ratio として別に出す"
    ),
    "layer_v2": (
        "水平半径 mid_radial_h=sqrt(x_n^2+y_n^2)（頭皮面=1.0）で層を分ける。"
        "房の中点 z_norm<layer_below_z→below_head（頭部を離れており層は定義できない） | "
        "<layer_bounds[0]→scalp（頭皮沿い） | <layer_bounds[1]→mid | 以上→outer（外側レイヤー）。"
        "※v1 は 3D 半径 mid_radial を使っていたが、これには z が入るため"
        "『頭皮に密着した長い房』が『外側に膨らんだ短い房』より外側と判定され、層の順序が反転した"
    ),
    "envelope_v1": (
        "髪全体の頂点を頭部正規化座標に置き、z_norm 固定帯"
        "(above_crown>1.2 / crown / upper / eye / jaw / neck / shoulder / below<=-3.0) ごとに"
        "水平半径 sqrt(x_n^2+y_n^2)（1.0=頭皮面）の p50/p90/max を出す。"
        "r_p90 が最大の帯を widest_band、その値を silhouette_ratio とする。"
        "帯は頭部基準に固定なのでサンプル間で直接比較できる"
    ),
    "spacing_v1": (
        "各房の根元重心から最近傍の他房の根元までの距離（頭幅比）を房ピッチとする。"
        "root_pitch_ratio=根元幅中央値/房ピッチ中央値。>1 は根元で房が重なることを示す"
    ),
    "mirror_v1": (
        "根元重心を横軸（front_axis に直交する水平軸）について頭部中心で鏡映し、"
        "mirror_tol_norm（頭幅比）以内に根元があり長さの相対差が mirror_length_tol 以内の房を相手とみなす。"
        "|root_norm[横軸]|<mirror_midline_norm の房は正中房として分母から除く"
    ),
    "root_lock_v1": (
        "Head 系頂点グループ（head_group_hints 一致）の最大ウェイトを t で weight_bins 分割し、"
        "根元から連続して『ウェイト>=root_lock_weight の頂点が root_lock_fraction 以上』を満たす"
        "最後の帯の上端を root_lock_t（根元固定帯の長さ）とする。"
        "平均ウェイトが 0.01 未満になる最初の帯の下端を head_release_t（頭部から完全に離れる位置）"
    ),
}


# ----------------------------------------------------------------------------
# 小道具
# ----------------------------------------------------------------------------
def _r(x, nd=4):
    if x is None:
        return None
    if isinstance(x, bool):
        return x
    if isinstance(x, (int,)):
        return x
    try:
        xf = float(x)
    except Exception:
        return x
    if math.isnan(xf) or math.isinf(xf):
        return None
    return round(xf, nd)


def _rt(v, nd=5):
    return [_r(float(c), nd) for c in v]


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def _len(a):
    return math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2])


def _dist(a, b):
    return _len(_sub(a, b))


def _norm(a):
    l = _len(a)
    return (a[0] / l, a[1] / l, a[2] / l) if l > 1e-12 else (0.0, 0.0, 0.0)


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _angle_deg(a, b):
    na, nb = _norm(a), _norm(b)
    if _len(na) == 0 or _len(nb) == 0:
        return None
    d = max(-1.0, min(1.0, _dot(na, nb)))
    return math.degrees(math.acos(d))


def _line_angle_deg(a, b):
    """向きを無視した直線同士の角度（0〜90°）"""
    ang = _angle_deg(a, b)
    if ang is None:
        return None
    return min(ang, 180.0 - ang)


def _centroid(pts):
    n = len(pts)
    if n == 0:
        return None
    sx = sum(p[0] for p in pts)
    sy = sum(p[1] for p in pts)
    sz = sum(p[2] for p in pts)
    return (sx / n, sy / n, sz / n)


def _stats(vals, nd=4):
    vals = [float(v) for v in vals if v is not None and not (isinstance(v, float) and math.isnan(v))]
    if not vals:
        return {"count": 0}
    vals.sort()
    n = len(vals)

    def q(p):
        if n == 1:
            return vals[0]
        k = (n - 1) * p
        f = int(math.floor(k))
        c = min(f + 1, n - 1)
        return vals[f] + (vals[c] - vals[f]) * (k - f)

    return {
        "count": n,
        "min": _r(vals[0], nd),
        "p10": _r(q(0.10), nd),
        "p25": _r(q(0.25), nd),
        "median": _r(q(0.5), nd),
        "p75": _r(q(0.75), nd),
        "p90": _r(q(0.90), nd),
        "max": _r(vals[-1], nd),
        "mean": _r(sum(vals) / n, nd),
    }


def _corr(xs, ys):
    """ピアソン相関（numpy 非依存）。どちらかの標準偏差が 0 なら None。"""
    n = len(xs)
    if n < 2 or n != len(ys):
        return None
    mx = sum(xs) / float(n)
    my = sum(ys) / float(n)
    sxx = syy = sxy = 0.0
    for x, y in zip(xs, ys):
        dx = x - mx
        dy = y - my
        sxx += dx * dx
        syy += dy * dy
        sxy += dx * dy
    if math.sqrt(sxx / n) <= 1e-9 or math.sqrt(syy / n) <= 1e-9:
        return None
    return sxy / math.sqrt(sxx * syy)


def _mode(vals):
    vals = [v for v in vals if v is not None]
    if not vals:
        return None, 0
    c = Counter(vals).most_common(1)[0]
    return c[0], c[1]


def _profile_median(profiles, nd=3):
    """同じ長さのプロファイル列（[t0..t100] など）の要素ごとの中央値。欠損を含む行は捨てる。"""
    ps = [p for p in profiles if p and all(v is not None for v in p)]
    if not ps:
        return None
    L = min(len(p) for p in ps)
    ps = [p for p in ps if len(p) == L]
    if not ps:
        return None
    out = []
    for k in range(L):
        vals = sorted(float(p[k]) for p in ps)
        m = len(vals)
        out.append(_r(vals[m // 2] if m % 2 else (vals[m // 2 - 1] + vals[m // 2]) / 2.0, nd))
    return out


def _name_tokens(s):
    """名前を英数字/かな漢字の連なりで区切る。"J_Bip_C_Head" -> [j, bip, c, head]"""
    out, cur = [], []
    for ch in s:
        if ch.isalnum():
            cur.append(ch)
        elif cur:
            out.append("".join(cur))
            cur = []
    if cur:
        out.append("".join(cur))
    return out


def is_head_group_name(name, cfg):
    """Head 系の頂点グループ名か（head_group_v2）。

    ①head_group_hints と完全一致 → Head 系
    ②head_group_exclude_tokens（hair/髪 等）を語として含む → Head 系ではない
    ③"head" またはヒントを *語として* 含む → Head 系

    生の部分一致（`"head" in name`）だと "HeadHair_01" や "Headband" まで拾う。
    髪の PhysBone チェーンを Head 系と誤認すると、房全体が頭部固定に見えて
    root_lock_t が 1.0 に飽和し、「頭に固定された髪」という正反対の読みになる。
    """
    if name is None:
        return False
    low = str(name).strip().lower()
    hints = [h.lower() for h in cfg["head_group_hints"]]
    if low in hints:
        return True
    toks = _name_tokens(low)
    if any(t in cfg["head_group_exclude_tokens"] for t in toks):
        return False
    return "head" in toks or any(h in toks for h in hints)


def _head_group_indices(md, cfg):
    """Head 系とみなす頂点グループの index 集合。_choose_root_end と同じ判定を使う。"""
    return set(gi for gi, nm in enumerate(md.group_names) if is_head_group_name(nm, cfg))


def width_profile_guess(bulge, waist, taper, cfg):
    """幅プロファイルの形（width_profile_v2）。板の中膨れの検出がここ。

    中膨れ/中細りは「中央が両端の *どちらよりも* 太い/細い」で判定する。
    両端の平均と比べると、単調に細るだけの板（例 1.00→0.30→0.10）が中細りに、
    （例 1.00→0.90→0.10）が中膨れに化ける。
    """
    lo_b, hi_b = cfg["bulge_bounds"]
    if bulge is not None and bulge >= hi_b:
        return "mid_bulge"
    if waist is not None and waist <= lo_b:
        return "waisted"
    if taper is None:
        return None if bulge is None else "uniform"
    lo_t, hi_t = cfg["taper_bounds"]
    if taper >= hi_t:
        return "taper_linear"
    if taper <= lo_t:
        return "flare"
    return "uniform"


def layer_guess(mid_radial_h, mid_z_norm, cfg):
    """房がどの層にあるか（layer_v2）。水平半径 1.0 = 頭皮面。

    3D 半径を使うと z 成分が入り、頭皮に密着した長い房が外側レイヤーと判定される。
    層は「頭からどれだけ横に離れているか」なので水平半径で測る。
    頭部より下まで垂れた房には層が定義できないので below_head を返す。
    """
    if mid_radial_h is None:
        return None
    if mid_z_norm is not None and mid_z_norm < cfg["layer_below_z"]:
        return "below_head"
    inner, outer = cfg["layer_bounds"]
    if mid_radial_h < inner:
        return "scalp"
    if mid_radial_h < outer:
        return "mid"
    return "outer"


# ----------------------------------------------------------------------------
# データ構造（bpy 非依存）
# ----------------------------------------------------------------------------
class MeshData:
    """解析に必要な最小限のメッシュ情報。座標・法線はワールド空間。"""
    __slots__ = ("name", "verts", "vnormals", "edges", "faces", "loops_vert",
                 "loops_face", "uv", "corner_normals", "vgroups", "group_names")

    def __init__(self, name=""):
        self.name = name
        self.verts = []          # [(x,y,z)]
        self.vnormals = []       # [(x,y,z)] 幾何法線（頂点）
        self.edges = []          # [(i,j)]
        self.faces = []          # [[i,j,k,...]]
        self.loops_vert = []     # loop -> vert index
        self.loops_face = []     # loop -> face index
        self.uv = None           # [(u,v)] per loop or None
        self.corner_normals = None  # [(x,y,z)] per loop（カスタム法線がある場合）
        self.vgroups = []        # per vert: [(group_index, weight)]
        self.group_names = []    # group_index -> name


def head_from_points(pts, source):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    zs = [p[2] for p in pts]
    mn = (min(xs), min(ys), min(zs))
    mx = (max(xs), max(ys), max(zs))
    center = tuple((mn[i] + mx[i]) / 2.0 for i in range(3))
    half = tuple(max((mx[i] - mn[i]) / 2.0, 1e-6) for i in range(3))
    return {"center": center, "half": half, "width": 2.0 * half[0], "source": source,
            "bbox_min": mn, "bbox_max": mx}


def hnorm(head, p):
    return tuple((p[i] - head["center"][i]) / head["half"][i] for i in range(3))


def hradial(head, p):
    q = hnorm(head, p)
    return math.sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2])


def front_axis_spec(front_axis):
    fa = (front_axis or "-Y").upper()
    sign = -1.0 if fa.startswith("-") else 1.0
    axis = fa[-1]
    f_idx = 1 if axis == "Y" else 0
    s_idx = 0 if f_idx == 1 else 1
    return f_idx, sign, s_idx


def region_guess(root_n, tip_n, fa_spec):
    f_idx, sign, s_idx = fa_spec
    f = sign * root_n[f_idx]
    s = root_n[s_idx]
    z = root_n[2]
    a = abs(s)
    if a >= 0.7:
        return "side_%s%s" % ("+" if s > 0 else "-", "X" if s_idx == 0 else "Y")
    if z > 0.75 and a < 0.35 and abs(f) < 0.5:
        if tip_n is not None and tip_n[2] > z + 0.2:
            return "ahoge"
        return "top"
    if f > 0.35 and z > 0.2:
        return "bangs"
    if f < -0.35:
        return "back"
    if f > 0.35:
        return "front_low"
    return "unclassified"


# ----------------------------------------------------------------------------
# 幾何解析（bpy 非依存）
# ----------------------------------------------------------------------------
def geodesic(adj, sources):
    n = len(adj)
    dist = [math.inf] * n
    heap = []
    for s in sources:
        dist[s] = 0.0
        heap.append((0.0, s))
    heapq.heapify(heap)
    while heap:
        d, u = heapq.heappop(heap)
        if d > dist[u]:
            continue
        for v, w in adj[u]:
            nd = d + w
            if nd < dist[v]:
                dist[v] = nd
                heapq.heappush(heap, (nd, v))
    return dist


def band_profile(coords, tangent):
    """帯内の頂点を接線と直交する平面へ射影し、幅（主軸方向の広がり）と厚み（幅軸×接線 方向の広がり）を返す。
    返り値: (width, thickness, thickness_axis, width_axis) / 計算不能なら None"""
    if np is None or len(coords) < 2:
        return None
    X = np.array(coords, dtype=float)
    X = X - X.mean(axis=0)
    t = np.array(tangent, dtype=float)
    tn = float(np.linalg.norm(t))
    if tn > 1e-12:
        t = t / tn
        X = X - np.outer(X @ t, t)
    else:
        t = None
    cov = X.T @ X / max(1, len(X) - 1)
    try:
        w, V = np.linalg.eigh(cov)
    except Exception:
        return None
    pc1 = V[:, 2]
    p1 = X @ pc1
    width = float(p1.max() - p1.min())
    if t is not None:
        nrm = np.cross(t, pc1)
        nn = float(np.linalg.norm(nrm))
        if nn > 1e-12:
            nrm = nrm / nn
        else:
            nrm = V[:, 1]
    else:
        nrm = V[:, 1]
    p2 = X @ nrm
    thick = float(p2.max() - p2.min())
    return width, thick, tuple(float(c) for c in nrm), tuple(float(c) for c in pc1)


def _components(nodes, adj_local, allowed):
    """allowed 集合内の頂点だけを辿った連結成分（頂点リストのリスト）"""
    seen = set()
    comps = []
    for s in nodes:
        if s in seen or s not in allowed:
            continue
        stack = [s]
        seen.add(s)
        comp = []
        while stack:
            u = stack.pop()
            comp.append(u)
            for v, _w in adj_local[u]:
                if v in allowed and v not in seen:
                    seen.add(v)
                    stack.append(v)
        comps.append(comp)
    return comps


def _ring_closed(band, boundary_edges):
    """band 内の境界辺だけで作るグラフが単一閉路なら True。(closed, ring_vertex_count)"""
    deg = Counter()
    adjb = defaultdict(list)
    nodes = set()
    for a, b in boundary_edges:
        if a in band and b in band:
            deg[a] += 1
            deg[b] += 1
            adjb[a].append(b)
            adjb[b].append(a)
            nodes.add(a)
            nodes.add(b)
    if len(nodes) < 3:
        return False, len(nodes)
    if any(deg[x] != 2 for x in nodes):
        return False, len(nodes)
    start = next(iter(nodes))
    seen = {start}
    stack = [start]
    while stack:
        u = stack.pop()
        for v in adjb[u]:
            if v not in seen:
                seen.add(v)
                stack.append(v)
    return len(seen) == len(nodes), len(nodes)


def _spread(pts):
    """重心からの最大距離×2（端の太さの代理値。O(n)）"""
    c = _centroid(pts)
    if c is None:
        return 0.0
    return 2.0 * max(_dist(p, c) for p in pts)


def _choose_root_end(md, vidx, P, endA, endB, cfg):
    """両端集合のどちらが根元かを判定する。
    ルール順: ①Head系ボーンのウェイトが支配的な端 → ②明確に太い端（1.3倍以上） → ③高い端(z)。
    戻り値: (root_vertex_list_local, rule_name)"""
    def head_like(gidx):
        if gidx is None or gidx >= len(md.group_names):
            return False
        return is_head_group_name(md.group_names[gidx], cfg)

    if md.vgroups and md.group_names:
        def head_frac(end):
            hit = tot = 0
            for i in end:
                gs = md.vgroups[vidx[i]] if vidx[i] < len(md.vgroups) else []
                if not gs:
                    continue
                g, w = max(gs, key=lambda x: x[1])
                tot += 1
                if head_like(g):
                    hit += 1
            return (hit / tot) if tot else None
        fa, fb = head_frac(endA), head_frac(endB)
        if fa is not None and fb is not None:
            if fa > 0.5 and fb < 0.5:
                return endA, "head_weight"
            if fb > 0.5 and fa < 0.5:
                return endB, "head_weight"
    sa, sb = _spread([P[i] for i in endA]), _spread([P[i] for i in endB])
    if sa > 1.3 * sb:
        return endA, "wider_end"
    if sb > 1.3 * sa:
        return endB, "wider_end"
    za = _centroid([P[i] for i in endA])[2]
    zb = _centroid([P[i] for i in endB])[2]
    return (endA, "higher_end") if za >= zb else (endB, "higher_end")


def analyze_island(md, vidx, fidx, face_loops, isl_edges, isl_boundary, head, cfg, fa_spec, sid, obj_name):
    """1 房（連結成分）の解析。戻り値は JSON 化可能な dict。"""
    n = len(vidx)
    local = {v: i for i, v in enumerate(vidx)}
    P = [md.verts[v] for v in vidx]
    res = {"id": sid, "object": obj_name, "verts": n, "faces": len(fidx)}

    # 面種別
    ft = Counter()
    for f in fidx:
        m = len(md.faces[f])
        ft["tri" if m == 3 else "quad" if m == 4 else "ngon"] += 1
    res["face_types"] = dict(ft)

    if n < 3 or len(fidx) == 0:
        res["degenerate"] = True
        return res

    # 局所隣接（辺長付き）
    # isl_edges / isl_boundary はこの島の辺だけ。メッシュ全体の辺集合を島ごとに走査すると
    # Θ(島数 × 辺数) になり、房数の多い髪で実用にならなかった（房 200 で 0.6 秒 → 房 1000 で分単位）
    adj = [[] for _ in range(n)]
    loc_edges = []
    for a, b in isl_edges:
        la, lb = local[a], local[b]
        w = _dist(P[la], P[lb])
        adj[la].append((lb, w))
        adj[lb].append((la, w))
        loc_edges.append((la, lb))
    loc_boundary = [(local[a], local[b]) for a, b in isl_boundary]
    boundary_verts = set()
    for a, b in loc_boundary:
        boundary_verts.add(a)
        boundary_verts.add(b)
    res["boundary_vertex_fraction"] = _r(len(boundary_verts) / n)

    # 両端点の探索（ダブルスイープ）→ 根元側の判定
    # 注意: 「頭部中心に最も近い端＝根元」は頭皮に沿う房で中間が最近点になり破綻するため使わない
    d0 = geodesic(adj, [0])
    A = max(range(n), key=lambda i: d0[i] if not math.isinf(d0[i]) else -1.0)
    dA = geodesic(adj, [A])
    B = max(range(n), key=lambda i: dA[i] if not math.isinf(dA[i]) else -1.0)
    L = dA[B]
    if L <= 1e-9:
        res["degenerate"] = True
        return res
    dB = geodesic(adj, [B])
    endA = [i for i in range(n) if dA[i] <= 0.12 * L]
    endB = [i for i in range(n) if dB[i] <= 0.12 * L]
    root_end, root_rule = _choose_root_end(md, vidx, P, endA, endB, cfg)
    res["root_rule_used"] = root_rule
    res["endpoint_length_norm"] = _r(L / head["width"])
    # 根元端点集合は角に偏るので、局所接線に直交する平面で「根元の断面（列/リング全体）」を取り直す
    g0 = geodesic(adj, root_end)
    L0 = max(g for g in g0 if not math.isinf(g))
    if L0 <= 1e-9:
        res["degenerate"] = True
        return res
    c0 = _centroid([P[i] for i in range(n) if g0[i] <= 0.1 * L0])
    c1 = _centroid([P[i] for i in range(n) if 0.2 * L0 <= g0[i] <= 0.35 * L0])
    u = _norm(_sub(c1, c0)) if (c0 is not None and c1 is not None) else (0.0, 0.0, 0.0)
    if _len(u) < 0.5:
        u = _norm(_sub(_centroid([P[i] for i in endB]) if root_end is endA else _centroid([P[i] for i in endA]),
                       _centroid([P[i] for i in root_end])))
    along = [_dot(_sub(P[i], c0), u) for i in range(n)]
    amin = min(along[i] for i in root_end)
    root_set = [i for i in range(n) if along[i] <= amin + 0.05 * L0]
    if len(root_set) < 1:
        root_set = root_end
    geo = geodesic(adj, root_set)
    geo_max = max(g for g in geo if not math.isinf(g))
    if geo_max <= 1e-9:
        res["degenerate"] = True
        return res
    t = [min(1.0, g / geo_max) if not math.isinf(g) else 1.0 for g in geo]

    # 中心線（t を 10 区分した重心）
    NB = 10
    bins = [[] for _ in range(NB)]
    for i, tv in enumerate(t):
        bins[min(NB - 1, int(tv * NB))].append(i)
    cent = [(_centroid([P[i] for i in b]) if b else None) for b in bins]
    for k in range(NB):
        if cent[k] is None:
            # 近傍の非空 bin で補う
            for d in range(1, NB):
                if k - d >= 0 and cent[k - d] is not None:
                    cent[k] = cent[k - d]
                    break
                if k + d < NB and cent[k + d] is not None:
                    cent[k] = cent[k + d]
                    break
    root_c = _centroid([P[i] for i in range(n) if t[i] <= cfg["root_band_frac"]]) or cent[0]
    tip_c = _centroid([P[i] for i in range(n) if t[i] >= 1.0 - cfg["root_band_frac"]]) or cent[-1]
    mid_c = cent[NB // 2]

    # 長さ・曲がり
    length_geo = geo_max
    length_straight = _dist(root_c, tip_c)
    poly_len = 0.0
    turn = 0.0
    prev_seg = None
    for k in range(NB - 1):
        seg = _sub(cent[k + 1], cent[k])
        sl = _len(seg)
        if sl < 1e-9:
            continue
        poly_len += sl
        if prev_seg is not None:
            a = _angle_deg(prev_seg, seg)
            if a is not None:
                turn += a
        prev_seg = seg
    W = head["width"]
    res["length_geo_norm"] = _r(length_geo / W)
    res["length_straight_norm"] = _r(length_straight / W)
    res["straightness"] = _r(length_straight / poly_len) if poly_len > 1e-9 else None
    res["turn_total_deg"] = _r(turn, 1)

    # 幅・厚み（5 帯）
    hw = cfg["band_half_width"]
    tcs = [0.0, 0.25, 0.5, 0.75, 1.0]
    labels = ["t0", "t25", "t50", "t75", "t100"]
    widths, thicks, naxes = {}, {}, []
    for tc, lab in zip(tcs, labels):
        lo, hi = max(0.0, tc - hw), min(1.0, tc + hw)
        if tc == 0.0:
            hi = 2 * hw
        if tc == 1.0:
            lo = 1.0 - 2 * hw
        band = [i for i in range(n) if lo <= t[i] <= hi]
        bi = min(NB - 1, int(tc * NB))
        tang = _sub(cent[min(bi + 1, NB - 1)], cent[max(bi - 1, 0)])
        if _len(tang) < 1e-9:
            tang = _sub(tip_c, root_c)
        prof = band_profile([P[i] for i in band], tang) if len(band) >= 2 else None
        if prof is None:
            widths[lab] = None
            thicks[lab] = None
            naxes.append(None)
        else:
            widths[lab] = _r(prof[0] / W)
            thicks[lab] = _r(prof[1] / W)
            naxes.append(prof[3])  # 幅軸（ねじれ推定用。法線軸だと曲げがねじれに混入する）
    res["width_norm"] = widths
    res["thickness_norm"] = thicks
    if widths["t0"] and widths["t100"] is not None:
        res["root_tip_width_ratio"] = _r(widths["t0"] / widths["t100"]) if widths["t100"] and widths["t100"] > 1e-6 else None
    else:
        res["root_tip_width_ratio"] = None
    mid_ratio = None
    if widths["t50"] and widths["t50"] > 1e-9 and thicks["t50"] is not None:
        mid_ratio = thicks["t50"] / widths["t50"]
    res["mid_thickness_ratio"] = _r(mid_ratio)

    # 幅プロファイル（板の中膨れ。width_profile_v1）
    w0, w50, w100 = widths["t0"], widths["t50"], widths["t100"]
    if w0 and w0 > 1e-9:
        res["width_profile_norm"] = [(_r(widths[lab] / w0, 3) if widths[lab] is not None else None)
                                     for lab in labels]
    else:
        res["width_profile_norm"] = None
    bulge = waist = curvature = None
    if w0 is not None and w50 is not None and w100 is not None:
        hi_end, lo_end = max(w0, w100), min(w0, w100)
        if hi_end > 1e-9:
            bulge = w50 / hi_end          # >1 なら中央が「両端のどちらよりも」太い＝本物の中膨れ
        if lo_end > 1e-9:
            waist = w50 / lo_end          # <1 なら中央が「両端のどちらよりも」細い＝本物の中細り
        ends_mean = (w0 + w100) / 2.0
        if ends_mean > 1e-9:
            curvature = w50 / ends_mean   # テーパ曲線の凸性（中膨れの判定には使わない）
    res["width_bulge_ratio"] = _r(bulge, 3)
    res["width_waist_ratio"] = _r(waist, 3)
    res["width_curvature_ratio"] = _r(curvature, 3)
    res["width_profile_guess"] = width_profile_guess(bulge, waist, res["root_tip_width_ratio"], cfg)

    # ねじれ（帯ごとの幅軸の回転量、直線角。面内曲がりも混入し得る推定値）
    twist = 0.0
    prev = None
    for ax in naxes:
        if ax is None:
            continue
        if prev is not None:
            a = _line_angle_deg(prev, ax)
            if a is not None:
                twist += a
        prev = ax
    res["twist_total_deg"] = _r(twist, 1)

    # リング閉包・断面推定
    root_band = {i for i in range(n) if t[i] <= cfg["root_band_frac"]}
    tip_band = {i for i in range(n) if t[i] >= 1.0 - cfg["root_band_frac"]}
    root_closed, root_ring_n = _ring_closed(root_band, loc_boundary)
    tip_closed, tip_ring_n = _ring_closed(tip_band, loc_boundary)
    res["root_ring_closed"] = root_closed
    res["root_ring_vertex_count"] = root_ring_n if root_closed else None
    res["tip_ring_closed"] = tip_closed
    bvf = len(boundary_verts) / n
    if mid_ratio is not None and mid_ratio < 0.25:
        cs = "flat_card"
    elif bvf < 0.35 or root_closed:
        cs = "closed_tube"
    else:
        cs = "unknown"
    res["cross_section_guess"] = cs

    # 格子推定
    cols = rows = None
    grid_fit = None
    quad_frac = ft["quad"] / max(1, sum(ft.values()))
    if root_closed and root_ring_n >= 3:
        cols = root_ring_n
        rr = n / cols
        grid_fit = abs(rr - round(rr)) < 0.05 and quad_frac > 0.9
        rows = int(round(rr)) if grid_fit else _r(rr, 2)
    else:
        b = len(boundary_verts)
        S = (b + 4) / 2.0
        disc = S * S - 4.0 * n
        if disc >= 0:
            x1 = (S - math.sqrt(disc)) / 2.0
            x2 = (S + math.sqrt(disc)) / 2.0
            grid_fit = (abs(x1 - round(x1)) < 0.05 and abs(x2 - round(x2)) < 0.05 and quad_frac > 0.9)
            cols = int(round(x1)) if grid_fit else _r(x1, 2)
            rows = int(round(x2)) if grid_fit else _r(x2, 2)
    res["columns_estimate"] = cols
    res["rows_estimate"] = rows
    res["grid_fit"] = grid_fit
    res["quad_fraction"] = _r(quad_frac)

    # 毛先分岐
    tip_allowed = {i for i in range(n) if t[i] >= cfg["tip_split_t"]}
    comps = _components(sorted(tip_allowed), adj, tip_allowed)
    res["tip_split_count"] = len([c for c in comps if len(c) >= 2])

    # 位置（正規化）
    root_n = hnorm(head, root_c)
    tip_n = hnorm(head, tip_c)
    res["root_world"] = _rt(root_c)
    res["tip_world"] = _rt(tip_c)
    res["root_norm"] = _rt(root_n, 3)
    res["tip_norm"] = _rt(tip_n, 3)
    res["root_radial"] = _r(hradial(head, root_c), 3)
    res["mid_radial"] = _r(hradial(head, mid_c), 3)
    res["tip_radial"] = _r(hradial(head, tip_c), 3)
    mid_n = hnorm(head, mid_c)
    res["mid_norm"] = _rt(mid_n, 3)
    # 層は水平半径で測る（3D 半径には z が入り、垂れた長さを層と誤認する）
    res["root_radial_h"] = _r(math.sqrt(root_n[0] ** 2 + root_n[1] ** 2), 3)
    res["mid_radial_h"] = _r(math.sqrt(mid_n[0] ** 2 + mid_n[1] ** 2), 3)
    res["tip_radial_h"] = _r(math.sqrt(tip_n[0] ** 2 + tip_n[1] ** 2), 3)
    res["layer_guess"] = layer_guess(res["mid_radial_h"], mid_n[2], cfg)
    res["region_guess"] = region_guess(root_n, tip_n, fa_spec)

    # UV
    uvres = None
    if md.uv is not None:
        ts, us, vs = [], [], []
        for f in fidx:
            for li in face_loops[f]:
                v = md.loops_vert[li]
                if v in local:
                    ts.append(t[local[v]])
                    u, vv = md.uv[li]
                    us.append(u)
                    vs.append(vv)
        if len(ts) >= 4:
            uvres = {"bbox": [_r(min(us)), _r(min(vs)), _r(max(us)), _r(max(vs))]}
            # numpy の有無で分岐しない（0.1.0 は numpy 非搭載環境で along キーが作られず、
            # analyze_mesh の UV サマリーが KeyError になった）
            cu = _corr(ts, us)
            cv = _corr(ts, vs)
            uvres["corr_u"] = _r(cu, 3)
            uvres["corr_v"] = _r(cv, 3)
            if cu is None and cv is None:
                uvres["along"] = None
                uvres["tip_dir"] = None
            else:
                au = abs(cu) if cu is not None else -1
                av = abs(cv) if cv is not None else -1
                if av >= au:
                    uvres["along"] = "V"
                    uvres["tip_dir"] = "-V" if cv < 0 else "+V"
                else:
                    uvres["along"] = "U"
                    uvres["tip_dir"] = "-U" if cu < 0 else "+U"
    res["uv"] = uvres

    # 頂点グループ（ウェイト）
    if md.vgroups:
        dom = []
        gset = {}
        per_vert_cnt = []
        for i, v in enumerate(vidx):
            gs = md.vgroups[v] if v < len(md.vgroups) else []
            sig = [(g, w) for g, w in gs if w >= 0.01]
            per_vert_cnt.append(len(sig))
            if gs:
                g, w = max(gs, key=lambda x: x[1])
                dom.append(g)
            else:
                dom.append(None)
            for g, w in gs:
                if w >= 0.05:
                    gset[g] = max(gset.get(g, 0.0), w)

        def gname(g):
            if g is None:
                return None
            return md.group_names[g] if g < len(md.group_names) else "group_%d" % g

        root_dom, _c = _mode([dom[i] for i in root_band])
        tip_dom, _c2 = _mode([dom[i] for i in tip_band])
        res["groups"] = {
            "count_sig": len(gset),
            "names": sorted([gname(g) for g in gset], key=lambda s: str(s)),
            "root_dominant": gname(root_dom),
            "tip_dominant": gname(tip_dom),
            "per_vertex_mean": _r(sum(per_vert_cnt) / max(1, len(per_vert_cnt)), 2),
        }

        # 根元固定帯（root_lock_v1）: Head 系ウェイトが 1.0 のまま続く t の長さ
        head_gidx = _head_group_indices(md, cfg)
        lock_t = rel_t = None
        hw_profile = None
        if head_gidx:
            hw = []
            for i, v in enumerate(vidx):
                gs = md.vgroups[v] if v < len(md.vgroups) else []
                hw.append(max([w for g, w in gs if g in head_gidx] or [0.0]))
            NBW = max(4, int(cfg["weight_bins"]))
            wbins = [[] for _ in range(NBW)]
            for i in range(n):
                wbins[min(NBW - 1, int(t[i] * NBW))].append(hw[i])
            thr = cfg["root_lock_weight"]
            need = cfg["root_lock_fraction"]
            lock_t = 0.0
            for k in range(NBW):
                b = wbins[k]
                if not b:
                    continue  # 空帯は判定を保留（break しない）
                if sum(1 for x in b if x >= thr) / len(b) >= need:
                    lock_t = (k + 1) / float(NBW)
                else:
                    break
            for k in range(NBW):
                b = wbins[k]
                if b and (sum(b) / len(b)) < 0.01:
                    rel_t = k / float(NBW)
                    break
            hw_profile = []
            for tc in (0.0, 0.25, 0.5, 0.75, 1.0):
                k0 = min(NBW - 1, int(tc * NBW))
                b = None
                for d in range(NBW):  # 疎なメッシュでは帯が空になるので近い非空帯で埋める
                    for k in (k0 - d, k0 + d):
                        if 0 <= k < NBW and wbins[k]:
                            b = wbins[k]
                            break
                    if b:
                        break
                hw_profile.append(_r(sum(b) / len(b), 3) if b else None)
            res["groups"]["head_weight_max"] = _r(max(hw), 3)
            res["groups"]["head_influenced"] = bool(max(hw) >= 0.5)
        else:
            res["groups"]["head_weight_max"] = None
            res["groups"]["head_influenced"] = None
        res["groups"]["head_group_names"] = sorted(gname(g) for g in head_gidx)
        res["groups"]["root_lock_t"] = _r(lock_t, 3) if lock_t is not None else None
        res["groups"]["head_release_t"] = _r(rel_t, 3) if rel_t is not None else None
        res["groups"]["head_weight_profile"] = hw_profile
    else:
        res["groups"] = None

    # 法線（放射方向との角度）
    geo_ang = []
    cus_ang = []
    cvg_ang = []
    cn_acc = defaultdict(lambda: [0.0, 0.0, 0.0])
    if md.corner_normals is not None:
        for f in fidx:
            for li in face_loops[f]:
                v = md.loops_vert[li]
                c = md.corner_normals[li]
                a = cn_acc[v]
                a[0] += c[0]
                a[1] += c[1]
                a[2] += c[2]
    for i, v in enumerate(vidx):
        rad = _sub(P[i], head["center"])
        gn = md.vnormals[v] if v < len(md.vnormals) else None
        if gn is not None:
            a = _angle_deg(gn, rad)
            if a is not None:
                geo_ang.append(a)
        if v in cn_acc:
            cn = tuple(cn_acc[v])
            a = _angle_deg(cn, rad)
            if a is not None:
                cus_ang.append(a)
            if gn is not None:
                a2 = _angle_deg(cn, gn)
                if a2 is not None:
                    cvg_ang.append(a2)
    res["normals"] = {
        "geo_radial_mean_deg": _r(sum(geo_ang) / len(geo_ang), 1) if geo_ang else None,
        "custom_radial_mean_deg": _r(sum(cus_ang) / len(cus_ang), 1) if cus_ang else None,
        "custom_vs_geo_mean_deg": _r(sum(cvg_ang) / len(cvg_ang), 1) if cvg_ang else None,
    }
    return res


def analyze_mesh(md, head, cfg, start_id=0):
    """メッシュを連結成分（＝房候補）に分解して各房を解析する。戻り値: (strands, uv_info)"""
    n = len(md.verts)
    fa_spec = front_axis_spec(cfg["front_axis"])

    edge_face_count = Counter()
    for f in md.faces:
        m = len(f)
        for k in range(m):
            a, b = f[k], f[(k + 1) % m]
            edge_face_count[(a, b) if a < b else (b, a)] += 1
    edge_set = set(edge_face_count.keys())
    for a, b in md.edges:
        edge_set.add((a, b) if a < b else (b, a))
    boundary_edge_set = {e for e, c in edge_face_count.items() if c == 1}

    parent = list(range(n))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for a, b in edge_set:
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb
    comp = defaultdict(list)
    for i in range(n):
        comp[find(i)].append(i)
    islands = sorted(comp.values(), key=lambda vs: min(vs))
    isl_of = [0] * n
    for ii, vs in enumerate(islands):
        for v in vs:
            isl_of[v] = ii
    face_loops = [[] for _ in md.faces]
    for li, fi in enumerate(md.loops_face):
        face_loops[fi].append(li)
    faces_of = defaultdict(list)
    for fi, f in enumerate(md.faces):
        if f:
            faces_of[isl_of[f[0]]].append(fi)
    # 辺は 1 パスで島ごとに振り分ける（辺の両端は必ず同じ島に属する）
    edges_of = defaultdict(list)
    for a, b in edge_set:
        edges_of[isl_of[a]].append((a, b))
    bedges_of = defaultdict(list)
    for a, b in boundary_edge_set:
        bedges_of[isl_of[a]].append((a, b))

    strands = []
    for ii, vs in enumerate(islands):
        s = analyze_island(md, vs, faces_of.get(ii, []), face_loops,
                           edges_of.get(ii, []), bedges_of.get(ii, []),
                           head, cfg, fa_spec, start_id + ii, md.name)
        strands.append(s)

    # UV 全体（オブジェクト単位）: bbox 集合から占有率と共有ペア数
    uv_info = None
    if md.uv is not None:
        boxes = [s["uv"]["bbox"] for s in strands if s.get("uv") and s["uv"].get("bbox")]
        G = 64
        cells = set()
        for b in boxes:
            u0, v0, u1, v1 = b
            for gx in range(max(0, int(u0 * G)), min(G - 1, int(u1 * G)) + 1):
                for gy in range(max(0, int(v0 * G)), min(G - 1, int(v1 * G)) + 1):
                    cells.add((gx, gy))
        shared = 0
        if len(boxes) <= 2000:
            for i in range(len(boxes)):
                a = boxes[i]
                aa = max(1e-9, (a[2] - a[0]) * (a[3] - a[1]))
                for j in range(i + 1, len(boxes)):
                    b = boxes[j]
                    iw = min(a[2], b[2]) - max(a[0], b[0])
                    ih = min(a[3], b[3]) - max(a[1], b[1])
                    if iw <= 0 or ih <= 0:
                        continue
                    inter = iw * ih
                    ba = max(1e-9, (b[2] - b[0]) * (b[3] - b[1]))
                    if inter / (aa + ba - inter) > 0.5:
                        shared += 1
        else:
            shared = None
        uv_info = {
            "object": md.name,
            "coverage_est": _r(len(cells) / float(G * G)),
            "shared_bbox_pairs": shared,
            "along_mode": _mode([s["uv"]["along"] for s in strands if s.get("uv")])[0],
            "tip_dir_mode": _mode([s["uv"]["tip_dir"] for s in strands if s.get("uv")])[0],
        }
    return strands, uv_info


ENVELOPE_BANDS = [
    # (ラベル, z_norm 下限(排他), z_norm 上限(含む))。頭部基準に固定＝サンプル間で直接比較できる
    ("above_crown", 1.2, None),
    ("crown", 0.6, 1.2),
    ("upper", 0.0, 0.6),
    ("eye", -0.6, 0.0),
    ("jaw", -1.2, -0.6),
    ("neck", -2.0, -1.2),
    ("shoulder", -3.0, -2.0),
    ("below", None, -3.0),
]


def mesh_surface_points(md):
    """面に属する頂点だけを返す。(points, 除外したルーズ頂点数)

    エンベロープは全頂点で測ると、孤立頂点 1 個で bbox と top/bottom_z_norm が
    決まってしまう（房の解析側はそれを退化島として既に除外している）。
    """
    used = set()
    for f in md.faces:
        used.update(f)
    pts = [md.verts[v] for v in sorted(used) if 0 <= v < len(md.verts)]
    return pts, len(md.verts) - len(pts)


def envelope_metrics(points, head, cfg):
    """髪全体のシルエット指標（envelope_v1）。points はワールド座標の全頂点。
    bbox は頭幅=1.0 単位、水平半径は頭部楕円半径単位（1.0=頭皮面）。"""
    pts = [p for p in points if p is not None]
    if len(pts) < 4:
        return None
    W = head["width"]
    mn = tuple(min(p[i] for p in pts) for i in range(3))
    mx = tuple(max(p[i] for p in pts) for i in range(3))
    ctr = tuple((mn[i] + mx[i]) / 2.0 for i in range(3))
    qs = [hnorm(head, p) for p in pts]
    rad = [math.sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2]) for q in qs]
    horiz = [math.sqrt(q[0] * q[0] + q[1] * q[1]) for q in qs]
    rs = _stats(rad, 3)
    bands = []
    minpts = cfg["envelope_min_band_points"]
    for lab, lo, hi in ENVELOPE_BANDS:
        sel = [horiz[i] for i, q in enumerate(qs)
               if (lo is None or q[2] > lo) and (hi is None or q[2] <= hi)]
        if len(sel) < max(1, minpts):
            continue
        st = _stats(sel, 3)
        bands.append({"band": lab, "z_norm_range": [lo, hi], "count": st["count"],
                      "r_p50": st["median"], "r_p90": st["p90"], "r_max": st["max"],
                      "fraction": _r(st["count"] / float(len(pts)), 3)})
    widest = max(bands, key=lambda b: b["r_p90"]) if bands else None
    # 頭部基準が髪全体 bbox の代用だと、髪はその bbox をちょうど埋めるので
    # bbox_norm.x=1.0 / top_z_norm=+1 / bottom_z_norm=-1 / widest_band="crown" が
    # 形状によらず必ずこの値になる。定数を数値として残すと転記されてしまうので埋めない
    degenerate = (head.get("source") == "hair_union_bbox_fallback")
    if degenerate:
        return {
            "point_count": len(pts),
            "head_source": head.get("source"),
            "degenerate_under_fallback": True,
            "note": "頭部基準が髪全体bboxの代用なので、頭部を基準にした量は構造上の定数になり測れない。"
                    "比較可能なのは bbox の縦横比だけ",
            "bbox_aspect_zx": _r((mx[2] - mn[2]) / max(mx[0] - mn[0], 1e-9), 3),
            "bbox_aspect_yx": _r((mx[1] - mn[1]) / max(mx[0] - mn[0], 1e-9), 3),
            "bbox_norm": None, "center_offset_norm": None, "radial": None,
            "horiz_radius_by_z": None, "widest_band": None, "silhouette_ratio": None,
            "top_z_norm": None, "bottom_z_norm": None,
        }
    return {
        "point_count": len(pts),
        # 頭部基準が代用（hair_union_bbox_fallback）だと z 帯は頭部を指さないので、
        # widest_band や silhouette_ratio は他サンプルと比較できない。出所をここに残す
        "head_source": head.get("source"),
        "degenerate_under_fallback": False,
        "bbox_norm": {"x": _r((mx[0] - mn[0]) / W, 3), "y": _r((mx[1] - mn[1]) / W, 3),
                      "z": _r((mx[2] - mn[2]) / W, 3)},
        "center_offset_norm": _rt(hnorm(head, ctr), 3),
        "radial": {"p10": rs["p10"], "median": rs["median"], "p90": rs["p90"], "max": rs["max"]},
        "horiz_radius_by_z": bands,
        "widest_band": widest["band"] if widest else None,
        "silhouette_ratio": widest["r_p90"] if widest else None,
        "top_z_norm": _r(max(q[2] for q in qs), 3),
        "bottom_z_norm": _r(min(q[2] for q in qs), 3),
    }


def spacing_and_mirror(ok, head, cfg):
    """房ピッチ（spacing_v1）と左右対称性（mirror_v1）。
    副作用: 各房 dict に root_spacing_norm を書き込む（by_region で使うため）。"""
    n = len(ok)
    if n == 0:
        return None
    if n > cfg["max_pairwise_strands"]:
        return {"root_spacing_norm": {"count": 0}, "mirror": None,
                "skipped_reason": "strand_count>%d（総当たり計算を省略）" % cfg["max_pairwise_strands"]}
    W = head["width"]
    _f_idx, _sign, s_idx = front_axis_spec(cfg["front_axis"])
    roots = [s.get("root_world") for s in ok]
    lens = [s.get("length_geo_norm") for s in ok]

    spacing = []
    for i in range(n):
        a = roots[i]
        best = None
        if a is not None:
            for j in range(n):
                if i == j or roots[j] is None:
                    continue
                d = _dist(a, roots[j])
                if best is None or d < best:
                    best = d
        v = _r(best / W, 4) if best is not None else None
        ok[i]["root_spacing_norm"] = v
        spacing.append(v)

    c = head["center"][s_idx]
    tol = cfg["mirror_tol_norm"] * W
    ltol = cfg["mirror_length_tol"]
    mid_half = cfg["mirror_midline_norm"]
    matched = considered = midline = 0
    for i in range(n):
        a = roots[i]
        if a is None:
            continue
        rn = ok[i].get("root_norm")
        if rn is not None and abs(rn[s_idx]) < mid_half:
            midline += 1
            ok[i]["mirror_role"] = "midline"
            ok[i]["mirror_partner_id"] = None
            continue
        considered += 1
        m = list(a)
        m[s_idx] = 2.0 * c - a[s_idx]
        m = tuple(m)
        hit = None
        for j in range(n):
            if j == i or roots[j] is None or _dist(m, roots[j]) > tol:
                continue
            la, lb = lens[i], lens[j]
            if la is None or lb is None or abs(lb - la) / max(abs(la), 1e-9) <= ltol:
                hit = ok[j].get("id")
                break
        ok[i]["mirror_partner_id"] = hit
        ok[i]["mirror_role"] = "matched" if hit is not None else "unmatched"
        if hit is not None:
            matched += 1
    return {
        "root_spacing_norm": _stats(spacing, 4),
        "mirror": {
            "axis": "X" if s_idx == 0 else "Y",
            "tol_norm": cfg["mirror_tol_norm"],
            "length_tol": ltol,
            "considered": considered,
            "matched": matched,
            "matched_fraction": _r(matched / float(considered), 3) if considered else None,
            "midline_strand_count": midline,
        },
    }


def aggregate(strands, head, cfg, points=None):
    ok = [s for s in strands if not s.get("degenerate")]
    agg = {
        "strand_count": len(ok),
        "degenerate_islands": len(strands) - len(ok),
        "verts_per_strand": _stats([s["verts"] for s in ok], 1),
        "length_geo_norm": _stats([s.get("length_geo_norm") for s in ok]),
        "length_straight_norm": _stats([s.get("length_straight_norm") for s in ok]),
        "straightness": _stats([s.get("straightness") for s in ok]),
        "turn_total_deg": _stats([s.get("turn_total_deg") for s in ok], 1),
        "twist_total_deg": _stats([s.get("twist_total_deg") for s in ok], 1),
        "root_width_norm": _stats([s["width_norm"]["t0"] for s in ok if s.get("width_norm")]),
        "mid_width_norm": _stats([s["width_norm"]["t50"] for s in ok if s.get("width_norm")]),
        "tip_width_norm": _stats([s["width_norm"]["t100"] for s in ok if s.get("width_norm")]),
        "root_tip_width_ratio": _stats([s.get("root_tip_width_ratio") for s in ok]),
        "cross_section_counts": dict(Counter(s.get("cross_section_guess") for s in ok)),
        "columns_estimate_mode": _mode([s.get("columns_estimate") for s in ok if s.get("grid_fit")])[0],
        "rows_estimate": _stats([s.get("rows_estimate") for s in ok if s.get("grid_fit")], 1),
        "grid_fit_fraction": _r(sum(1 for s in ok if s.get("grid_fit")) / max(1, len(ok))),
        "tip_split_fraction": _r(sum(1 for s in ok if (s.get("tip_split_count") or 0) >= 2) / max(1, len(ok))),
        "region_counts": dict(Counter(s.get("region_guess") for s in ok)),
        "root_radial": _stats([s.get("root_radial") for s in ok], 3),
        "mid_radial": _stats([s.get("mid_radial") for s in ok], 3),
        "tip_z_norm_min": _r(min([s["tip_norm"][2] for s in ok if s.get("tip_norm")] or [0]), 3),
    }
    tz = agg["tip_z_norm_min"]
    if tz is None:
        agg["length_class_guess"] = None
    elif tz > -0.6:
        agg["length_class_guess"] = "very_short"
    elif tz > -1.2:
        agg["length_class_guess"] = "short"
    elif tz > -2.5:
        agg["length_class_guess"] = "medium"
    else:
        agg["length_class_guess"] = "long"

    # --- 第2段 昇格候補指標（0.2.0） ---
    agg["tip_radial"] = _stats([s.get("tip_radial") for s in ok], 3)
    # 層の判定に使うのは水平半径（3D 半径は垂れ下がった長さを層と誤認する）
    agg["root_radial_h"] = _stats([s.get("root_radial_h") for s in ok], 3)
    agg["mid_radial_h"] = _stats([s.get("mid_radial_h") for s in ok], 3)
    agg["tip_radial_h"] = _stats([s.get("tip_radial_h") for s in ok], 3)
    agg["width_bulge_ratio"] = _stats([s.get("width_bulge_ratio") for s in ok], 3)
    agg["width_waist_ratio"] = _stats([s.get("width_waist_ratio") for s in ok], 3)
    agg["width_curvature_ratio"] = _stats([s.get("width_curvature_ratio") for s in ok], 3)
    agg["width_profile_counts"] = dict(Counter(s.get("width_profile_guess") for s in ok))
    agg["width_profile_mode"] = _mode([s.get("width_profile_guess") for s in ok])[0]
    agg["width_profile_median"] = _profile_median([s.get("width_profile_norm") for s in ok])
    agg["layer_counts"] = dict(Counter(s.get("layer_guess") for s in ok))
    sm = spacing_and_mirror(ok, head, cfg)  # ここで各房に root_spacing_norm が入る
    agg["root_spacing_norm"] = sm["root_spacing_norm"] if sm else {"count": 0}
    agg["mirror"] = sm["mirror"] if sm else None
    if sm and sm.get("skipped_reason"):
        agg["spacing_skipped_reason"] = sm["skipped_reason"]
    _rw = (agg["root_width_norm"] or {}).get("median")
    _rs = (agg["root_spacing_norm"] or {}).get("median")
    agg["root_pitch_ratio"] = _r(_rw / _rs, 3) if (_rw and _rs and _rs > 1e-9) else None
    agg["envelope"] = envelope_metrics(points, head, cfg) if points else None

    # 領域別
    by_region = {}
    for reg in sorted(set(s.get("region_guess") for s in ok)):
        ss = [s for s in ok if s.get("region_guess") == reg]
        by_region[str(reg)] = {
            "count": len(ss),
            "length_geo_norm": _stats([s.get("length_geo_norm") for s in ss]),
            "root_width_norm": _stats([s["width_norm"]["t0"] for s in ss if s.get("width_norm")]),
            "root_tip_width_ratio": _stats([s.get("root_tip_width_ratio") for s in ss]),
            "width_bulge_ratio": _stats([s.get("width_bulge_ratio") for s in ss], 3),
            "width_profile_mode": _mode([s.get("width_profile_guess") for s in ss])[0],
            "turn_total_deg": _stats([s.get("turn_total_deg") for s in ss], 1),
            "twist_total_deg": _stats([s.get("twist_total_deg") for s in ss], 1),
            "root_radial": _stats([s.get("root_radial") for s in ss], 3),
            "mid_radial": _stats([s.get("mid_radial") for s in ss], 3),
            "tip_radial": _stats([s.get("tip_radial") for s in ss], 3),
            "mid_radial_h": _stats([s.get("mid_radial_h") for s in ss], 3),
            "layer_counts": dict(Counter(s.get("layer_guess") for s in ss)),
            "root_spacing_norm": _stats([s.get("root_spacing_norm") for s in ss], 4),
        }
    agg["by_region"] = by_region

    # ウェイト
    gs = [s["groups"] for s in ok if s.get("groups")]
    hg = [g for g in gs if g.get("head_influenced")]
    if gs:
        agg["weights"] = {
            "groups_per_strand": _stats([g["count_sig"] for g in gs], 1),
            "groups_per_vertex_mean": _stats([g["per_vertex_mean"] for g in gs], 2),
            "root_dominant_mode": _mode([g["root_dominant"] for g in gs])[0],
            "root_dominant_fraction": _r(_mode([g["root_dominant"] for g in gs])[1] / len(gs)),
            "tip_dominant_mode": _mode([g["tip_dominant"] for g in gs])[0],
            "head_groups_detected": sorted({nm for g in gs for nm in (g.get("head_group_names") or [])}),
            # 根元固定帯は「Head 系ウェイトが届いている房」だけで集計する
            # （届いていない房を混ぜると lock_t=0 が中央値を引き下げ、規約を読み違える）
            "head_influenced_fraction": _r(len(hg) / float(len(gs)), 3),
            "root_lock_t": _stats([g.get("root_lock_t") for g in hg], 3),
            "head_release_t": _stats([g.get("head_release_t") for g in hg], 3),
            "head_weight_profile_median": _profile_median([g.get("head_weight_profile") for g in hg]),
        }
    else:
        agg["weights"] = None

    # 法線
    ns = [s["normals"] for s in ok if s.get("normals")]
    if ns:
        def wmean(key):
            num = 0.0
            den = 0
            for s in ok:
                v = s.get("normals", {}).get(key)
                if v is not None:
                    num += v * s["verts"]
                    den += s["verts"]
            return _r(num / den, 1) if den else None
        agg["normals"] = {
            "geo_radial_mean_deg": wmean("geo_radial_mean_deg"),
            "custom_radial_mean_deg": wmean("custom_radial_mean_deg"),
            "custom_vs_geo_mean_deg": wmean("custom_vs_geo_mean_deg"),
        }
    return agg


# ----------------------------------------------------------------------------
# Blender 側（bpy 依存部分はここに閉じ込める）
# ----------------------------------------------------------------------------
def bl_world_bbox(obj):
    from mathutils import Vector
    mw = obj.matrix_world
    pts = [mw @ Vector(c) for c in obj.bound_box]
    return head_from_points([tuple(p) for p in pts], "bbox")


def bl_select_targets(bpy, cfg, warnings):
    objs = []
    if cfg.get("targets"):
        for nm in cfg["targets"]:
            o = bpy.data.objects.get(nm)
            if o is None:
                warnings.append("対象オブジェクトが見つからない: %s" % nm)
            elif o.type != 'MESH':
                warnings.append("メッシュではない: %s (%s)" % (nm, o.type))
            else:
                objs.append(o)
        return objs
    try:
        objs = [o for o in bpy.context.selected_objects if o.type == 'MESH']
    except Exception:
        objs = []
    if objs:
        return objs
    hints = cfg["hair_name_hints"]
    objs = [o for o in bpy.data.objects if o.type == 'MESH' and any(h in o.name for h in hints)]
    if objs:
        warnings.append("選択なし → 名前ヒント一致で対象を決定: %s" % [o.name for o in objs])
    return objs


def bl_find_head(bpy, cfg, target_names, warnings):
    ov = cfg.get("head_bbox_override")
    if ov:
        c = tuple(float(x) for x in ov["center"])
        h = tuple(max(float(x), 1e-6) for x in ov["half"])
        return {"center": c, "half": h, "width": 2 * h[0], "source": "override",
                "bbox_min": tuple(c[i] - h[i] for i in range(3)), "bbox_max": tuple(c[i] + h[i] for i in range(3))}
    if cfg.get("head_object"):
        o = bpy.data.objects.get(cfg["head_object"])
        if o is None:
            warnings.append("head_object が見つからない: %s" % cfg["head_object"])
        else:
            hd = bl_world_bbox(o)
            hd["source"] = "head_object(%s)" % o.name
            return hd
    # 頂点グループ（Head ウェイト）から
    thr = cfg["head_weight_threshold"]
    pairs = []
    if cfg.get("head_vertex_group"):
        pairs.append(tuple(cfg["head_vertex_group"]))
    else:
        hints_l = [h.lower() for h in cfg["head_group_hints"]]
        for ob in bpy.data.objects:
            if ob.type != 'MESH' or ob.name in target_names:
                continue
            for g in ob.vertex_groups:
                if g.name in cfg["head_group_hints"] or g.name.lower() in hints_l:
                    pairs.append((ob.name, g.name))
    best = None
    for obname, gname in pairs:
        ob = bpy.data.objects.get(obname)
        if ob is None or gname not in ob.vertex_groups:
            continue
        gi = ob.vertex_groups[gname].index
        mw = ob.matrix_world
        pts = []
        for v in ob.data.vertices:
            for g in v.groups:
                if g.group == gi and g.weight >= thr:
                    pts.append(tuple(mw @ v.co))
                    break
        if len(pts) >= 50 and (best is None or len(pts) > best[0]):
            best = (len(pts), pts, obname, gname)
    if best:
        hd = head_from_points(best[1], "vertex_group(%s/%s, w>=%.2f, %d verts)" % (best[2], best[3], thr, best[0]))
        ratio = hd["half"][2] / hd["half"][0]
        if ratio > 1.6 or ratio < 0.6:
            warnings.append("頭部bboxの縦横比が不自然 (z/x=%.2f)。首や体が混入している可能性。head_bbox_override を検討" % ratio)
        return hd
    return None


def bl_corner_normals(me, nm):
    vecs = None
    if hasattr(me, "corner_normals"):
        try:
            vecs = [cn.vector.copy() for cn in me.corner_normals]
        except Exception:
            vecs = None
    if vecs is None:
        try:
            me.calc_normals_split()
        except Exception:
            pass
        try:
            vecs = [l.normal.copy() for l in me.loops]
        except Exception:
            return None
    out = []
    for v in vecs:
        w = nm @ v
        out.append(tuple(_norm((w.x, w.y, w.z))))
    return out


def bl_modifier_summary(m):
    d = {"name": m.name, "type": m.type, "show_viewport": bool(m.show_viewport)}
    keys = {
        'MIRROR': ["use_axis", "use_mirror_merge", "merge_threshold", "mirror_object", "use_clip"],
        'SUBSURF': ["levels", "render_levels", "subdivision_type"],
        'SOLIDIFY': ["thickness", "offset", "use_even_offset"],
        'DATA_TRANSFER': ["object", "use_loop_data", "data_types_loops", "use_vert_data", "data_types_verts", "mix_mode"],
        'ARMATURE': ["object", "use_deform_preserve_volume"],
        'CURVE': ["object", "deform_axis"],
        'ARRAY': ["count", "use_relative_offset"],
        'NORMAL_EDIT': ["mode", "target", "mix_mode"],
        'SHRINKWRAP': ["target", "wrap_method", "offset"],
        'WEIGHTED_NORMAL': ["mode", "keep_sharp"],
        'TRIANGULATE': ["quad_method"],
        'DECIMATE': ["ratio", "decimate_type"],
        'SMOOTH': ["factor", "iterations"],
        'DISPLACE': ["strength", "direction"],
        'NODES': ["node_group"],
        'BEVEL': ["width", "segments"],
    }
    for k in keys.get(m.type, []):
        try:
            v = getattr(m, k)
        except Exception:
            continue
        if hasattr(v, "name") and not isinstance(v, str):
            v = v.name
        elif hasattr(v, "__iter__") and not isinstance(v, str):
            try:
                v = [x if isinstance(x, (bool, int, float, str)) else str(x) for x in v]
            except Exception:
                v = str(v)
        elif not isinstance(v, (bool, int, float, str, type(None))):
            v = str(v)
        d[k] = v
    return d


def bl_mesh_data(bpy, obj, cfg, warnings):
    """1 オブジェクトから MeshData と付随情報を作る。"""
    info = {"name": obj.name, "data": obj.data.name, "parent": obj.parent.name if obj.parent else None,
            "collection": obj.users_collection[0].name if obj.users_collection else None}
    info["modifiers"] = [bl_modifier_summary(m) for m in obj.modifiers]
    info["material_slots"] = [ms.material.name if ms.material else None for ms in obj.material_slots]
    info["uv_layers"] = [u.name for u in obj.data.uv_layers]
    info["active_uv_layer"] = obj.data.uv_layers.active.name if obj.data.uv_layers.active else None
    info["vertex_groups"] = [g.name for g in obj.vertex_groups]
    sk = obj.data.shape_keys
    info["shape_keys"] = [k.name for k in sk.key_blocks] if sk else []
    arm = obj.find_armature()
    info["armature"] = arm.name if arm else None
    info["custom_normals_base"] = bool(getattr(obj.data, "has_custom_normals", False))
    info["auto_smooth_legacy"] = getattr(obj.data, "use_auto_smooth", None)
    info["scale"] = _rt(tuple(obj.matrix_world.to_scale()), 4)
    bb = bl_world_bbox(obj)
    info["bbox_world_min"] = _rt(bb["bbox_min"])
    info["bbox_world_max"] = _rt(bb["bbox_max"])

    dg = bpy.context.evaluated_depsgraph_get()
    ob_eval = obj.evaluated_get(dg)
    me_eval = None
    try:
        me_eval = ob_eval.to_mesh(preserve_all_data_layers=True, depsgraph=dg)
        info["custom_normals_evaluated"] = bool(getattr(me_eval, "has_custom_normals", False))
        info["verts_evaluated"] = len(me_eval.vertices)
    except Exception as e:
        warnings.append("%s: 評価後メッシュの取得に失敗 (%s)" % (obj.name, e))
        info["custom_normals_evaluated"] = None

    if cfg["use_evaluated"] and me_eval is not None:
        me = me_eval
        src_obj = ob_eval
        info["geometry_source"] = "evaluated"
    else:
        me = obj.data
        src_obj = obj
        info["geometry_source"] = "base"

    mw = src_obj.matrix_world
    nm = mw.to_3x3().inverted_safe().transposed()
    md = MeshData(obj.name)
    md.verts = [tuple(mw @ v.co) for v in me.vertices]
    md.vnormals = []
    for v in me.vertices:
        w = nm @ v.normal
        md.vnormals.append(tuple(_norm((w.x, w.y, w.z))))
    md.edges = [tuple(e.vertices) for e in me.edges]
    md.faces = [list(p.vertices) for p in me.polygons]
    md.loops_vert = [l.vertex_index for l in me.loops]
    md.loops_face = [-1] * len(me.loops)
    for pi, p in enumerate(me.polygons):
        for li in p.loop_indices:
            md.loops_face[li] = pi
    if me.uv_layers.active:
        md.uv = [tuple(d.uv) for d in me.uv_layers.active.data]
    if getattr(me, "has_custom_normals", False):
        md.corner_normals = bl_corner_normals(me, nm)
    md.group_names = [g.name for g in obj.vertex_groups]
    md.vgroups = [[(g.group, g.weight) for g in v.groups] for v in me.vertices]

    info["verts"] = len(md.verts)
    info["edges"] = len(md.edges)
    info["faces"] = len(md.faces)
    ft = Counter()
    for f in md.faces:
        m = len(f)
        ft["tri" if m == 3 else "quad" if m == 4 else "ngon"] += 1
    info["face_types"] = dict(ft)

    if me_eval is not None:
        try:
            ob_eval.to_mesh_clear()
        except Exception:
            pass
    return md, info, arm


def bl_bone_summary(arm, group_names):
    if arm is None:
        return None
    bones = arm.data.bones
    names = [g for g in group_names if g in bones]
    if not names:
        return {"armature": arm.name, "matched_bones": 0}
    nameset = set(names)
    chain_roots = []
    depths = []
    parents_of_roots = []
    for nmb in names:
        b = bones[nmb]
        depth = 0
        p = b.parent
        while p is not None and p.name in nameset:
            depth += 1
            p = p.parent
        depths.append(depth)
        if depth == 0:
            chain_roots.append(nmb)
            parents_of_roots.append(b.parent.name if b.parent else None)
    return {
        "armature": arm.name,
        "matched_bones": len(names),
        "chain_roots": len(chain_roots),
        "chain_root_examples": chain_roots[:20],
        "chain_root_parent_mode": _mode(parents_of_roots)[0],
        "max_chain_depth": max(depths) + 1 if depths else 0,
        "depth_hist": dict(Counter(d + 1 for d in depths)),
    }


def run_blender(cfg):
    import bpy
    warnings = []
    t0 = datetime.datetime.now()
    objs = bl_select_targets(bpy, cfg, warnings)
    if not objs:
        msg = "[inspect_hair] 対象オブジェクトなし。targets を指定するかメッシュを選択してください。"
        print(msg)
        return None
    target_names = [o.name for o in objs]
    head = bl_find_head(bpy, cfg, target_names, warnings)

    meshes = []
    arms = {}
    for o in objs:
        md, info, arm = bl_mesh_data(bpy, o, cfg, warnings)
        meshes.append((md, info))
        if arm is not None:
            arms[o.name] = arm
    if head is None:
        pts = []
        for md, _i in meshes:
            pts.extend(md.verts)
        if not pts:
            print("[inspect_hair] 対象メッシュに頂点がありません。targets を確認してください。")
            return None
        head = head_from_points(pts, "hair_union_bbox_fallback")
        warnings.append(
            "頭部基準が見つからず髪全体bboxで代用。正規化値の絶対比較は不可（同一設定同士の比較のみ有効）。"
            "envelope はこれより弱く、bbox_norm.x=1.0 / top_z_norm=+1 / bottom_z_norm=-1 / "
            "widest_band=crown が形状によらず必ずそうなる構造上の定数なので、"
            "同一設定同士でも比較できない。該当項目は None にしてある"
            "（比較できるのは bbox_aspect_zx / bbox_aspect_yx だけ）")

    strands = []
    uv_infos = []
    for md, info in meshes:
        ss, uvi = analyze_mesh(md, head, cfg, start_id=len(strands))
        strands.extend(ss)
        if uvi:
            uv_infos.append(uvi)

    bones = {}
    for o in objs:
        arm = arms.get(o.name)
        if arm is not None:
            bones[o.name] = bl_bone_summary(arm, [g.name for g in o.vertex_groups])

    # エンベロープは面に属する頂点だけで測る。ルーズ頂点が 1 個あるだけで
    # bbox と top/bottom_z_norm がそれに支配される（解析側は退化島として除外済み）
    all_points = []
    loose = 0
    for md, _i in meshes:
        pts_s, n_loose = mesh_surface_points(md)
        all_points.extend(pts_s)
        loose += n_loose
    if loose:
        warnings.append("面に属さない頂点 %d 個を envelope の計算から除外した" % loose)
    if cfg["use_evaluated"] and head is not None and str(head.get("source", "")).startswith(
            ("head_object", "vertex_group", "bbox")):
        warnings.append(
            "use_evaluated=True で髪はモディファイア適用後を見ているが、頭部基準は適用前の"
            "メッシュから作っている（%s）。頭部側に Shrinkwrap/Armature 等がかかっていると"
            "正規化の基準がずれる" % head.get("source"))
    if not cfg["use_evaluated"]:
        mirrored = [i["name"] for _md, i in meshes
                    if any((m or {}).get("type") == "MIRROR" for m in (i.get("modifiers") or []))]
        if mirrored:
            warnings.append(
                "Mirror モディファイアが未適用のオブジェクトがある: %s。use_evaluated=False では"
                "片側の形状しか見ていないので、mirror・envelope・房数・房ピッチは実物と一致しない。"
                "use_evaluated=True で再実行すること" % mirrored)
    agg = aggregate(strands, head, cfg, points=all_points)
    out = {
        "meta": {
            "script_version": SCRIPT_VERSION,
            "blender_version": bpy.app.version_string,
            "blend_file": bpy.data.filepath or None,
            "timestamp": t0.isoformat(timespec="seconds"),
            "config": {k: v for k, v in cfg.items() if k != "out"},
            "rules": RULES,
        },
        "head": {
            "source": head["source"],
            "center": _rt(head["center"]),
            "half": _rt(head["half"]),
            "width": _r(head["width"], 5),
        },
        "objects": [info for _md, info in meshes],
        "aggregates": agg,
        "uv": uv_infos,
        "bones": bones,
        "strands": strands[: cfg["max_strands_detail"]],
        "strands_truncated": max(0, len(strands) - cfg["max_strands_detail"]),
        "warnings": warnings,
    }
    out_path = cfg.get("out")
    if not out_path:
        base = os.path.splitext(os.path.basename(bpy.data.filepath))[0] if bpy.data.filepath else "untitled"
        d = os.path.dirname(bpy.data.filepath) if bpy.data.filepath else tempfile.gettempdir()
        out_path = os.path.join(d, "hair_inspect_%s.json" % base)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
    print_summary(out, out_path)
    return out


def print_summary(out, out_path):
    a = out["aggregates"]
    objs = out["objects"]
    hd = out["head"]
    tv = sum(o.get("verts", 0) for o in objs)
    print("[inspect_hair v%s] objects=%d verts=%d islands=%d (degenerate %d)" % (
        out["meta"]["script_version"], len(objs), tv, a["strand_count"] + a["degenerate_islands"], a["degenerate_islands"]))
    print("head: %s width=%s center=%s" % (hd["source"], hd["width"], hd["center"]))
    L = a["length_geo_norm"]
    R = a["root_tip_width_ratio"]
    print("length_norm: median %s (min %s, max %s) straightness median %s | root/tip width ratio median %s | class_guess=%s" % (
        L.get("median"), L.get("min"), L.get("max"), a["straightness"].get("median"), R.get("median"), a["length_class_guess"]))
    print("cross_section_guess: %s | columns mode=%s rows median=%s grid_fit=%s | tip_split=%s" % (
        a["cross_section_counts"], a["columns_estimate_mode"], a["rows_estimate"].get("median"), a["grid_fit_fraction"], a["tip_split_fraction"]))
    print("region_guess: %s" % a["region_counts"])
    wb = a.get("width_bulge_ratio") or {}
    ww = a.get("width_waist_ratio") or {}
    print("width_profile: mode=%s bulge(mid/max_end) median=%s waist(mid/min_end) median=%s | profile(root=1) %s | %s" % (
        a.get("width_profile_mode"), wb.get("median"), ww.get("median"),
        a.get("width_profile_median"), a.get("width_profile_counts")))
    mr = a.get("mid_radial_h") or {}
    print("layers: mid_radial_h p10/med/p90 %s/%s/%s | %s" % (
        mr.get("p10"), mr.get("median"), mr.get("p90"), a.get("layer_counts")))
    sp = a.get("root_spacing_norm") or {}
    mi = a.get("mirror") or {}
    if a.get("spacing_skipped_reason"):
        print("spacing: 省略 (%s)" % a["spacing_skipped_reason"])
    else:
        print("spacing: pitch median=%s width/pitch=%s | mirror axis=%s matched=%s midline=%s" % (
            sp.get("median"), a.get("root_pitch_ratio"), mi.get("axis"),
            mi.get("matched_fraction"), mi.get("midline_strand_count")))
    ev = a.get("envelope")
    if ev:
        bb = ev.get("bbox_norm") or {}
        print("envelope: bbox x/y/z %s/%s/%s (headW=1) | widest=%s r_p90=%s | z top/bottom %s/%s" % (
            bb.get("x"), bb.get("y"), bb.get("z"), ev.get("widest_band"),
            ev.get("silhouette_ratio"), ev.get("top_z_norm"), ev.get("bottom_z_norm")))
    for u in out.get("uv", []):
        print("uv[%s]: along=%s tip_dir=%s coverage=%s shared_pairs=%s" % (
            u["object"], u["along_mode"], u["tip_dir_mode"], u["coverage_est"], u["shared_bbox_pairs"]))
    w = a.get("weights")
    if w:
        print("weights: groups/strand median %s | root dominant %s (%s) | tip dominant %s" % (
            w["groups_per_strand"].get("median"), w["root_dominant_mode"], w["root_dominant_fraction"], w["tip_dominant_mode"]))
        print("root lock: head_groups=%s influenced=%s lock_t median=%s release_t median=%s | head weight profile %s" % (
            w.get("head_groups_detected"), w.get("head_influenced_fraction"),
            (w.get("root_lock_t") or {}).get("median"),
            (w.get("head_release_t") or {}).get("median"), w.get("head_weight_profile_median")))
    for on, b in (out.get("bones") or {}).items():
        if b:
            print("bones[%s]: matched=%s chain_roots=%s parent_mode=%s max_depth=%s" % (
                on, b.get("matched_bones"), b.get("chain_roots"), b.get("chain_root_parent_mode"), b.get("max_chain_depth")))
    nrm = a.get("normals") or {}
    print("normals: custom base/eval=%s/%s | radial angle geo %s° custom %s° | custom-vs-geo %s°" % (
        [o.get("custom_normals_base") for o in objs], [o.get("custom_normals_evaluated") for o in objs],
        nrm.get("geo_radial_mean_deg"), nrm.get("custom_radial_mean_deg"), nrm.get("custom_vs_geo_mean_deg")))
    mods = {o["name"]: [m["type"] for m in o.get("modifiers", [])] for o in objs}
    print("modifiers: %s | shape_keys: %s" % (mods, {o["name"]: len(o.get("shape_keys", [])) for o in objs}))
    if out.get("warnings"):
        print("warnings: %s" % out["warnings"])
    print("json: %s" % out_path)


# ----------------------------------------------------------------------------
# 自己テスト（Blender 不要）
# ----------------------------------------------------------------------------
def _grid_mesh(name, rows, cols, pos_fn, uv_fn, closed=False, groups=None):
    md = MeshData(name)
    uv_of_vert = {}
    for r in range(rows):
        for c in range(cols):
            md.verts.append(pos_fn(r, c))
            uv_of_vert[r * cols + c] = uv_fn(r, c)

    def idx(r, c):
        return r * cols + (c % cols)

    for r in range(rows - 1):
        ncols = cols if closed else cols - 1
        for c in range(ncols):
            md.faces.append([idx(r, c), idx(r, c + 1), idx(r + 1, c + 1), idx(r + 1, c)])
    es = set()
    for f in md.faces:
        for k in range(4):
            a, b = f[k], f[(k + 1) % 4]
            es.add((min(a, b), max(a, b)))
    md.edges = sorted(es)
    md.uv = []
    for fi, f in enumerate(md.faces):
        for v in f:
            md.loops_vert.append(v)
            md.loops_face.append(fi)
            md.uv.append(uv_of_vert[v])
    md.vnormals = [_norm(p) for p in md.verts]  # 原点からの放射方向を幾何法線の代わりに
    if groups:
        md.group_names = groups["names"]
        md.vgroups = [groups["fn"](r, c) for r in range(rows) for c in range(cols)]
    return md


def _merge_meshes(name, *mds):
    """複数の MeshData を 1 メッシュに結合（頂点グループ名は和集合に付け替える）。"""
    out = MeshData(name)
    names = []
    off = 0
    have_uv = all(m.uv is not None for m in mds)
    if have_uv:
        out.uv = []
    for m in mds:
        gmap = {}
        for gi, nm in enumerate(m.group_names):
            if nm not in names:
                names.append(nm)
            gmap[gi] = names.index(nm)
        out.verts.extend(m.verts)
        out.vnormals.extend(m.vnormals)
        out.edges.extend([(a + off, b + off) for a, b in m.edges])
        fbase = len(out.faces)
        out.faces.extend([[v + off for v in f] for f in m.faces])
        out.loops_vert.extend([v + off for v in m.loops_vert])
        out.loops_face.extend([f + fbase for f in m.loops_face])
        if have_uv:
            out.uv.extend(m.uv)
        if m.vgroups:
            out.vgroups.extend([[(gmap[g], w) for g, w in vs] for vs in m.vgroups])
        else:
            out.vgroups.extend([[] for _ in m.verts])
        off += len(m.verts)
    out.group_names = names
    if not any(out.vgroups):
        out.vgroups = []
    return out


def selftest():
    head = {"center": (0.0, 0.0, 0.0), "half": (0.1, 0.1, 0.12), "width": 0.2, "source": "selftest"}
    cfg = dict(DEFAULTS)
    fails = []
    skipped = []
    has_np = np is not None
    if not has_np:
        print("  ※ numpy が無い環境です。幅・厚み・断面（PCA 依存）の検査は SKIP します。")

    def check(cond, msg):
        print(("  OK  " if cond else "  NG  ") + msg)
        if not cond:
            fails.append(msg)

    def check_np(fn, msg):
        """numpy が要る検査。無い環境では評価せず SKIP と表示する。"""
        if not has_np:
            print("  SKIP  " + msg + "（numpy 非搭載）")
            skipped.append(msg)
            return
        check(fn(), msg)

    # 1) 平板カード 4列×12行、前髪位置、先細り、UVはVが毛先で減少
    rows, cols = 12, 4

    def card_pos(r, c):
        s = r / (rows - 1)
        width = 0.03 * (1 - 0.7 * s)
        x = (c / (cols - 1) - 0.5) * width
        return (x, -0.09 - 0.02 * s - 0.03 * s * s, 0.08 - 0.15 * s)

    def card_uv(r, c):
        return (c / (cols - 1), 1.0 - r / (rows - 1))

    def card_groups(r, c):
        s = r / (rows - 1)
        if s < 0.2:
            return [(0, 1.0)]
        return [(0, max(0.0, 1 - s)), (1, min(1.0, s))]

    card = _grid_mesh("Hair_card", rows, cols, card_pos, card_uv, groups={"names": ["Head", "Hair_1"], "fn": card_groups})
    strands, uvi = analyze_mesh(card, head, cfg)
    s = strands[0]
    print("[card]", json.dumps({k: s[k] for k in ("verts", "cross_section_guess", "columns_estimate", "rows_estimate",
                                                   "grid_fit", "width_norm", "length_geo_norm", "region_guess",
                                                   "tip_split_count", "root_ring_closed", "turn_total_deg")}, ensure_ascii=False))
    check(len(strands) == 1, "card: 1 island")
    check_np(lambda: s["cross_section_guess"] == "flat_card", "card: flat_card")
    check(s["columns_estimate"] == 4 and s["rows_estimate"] == 12 and s["grid_fit"], "card: 4 cols x 12 rows")
    check_np(lambda: abs(s["width_norm"]["t0"] - 0.15) < 0.03, "card: root width ~0.15 (0.03/0.2)")
    check_np(lambda: s["width_norm"]["t100"] < s["width_norm"]["t0"] * 0.5,
             "card: tip narrower than half of root")
    check(s["region_guess"] == "bangs", "card: region bangs")
    check(s["uv"]["along"] == "V" and s["uv"]["tip_dir"] == "-V", "card: uv along V, decreasing to tip")
    check(s["groups"]["root_dominant"] == "Head" and s["groups"]["tip_dominant"] == "Hair_1", "card: root=Head tip=Hair_1")
    check(s["tip_split_count"] == 1, "card: no tip split")
    check(uvi["along_mode"] == "V", "card: uv summary")
    # 0.2.0: 幅プロファイル / レイヤー / 根元固定帯
    print("[card2]", json.dumps({k: s[k] for k in ("width_profile_norm", "width_bulge_ratio",
                                                   "width_profile_guess", "mid_radial", "layer_guess")},
                                ensure_ascii=False), json.dumps(
        {k: s["groups"][k] for k in ("head_group_names", "root_lock_t", "head_release_t",
                                     "head_weight_profile")}, ensure_ascii=False))
    check_np(lambda: s["width_profile_guess"] == "taper_linear", "card: width profile taper_linear")
    check_np(lambda: s["width_profile_norm"][0] == 1.0 and s["width_profile_norm"][4] < 0.5,
             "card: width profile normalized to root, tip < 0.5")
    check_np(lambda: s["width_bulge_ratio"] < 1.1 and s["width_waist_ratio"] > 0.9,
             "card: neither mid bulge nor waist")
    check(s["layer_guess"] in ("scalp", "mid", "outer", "below_head"), "card: layer_guess set")
    check(s["groups"]["head_group_names"] == ["Head"], "card: head group detected")
    check(0.05 <= (s["groups"]["root_lock_t"] or 0) <= 0.35, "card: root lock band ~0.2")
    check(s["groups"]["head_weight_profile"][0] == 1.0, "card: root head weight 1.0")
    check((s["groups"]["head_release_t"] or 0) >= 0.8, "card: head weight released near tip")
    check(s["groups"]["head_influenced"] is True and s["groups"]["head_weight_max"] == 1.0,
          "card: head influenced, max weight 1.0")
    check(all(v is not None for v in s["groups"]["head_weight_profile"])
          and s["groups"]["head_weight_profile"][4] == 0.0,
          "card: head weight profile filled (5 bands, tip 0)")

    # 1b) 中央が膨らんだ板（板の中膨れ検出）
    def bulge_pos(r, c):
        sb = r / (rows - 1)
        width = 0.02 * (1.0 + 1.2 * math.sin(math.pi * sb))
        x = (c / (cols - 1) - 0.5) * width
        return (x, -0.09 - 0.02 * sb, 0.08 - 0.15 * sb)

    bcard = _grid_mesh("Hair_bulge", rows, cols, bulge_pos, card_uv)
    sbg = analyze_mesh(bcard, head, cfg)[0][0]
    print("[bulge]", json.dumps({k: sbg[k] for k in ("width_profile_norm", "width_bulge_ratio",
                                                     "width_profile_guess", "root_tip_width_ratio")},
                                ensure_ascii=False))
    check_np(lambda: sbg["width_profile_guess"] == "mid_bulge", "bulge: mid_bulge detected")
    check_np(lambda: sbg["width_bulge_ratio"] > 1.15, "bulge: bulge ratio > 1.15")
    check_np(lambda: sbg["width_profile_norm"][2] > sbg["width_profile_norm"][0],
             "bulge: t50 wider than root")

    # 1c) 回帰テスト: 判定ルールを関数レベルで直接検査する
    #     （どちらも 0.2.0 の初版で実際に誤判定していた。メッシュを組まずに固定できる）
    print("[rules] width_profile_v2 / layer_v2 の回帰テスト")
    # 単調に細るだけの板は、中央がどれだけ凸でも mid_bulge/waisted にしてはいけない
    for w0, w50, w100, want in (
            (1.00, 0.55, 0.10, "taper_linear"),   # 線形
            (1.00, 0.30, 0.10, "taper_linear"),   # 序盤で急に細る（v1 は waisted と誤判定）
            (1.00, 0.90, 0.10, "taper_linear"),   # 終盤で急に細る（v1 は mid_bulge と誤判定）
            (0.50, 1.00, 0.50, "mid_bulge"),      # 本物の中膨れ
            (1.00, 0.40, 1.00, "waisted"),        # 本物の中細り
            (0.10, 0.55, 1.00, "flare"),          # 毛先広がり
            (1.00, 1.00, 1.00, "uniform"),        # 一定
    ):
        b = w50 / max(w0, w100)
        wa = w50 / min(w0, w100)
        got = width_profile_guess(b, wa, w0 / w100, cfg)
        check(got == want, "rules: width %.2f/%.2f/%.2f -> %s (got %s)" % (w0, w50, w100, want, got))
    # 監査で実測された誤判定プロファイルをそのまま検査に入れる。
    # v1 は 1.453(偽陽性) > 1.193(本物の中膨れ) だったので、どの閾値でも分離できなかった
    for prof, want, note in (
            ([1.0, 1.0, 1.0, 0.64, 0.28], "taper_linear", "幅を保ってから急に細る板（v1 は mid_bulge）"),
            ([1.0, 0.959, 0.968, 0.731, 0.332], "taper_linear", "同上・実測値（v1 は mid_bulge）"),
            ([1.0, 0.705, 0.349, 0.148, 0.073], "taper_linear", "急に細って細いまま（v1 は waisted）"),
            ([1.0, 0.576, 0.399, 0.252, 0.15], "taper_linear", "同上・実測値（v1 は waisted）"),
            ([1.0, 1.147, 1.198, 1.115, 1.008], "mid_bulge", "本物の中膨れ（v1 のスコアは偽陽性より低かった）"),
            ([1.0, 0.873, 0.567, 0.240, 0.080], "taper_linear", "強い非線形テーパ（v1 でも正しかった）"),
    ):
        p0, p50, p100 = prof[0], prof[2], prof[4]
        got = width_profile_guess(p50 / max(p0, p100), p50 / min(p0, p100), p0 / p100, cfg)
        check(got == want, "rules: %s -> %s (got %s)" % (note, want, got))

    # 層は水平半径で測る。頭皮に密着した長い房が、外に膨らんだ短い房より外側になってはいけない
    hugging_long = layer_guess(0.90, -1.50, cfg)   # 頭皮沿いだが頭より下まで垂れている
    hugging_short = layer_guess(0.95, 0.0, cfg)
    bulging_short = layer_guess(1.40, 0.0, cfg)
    print("[rules] layer: hugging_long=%s hugging_short=%s bulging_short=%s" % (
        hugging_long, hugging_short, bulging_short))
    check(hugging_short == "scalp", "rules: layer 頭皮沿いは scalp")
    check(bulging_short == "outer", "rules: layer 外に膨らむと outer")
    check(hugging_long == "below_head", "rules: layer 頭部より下は below_head（層は未定義）")
    check(layer_guess(1.749, -1.5, cfg) != "outer",
          "rules: layer 3D半径なら outer になる房を outer と呼ばない")

    # 1d) ねじれ推定: 曲げのみの板は 0 度、軸まわりに 90 度ねじった板は大きな値になる
    #     （verified-facts.md がこの 2 値を引用するので、ここで再現できるようにしておく）
    def twist_pos(r, c):
        st = r / (rows - 1)
        ang = math.radians(90.0 * st)
        u = (c / (cols - 1) - 0.5) * 0.03
        return (u * math.cos(ang), -0.09 + u * math.sin(ang), 0.08 - 0.15 * st)

    s_tw = analyze_mesh(_grid_mesh("Hair_twist", rows, cols, twist_pos, card_uv), head, cfg)[0][0]
    print("[twist] bend_only twist=%s turn=%s | twisted twist=%s turn=%s" % (
        s["twist_total_deg"], s["turn_total_deg"], s_tw["twist_total_deg"], s_tw["turn_total_deg"]))
    check_np(lambda: s["twist_total_deg"] == 0.0, "twist: 曲げのみのカードは 0 度")
    check_np(lambda: s_tw["twist_total_deg"] > 60.0, "twist: 90度ねじりで 60 度超")
    check_np(lambda: s_tw["turn_total_deg"] < 1.0, "twist: ねじりのみのカードは turn 0 度")

    # 1e) 回帰テスト: Head 系グループ名の判定（髪ボーンを頭部と誤認しないこと）
    for nm, want in (("Head", True), ("J_Bip_C_Head", True), ("頭", True), ("HEAD", True),
                     ("HeadHair_01", False), ("Hair_Head_02", False), ("Headband", False),
                     ("Hair_01", False), ("髪_01", False), ("後頭部", False)):
        got = is_head_group_name(nm, cfg)
        check(got == want, "rules: head group %-14s -> %s (got %s)" % (nm, want, got))

    # 1f) 回帰テスト: 頭部基準が代用のとき envelope の頭部依存量を埋めないこと
    fb_pts = [(x * 0.01, y * 0.01, z * 0.05) for x in range(4) for y in range(3) for z in range(6)]
    fb_head = head_from_points(fb_pts, "hair_union_bbox_fallback")
    fb_ev = envelope_metrics(fb_pts, fb_head, cfg)
    print("[fallback] envelope=%s" % json.dumps(
        {k: fb_ev[k] for k in ("degenerate_under_fallback", "bbox_norm", "widest_band",
                               "top_z_norm", "bbox_aspect_zx")}, ensure_ascii=False))
    check(fb_ev["degenerate_under_fallback"] is True, "fallback: 代用であることを記録する")
    check(fb_ev["bbox_norm"] is None and fb_ev["widest_band"] is None
          and fb_ev["top_z_norm"] is None,
          "fallback: 構造上の定数（bbox_norm.x=1 等）は埋めない")
    check(fb_ev["bbox_aspect_zx"] is not None, "fallback: 縦横比だけは残す")

    # 2) 閉じた筒 6角×10行、後頭部位置
    rows2, cols2 = 10, 6

    def tube_pos(r, c):
        s = r / (rows2 - 1)
        rad = 0.008 * (1 - 0.7 * s)
        a = 2 * math.pi * c / cols2
        return (0.02 + rad * math.cos(a), 0.09 + 0.01 * s + rad * math.sin(a), 0.05 - 0.12 * s)

    tube = _grid_mesh("Hair_tube", rows2, cols2, tube_pos, lambda r, c: (c / cols2, r / (rows2 - 1)), closed=True)
    strands2, _ = analyze_mesh(tube, head, cfg)
    s2 = strands2[0]
    print("[tube]", json.dumps({k: s2[k] for k in ("verts", "cross_section_guess", "columns_estimate", "rows_estimate",
                                                    "root_ring_closed", "root_ring_vertex_count", "mid_thickness_ratio",
                                                    "boundary_vertex_fraction", "region_guess")}, ensure_ascii=False))
    check(s2["cross_section_guess"] == "closed_tube", "tube: closed_tube")
    check(s2["root_ring_closed"] and s2["root_ring_vertex_count"] == 6, "tube: root ring 6")
    check(s2["columns_estimate"] == 6 and s2["rows_estimate"] == 10, "tube: 6 x 10")
    check(s2["region_guess"] == "back", "tube: region back")
    check(s2["uv"]["along"] == "V" and s2["uv"]["tip_dir"] == "+V", "tube: uv along V increasing")

    # 3) 毛先が2本に分岐した2列ストリップ（サイド位置）
    md = MeshData("Hair_split")
    main_rows = 9

    def add_v(p):
        md.verts.append(p)
        return len(md.verts) - 1

    rowsv = []
    for r in range(main_rows):
        s = r / 12.0
        y = 0.0
        rowsv.append([add_v((0.095 + 0.0 * s, y - 0.01, 0.06 - 0.2 * s)), add_v((0.095, y + 0.01, 0.06 - 0.2 * s))])
    for br, dx in (("A", 0.0), ("B", 0.03)):
        prev = rowsv[-1]
        for r in range(main_rows, 13):
            s = r / 12.0
            cur = [add_v((0.095 + dx, -0.01 + dx, 0.06 - 0.2 * s)), add_v((0.095 + dx, 0.01 + dx, 0.06 - 0.2 * s))]
            md.faces.append([prev[0], prev[1], cur[1], cur[0]])
            prev = cur
    for r in range(main_rows - 1):
        a, b = rowsv[r], rowsv[r + 1]
        md.faces.append([a[0], a[1], b[1], b[0]])
    es = set()
    for f in md.faces:
        for k in range(4):
            a, b = f[k], f[(k + 1) % 4]
            es.add((min(a, b), max(a, b)))
    md.edges = sorted(es)
    for fi, f in enumerate(md.faces):
        for v in f:
            md.loops_vert.append(v)
            md.loops_face.append(fi)
    md.vnormals = [_norm(p) for p in md.verts]
    strands3, _ = analyze_mesh(md, head, cfg)
    s3 = strands3[0]
    print("[split]", json.dumps({k: s3[k] for k in ("verts", "tip_split_count", "region_guess", "cross_section_guess", "uv")}, ensure_ascii=False))
    check(len(strands3) == 1, "split: 1 island")
    check(s3["tip_split_count"] == 2, "split: 2 tips")
    check(s3["region_guess"] == "side_+X", "split: region side_+X")
    check(s3["uv"] is None, "split: no uv → None")

    # 4) 複数島 + 退化島 の集計
    both = _merge_meshes("Hair_multi", card, tube)
    both.verts.append((0.3, 0.3, 0.3))  # 孤立頂点（退化島）
    both.vnormals.append((0.0, 0.0, 1.0))
    strands4, _ = analyze_mesh(both, head, cfg)
    surf_pts, n_loose = mesh_surface_points(both)
    agg = aggregate(strands4, head, cfg, points=surf_pts)
    print("[multi] strands=%d degenerate=%d regions=%s cross=%s layers=%s profiles=%s" % (
        agg["strand_count"], agg["degenerate_islands"], agg["region_counts"],
        agg["cross_section_counts"], agg["layer_counts"], agg["width_profile_counts"]))
    check(agg["strand_count"] == 2 and agg["degenerate_islands"] == 1, "multi: 2 strands + 1 degenerate")
    check(agg["length_class_guess"] in ("very_short", "short"), "multi: length class")
    check(sum(agg["layer_counts"].values()) == 2, "multi: layer_counts covers both strands")
    check(agg["mid_radial"].get("p10") is not None and agg["mid_radial"].get("p90") is not None,
          "multi: radial quantiles p10/p90 present")
    check(agg["tip_radial"].get("median") is not None, "multi: tip_radial aggregated")
    check(agg["by_region"]["bangs"]["twist_total_deg"]["count"] == 1
          and agg["by_region"]["bangs"]["turn_total_deg"]["count"] == 1,
          "multi: by_region carries turn/twist")
    check_np(lambda: agg["by_region"]["bangs"]["width_bulge_ratio"]["count"] == 1,
             "multi: by_region carries taper/bulge")
    check((agg.get("envelope") or {}).get("horiz_radius_by_z"), "multi: envelope bands produced")
    # 孤立頂点 (0.3,0.3,0.3) は z_norm=2.5。面に属さないので envelope から外れねばならない
    check(n_loose == 1, "multi: 1 loose vertex detected")
    check(agg["envelope"]["point_count"] == len(both.verts) - 1, "multi: loose vertex excluded from envelope")
    check(agg["envelope"]["top_z_norm"] < 1.0,
          "multi: stray vertex no longer sets envelope top (was 2.5)")
    check(agg["weights"]["head_groups_detected"] == ["Head"], "multi: head group reported in weights")
    check(agg["weights"]["head_influenced_fraction"] == 0.5, "multi: only the card is head-influenced")
    check(abs(agg["weights"]["root_lock_t"]["median"] - 0.2) < 0.06,
          "multi: root lock median from the influenced strand only")
    check(agg["weights"]["head_weight_profile_median"] is not None,
          "multi: head weight profile median produced")

    # 5) 左右ミラー配置（房ピッチ・対称性・エンベロープ）
    def mirrored(dx, flip):
        def pos(r, c):
            p = card_pos(r, c)
            x = p[0] + dx
            return (-x if flip else x, p[1], p[2])
        return _grid_mesh("Hair_mirror", rows, cols, pos, card_uv,
                          groups={"names": ["Head", "Hair_1"], "fn": card_groups})

    pair = _merge_meshes("Hair_pair", mirrored(0.05, False), mirrored(0.05, True))
    strands5, _ = analyze_mesh(pair, head, cfg)
    agg5 = aggregate(strands5, head, cfg, points=mesh_surface_points(pair)[0])
    ev = agg5["envelope"]
    print("[pair] spacing=%s pitch_ratio=%s mirror=%s" % (
        agg5["root_spacing_norm"].get("median"), agg5["root_pitch_ratio"],
        json.dumps(agg5["mirror"], ensure_ascii=False)))
    print("[pair] envelope=%s" % json.dumps(
        {k: ev[k] for k in ("bbox_norm", "widest_band", "silhouette_ratio", "top_z_norm", "bottom_z_norm")},
        ensure_ascii=False))
    check(agg5["strand_count"] == 2, "pair: 2 strands")
    check(abs(agg5["root_spacing_norm"]["median"] - 0.5) < 0.06, "pair: root pitch 0.1/0.2 = 0.5")
    check_np(lambda: agg5["root_pitch_ratio"] is not None and agg5["root_pitch_ratio"] < 1.0,
             "pair: width/pitch < 1 (根元は重ならない)")
    check(agg5["mirror"]["axis"] == "X", "pair: mirror axis X")
    check(agg5["mirror"]["matched_fraction"] == 1.0 and agg5["mirror"]["considered"] == 2,
          "pair: both strands mirror-matched")
    check(agg5["mirror"]["midline_strand_count"] == 0, "pair: no midline strand")
    check(strands5[0].get("mirror_partner_id") == strands5[1]["id"]
          and strands5[0].get("mirror_role") == "matched", "pair: partner id recorded")
    check(abs(ev["bbox_norm"]["x"] - 0.65) < 0.06, "pair: envelope bbox x ~0.65 headW")
    check(abs(ev["bottom_z_norm"] - (-0.583)) < 0.03, "pair: envelope bottom z_norm ~-0.58")
    check(ev["silhouette_ratio"] > 1.0 and ev["widest_band"] is not None,
          "pair: silhouette bulges past the scalp")
    check(sum(b["count"] for b in ev["horiz_radius_by_z"]) <= ev["point_count"],
          "pair: z bands do not double count")
    out = {"meta": {"script_version": SCRIPT_VERSION}, "head": {"source": "selftest", "width": 0.2, "center": [0, 0, 0]},
           "objects": [{"name": "Hair_multi", "verts": len(both.verts), "modifiers": [], "shape_keys": []}],
           "aggregates": agg, "uv": [], "bones": {}, "warnings": []}
    print_summary(out, "(selftest)")
    json.dumps(out)  # JSON 化できることの確認

    print("\nSELFTEST %s (%d failures, %d skipped%s)" % (
        "PASSED" if not fails else "FAILED", len(fails), len(skipped),
        "" if has_np else ": numpy 非搭載"))
    return 0 if not fails else 1


# ----------------------------------------------------------------------------
# エントリポイント
# ----------------------------------------------------------------------------
def _parse_argv(argv):
    cfg = {}
    it = iter(argv)
    for a in it:
        if a == "--targets":
            cfg["targets"] = [s for s in next(it).split(",") if s]
        elif a == "--head":
            cfg["head_object"] = next(it)
        elif a == "--head-group":
            v = next(it)
            cfg["head_vertex_group"] = v.split("/", 1)
        elif a == "--out":
            cfg["out"] = next(it)
        elif a == "--front":
            cfg["front_axis"] = next(it)
        elif a == "--evaluated":
            cfg["use_evaluated"] = True
        elif a == "--max-detail":
            cfg["max_strands_detail"] = int(next(it))
    return cfg


def main():
    argv = sys.argv
    if "--selftest" in argv:
        sys.exit(selftest())
    cfg = dict(DEFAULTS)
    g = globals().get("INSPECT_HAIR_CONFIG")
    if isinstance(g, dict):
        cfg.update(g)
    if "--" in argv:
        cfg.update(_parse_argv(argv[argv.index("--") + 1:]))
    try:
        import bpy  # noqa: F401
    except Exception:
        print("[inspect_hair] bpy が無い環境です。Blender 内で実行するか --selftest を使ってください。")
        sys.exit(2)
    run_blender(cfg)


if __name__ == "__main__" or globals().get("INSPECT_HAIR_CONFIG") is not None:
    main()
