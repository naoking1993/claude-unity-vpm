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
    "centerline_bins": 10,           # 中心線の初期折れ線（geodesic t の帯）の数
    "centerline_max_nodes": 16,      # 中心線ノード数の上限。turn の値はノード数に依存しにくい
    "centerline_points_per_node": 4, # 1ノードあたり最低この頂点数。足りなければノードを増やさない
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
    "centerline_v2": (
        "中心線 = ①geodesic t を centerline_bins 等分した帯重心を初期折れ線にし、"
        "②各ノードの接線に直交するスラブ（半幅 hs = 係数×ノード間隔）内で位置を局所線形回帰して引き直し、"
        "③両端は内側の接線方向 tau の下位/上位2%分位の面へ張り直し、"
        "④8→12→16ノードの粗→細（1ノードあたり centerline_points_per_node 頂点を確保できる段まで。"
        "細分化による turn の増分が前段の増分より大きくなった段は、形ではなくメッシュのノイズを"
        "拾い始めたとみなして棄却＝粗い段の値を採る）。"
        "②が接線方向の射影なので、根元断面が非対称に取れて断面内で t がずれても幅方向のジグザグが混入しない"
    ),
    "turn_v2": (
        "turn_total_deg = 中心線の弦の折れ角の総和 + 先頭/末尾の折れ角の各1/2（両端の半区間ぶんの外挿補正）。"
        "この補正により、曲線上に等間隔で載ったノードなら円弧に対してノード数に依らず厳密。単位 deg、∫|dθ| の推定量。"
        "turn_net_deg = 先頭弦と末尾弦のなす角（C字は turn≒net、S字は turn>>net）。"
        "turn_per_length_deg = turn_total_deg / length_geo_norm（頭幅1あたりの曲がり＝曲率密度）。"
        "中心線が引けない房では3つとも None（埋めない）。"
        "房の断面数（行数）が少ないほど過小になる（合成カードで行数6のとき真値の約-8%、行数12以上で-1%）"
    ),
    "straightness_v2": (
        "中心線の両端間の直線距離 / 中心線の折れ線長。定義上 0<straightness<=1。"
        "length_straight_norm（根元帯重心と毛先帯重心の距離）とは端点の取り方が異なる"
    ),
    "twist_v1": (
        "5帯それぞれの断面PCAで得た幅軸を直線とみなし、隣接帯間の角度(0〜90°)を合計。"
        "法線軸の回転（＝曲げ）は含まないが、面内曲がりは混入する。"
        "turn とは独立に未改修（0.2.0 時点で turn_v2 のような検証を通していない）"
    ),
    "uv_direction_v1": (
        "房内ループの t(根元0→毛先1) と u,v の相関係数を比較し、絶対値が大きい軸を「房の長手方向のUV軸」とする。"
        "符号が負なら毛先に向かって減少"
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
        "p25": _r(q(0.25), nd),
        "median": _r(q(0.5), nd),
        "p75": _r(q(0.75), nd),
        "max": _r(vals[-1], nd),
        "mean": _r(sum(vals) / n, nd),
    }


def _mode(vals):
    vals = [v for v in vals if v is not None]
    if not vals:
        return None, 0
    c = Counter(vals).most_common(1)[0]
    return c[0], c[1]


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


def _bin_centroids(P, param, nb):
    """param(0..1) を nb 等分した帯の重心。空帯は None（詰めない・複製しない）。"""
    bins = [[] for _ in range(nb)]
    for i, v in enumerate(param):
        bins[min(nb - 1, max(0, int(v * nb)))].append(i)
    return [(_centroid([P[i] for i in b]) if b else None) for b in bins]


def _polyline_smooth(nodes):
    """3点移動平均（端は固定）。粗い初期折れ線のジグザグを均すためだけに使う。"""
    if len(nodes) < 3:
        return list(nodes)
    out = [nodes[0]]
    for k in range(1, len(nodes) - 1):
        out.append(tuple((nodes[k - 1][c] + 2.0 * nodes[k][c] + nodes[k + 1][c]) / 4.0 for c in range(3)))
    out.append(nodes[-1])
    return out


def _resample_polyline(nodes, m):
    """折れ線を弧長等間隔の m ノードに引き直す。退化していれば None。"""
    if len(nodes) < 2 or m < 2:
        return None
    seg, acc = [], [0.0]
    for k in range(len(nodes) - 1):
        l = _dist(nodes[k], nodes[k + 1])
        seg.append(l)
        acc.append(acc[-1] + l)
    total = acc[-1]
    if total <= 1e-12:
        return None
    out = []
    for j in range(m):
        target = total * j / (m - 1.0)
        k = 0
        while k < len(seg) - 1 and acc[k + 1] < target:
            k += 1
        w = 0.0 if seg[k] <= 1e-12 else (target - acc[k]) / seg[k]
        w = 0.0 if w < 0.0 else (1.0 if w > 1.0 else w)
        out.append(tuple(nodes[k][c] + (nodes[k + 1][c] - nodes[k][c]) * w for c in range(3)))
    return out


def _node_tangents(nodes):
    """各ノードの接線（端は片側差分、内部は中心差分）"""
    m = len(nodes)
    return [_norm(_sub(nodes[min(m - 1, j + 1)], nodes[max(0, j - 1)])) for j in range(m)]


def _slab_fit(P, c, T, hs, tau0=0.0):
    """c を通り T に直交する面から測った tau=dot(P-c,T) について、|tau-tau0|<hs の頂点で
    位置を局所線形回帰し、tau=tau0 における値を返す。
    tau は接線方向への射影なので、断面内（幅方向）のばらつきは「どの頂点を使うか」に混入しない。
    単純平均と違い次数1なので、片側しか頂点が無い端でも内側に寄らない。"""
    sw = swd = swdd = 0.0
    sp = [0.0, 0.0, 0.0]
    sdp = [0.0, 0.0, 0.0]
    cnt = 0
    for p in P:
        d = (p[0] - c[0]) * T[0] + (p[1] - c[1]) * T[1] + (p[2] - c[2]) * T[2] - tau0
        if d <= -hs or d >= hs:
            continue
        w = 1.0 - (d if d >= 0 else -d) / hs
        w *= w
        sw += w
        swd += w * d
        swdd += w * d * d
        for k in range(3):
            sp[k] += w * p[k]
            sdp[k] += w * d * p[k]
        cnt += 1
    if sw <= 0.0 or cnt < 2:
        return None
    mean = tuple(sp[k] / sw for k in range(3))
    # 回帰の条件付け。窓内の tau がほぼ一点に固まっていると傾きが発散し、評価点 d=0 まで
    # 外挿した切片がメッシュの外へ飛ぶ（断面が偏った房で実際に起きる）。
    # ①tau の広がりが窓幅に対して小さすぎる ②評価点が窓のデータから離れすぎている
    # のどちらかなら、次数0（重み付き平均）へ落とす。
    md = swd / sw
    var = swdd / sw - md * md
    sd = math.sqrt(var) if var > 0.0 else 0.0
    if sd < 0.05 * hs or abs(md) > 3.0 * sd:
        return mean
    det = sw * swdd - swd * swd
    if det <= 1e-18 * max(1.0, sw * swdd):
        return mean
    fit = tuple((swdd * sp[k] - swd * sdp[k]) / det for k in range(3))
    # 切片が窓の中心から窓幅以上離れたら外挿しすぎ。埋めずに次数0へ落とす
    return fit if _dist(fit, mean) <= hs else mean


def _end_anchor(P, c, T, hs, low, q=0.02):
    """房の端断面へノードを張り直す。T 方向の tau の下位/上位 q 分位の面で局所線形回帰する。
    端ノードだけは中心線の内側の清潔な接線を使って呼ぶこと（端ノード自身の接線は汚れている）。"""
    taus = sorted((p[0] - c[0]) * T[0] + (p[1] - c[1]) * T[1] + (p[2] - c[2]) * T[2] for p in P)
    k = max(0, min(len(taus) - 1, int(q * (len(taus) - 1))))
    tau0 = taus[k] if low else taus[len(taus) - 1 - k]
    return _slab_fit(P, c, T, hs, tau0)


def _refine_centerline(P, nodes, m, hsk, iters):
    """スラブ射影による主曲線の精密化を m ノードで iters 回。最後に両端を端断面へ張り直す。"""
    nodes = _resample_polyline(nodes, m)
    if nodes is None:
        return None
    for _ in range(iters):
        plen = sum(_dist(nodes[k], nodes[k + 1]) for k in range(m - 1))
        if plen <= 1e-12:
            return None
        hs = hsk * plen / (m - 1.0)
        T = _node_tangents(nodes)
        moved = [(_slab_fit(P, nodes[j], T[j], hs) or nodes[j]) for j in range(m)]
        nodes = _resample_polyline(moved, m) or nodes
    if m >= 5:
        plen = sum(_dist(nodes[k], nodes[k + 1]) for k in range(m - 1))
        hs = hsk * plen / (m - 1.0)
        t0 = _norm(_sub(nodes[2], nodes[1]))
        t1 = _norm(_sub(nodes[m - 2], nodes[m - 3]))
        a = _end_anchor(P, nodes[1], t0, hs, True)
        b = _end_anchor(P, nodes[m - 2], t1, hs, False)
        nodes = _resample_polyline(([a] if a else []) + nodes[1:m - 1] + ([b] if b else []), m) or nodes
    return nodes


# 中心線の粗→細スケジュール: (ノード数, スラブ半幅/ノード間隔, 反復回数)
CENTERLINE_SCHEDULE = ((8, 1.8, 3), (12, 1.3, 2), (16, 1.0, 2))
# 最初の細分化で turn がこの割合を超えて増えたら発散とみなす（2段目以降は前段の増分と比べる）
CENTERLINE_FIRST_INC_CAP = 0.5


def centerline(P, t, cfg):
    """房の中心線ノード列（centerline_v2）。
      ①geodesic t の帯重心を初期折れ線にする（根元断面が非対称に取れた房では幅方向にジグザグする）
      ②各ノードの接線に直交するスラブ内での局所線形回帰でノードを引き直す（射影方向が接線なので
        断面内のばらつきが混入しない＝①のジグザグが消える）
      ③両端は内側の接線を使って端断面へ張り直す（片側窓の外挿より安定し、t=0〜1 を実際に張る）
      ④粗→細（8→12→16ノード）。頂点数が足りない段は飛ばし、細くして turn が跳ねた段は棄却する
    戻り値: ノード列 / 引けなければ None"""
    c1 = [c for c in _bin_centroids(P, t, cfg["centerline_bins"]) if c is not None]
    if len(c1) < 3:
        return None
    nodes = _polyline_smooth(_polyline_smooth(c1))
    cap = max(5, len(P) // max(1, cfg["centerline_points_per_node"]))
    best = None
    best_turn = None
    prev_inc = None
    done = 0
    for (m, hsk, iters) in CENTERLINE_SCHEDULE:
        me = min(m, cap, cfg["centerline_max_nodes"])
        if me < 5 or me <= done:
            continue
        cand = _refine_centerline(P, nodes, me, hsk, iters)
        if cand is None:
            break
        turn, _net, _pl = polyline_turn(cand)
        if turn is None:
            break
        if best_turn is not None:
            # 細かくすると turn は増えるが、増分は段を追うごとに縮む（収束する）のが正常。
            # 増分が前段より大きいのは、形ではなくメッシュの粗さ・ノイズを拾い始めた合図なので棄却する。
            inc = (turn - best_turn) / max(best_turn, 10.0)
            limit = CENTERLINE_FIRST_INC_CAP if prev_inc is None else (prev_inc * 1.3 + 0.05)
            if inc > limit:
                break
            prev_inc = inc if inc > 0.0 else 0.0
        best, best_turn, nodes, done = cand, turn, cand, me
    return best


def polyline_turn(nodes):
    """折れ線の曲がり量。
    total: 接線方向の総回転量[deg]（∫|dθ| の推定）。弦の折れ角の総和に、両端の半区間ぶんを
           外挿補正（先頭/末尾の折れ角の各1/2）として足す。ノードが曲線上に等間隔で載っていれば、
           円弧に対してはノード数に依らず厳密。
    net:   先頭弦と末尾弦のなす角[deg]（C字は total≒net、S字は total>>net）
    戻り値: (total, net, 折れ線長)。折れ角が取れなければ (None, None, 長さ)"""
    segs = []
    for k in range(len(nodes) - 1):
        d = _sub(nodes[k + 1], nodes[k])
        l = _len(d)
        if l > 1e-12:
            segs.append((d, l))
    plen = sum(l for _, l in segs)
    if len(segs) < 2:
        return None, None, plen
    angs = []
    for k in range(1, len(segs)):
        a = _angle_deg(segs[k - 1][0], segs[k][0])
        angs.append(0.0 if a is None else a)
    total = sum(angs) + 0.5 * angs[0] + 0.5 * angs[-1]
    return total, _angle_deg(segs[0][0], segs[-1][0]), plen


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
    hints_l = [h.lower() for h in cfg["head_group_hints"]]

    def head_like(gidx):
        if gidx is None or gidx >= len(md.group_names):
            return False
        nm = md.group_names[gidx].lower()
        return nm in hints_l or "head" in nm

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


def analyze_island(md, vidx, fidx, face_loops, edge_set, boundary_edge_set, head, cfg, fa_spec, sid, obj_name):
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
    adj = [[] for _ in range(n)]
    loc_edges = []
    for a, b in edge_set:
        if a in local and b in local:
            la, lb = local[a], local[b]
            w = _dist(P[la], P[lb])
            adj[la].append((lb, w))
            adj[lb].append((la, w))
            loc_edges.append((la, lb))
    loc_boundary = [(local[a], local[b]) for a, b in boundary_edge_set if a in local and b in local]
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

    # 中心線（centerline_v2: t の帯重心 → 射影で再パラメータ化 → 局所線形回帰）
    cent = centerline(P, t, cfg)
    root_c = _centroid([P[i] for i in range(n) if t[i] <= cfg["root_band_frac"]]) or (cent[0] if cent else P[0])
    tip_c = _centroid([P[i] for i in range(n) if t[i] >= 1.0 - cfg["root_band_frac"]]) or (cent[-1] if cent else P[-1])
    mid_c = cent[len(cent) // 2] if cent else _centroid(P)

    # 長さ・曲がり（turn_v2。中心線が引けない房は turn 系を None にする＝埋めない）
    W = head["width"]
    length_geo = geo_max
    res["length_geo_norm"] = _r(length_geo / W)
    res["length_straight_norm"] = _r(_dist(root_c, tip_c) / W)
    res["centerline_node_count"] = len(cent) if cent else 0
    if cent:
        turn, turn_net, poly_len = polyline_turn(cent)
        cl_straight = _dist(cent[0], cent[-1])
        res["straightness"] = _r(cl_straight / poly_len) if poly_len > 1e-9 else None
        res["turn_total_deg"] = _r(turn, 1)
        res["turn_net_deg"] = _r(turn_net, 1)
        res["turn_per_length_deg"] = _r(turn / (length_geo / W), 1) if (turn is not None and length_geo > 1e-12) else None
    else:
        res["straightness"] = None
        res["turn_total_deg"] = None
        res["turn_net_deg"] = None
        res["turn_per_length_deg"] = None

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
        tang = (0.0, 0.0, 0.0)
        if cent:
            m_ = len(cent)
            bi = min(m_ - 1, max(0, int(round(tc * (m_ - 1)))))
            tang = _sub(cent[min(bi + 1, m_ - 1)], cent[max(bi - 1, 0)])
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
            if np is not None:
                ta = np.array(ts)
                ua = np.array(us)
                va = np.array(vs)
                cu = cv = None
                if ta.std() > 1e-9 and ua.std() > 1e-9:
                    cu = float(np.corrcoef(ta, ua)[0, 1])
                if ta.std() > 1e-9 and va.std() > 1e-9:
                    cv = float(np.corrcoef(ta, va)[0, 1])
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

    strands = []
    for ii, vs in enumerate(islands):
        s = analyze_island(md, vs, faces_of.get(ii, []), face_loops, edge_set, boundary_edge_set,
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


def aggregate(strands, head, cfg):
    ok = [s for s in strands if not s.get("degenerate")]
    agg = {
        "strand_count": len(ok),
        "degenerate_islands": len(strands) - len(ok),
        "verts_per_strand": _stats([s["verts"] for s in ok], 1),
        "length_geo_norm": _stats([s.get("length_geo_norm") for s in ok]),
        "length_straight_norm": _stats([s.get("length_straight_norm") for s in ok]),
        "straightness": _stats([s.get("straightness") for s in ok]),
        "turn_total_deg": _stats([s.get("turn_total_deg") for s in ok], 1),
        "turn_net_deg": _stats([s.get("turn_net_deg") for s in ok], 1),
        "turn_per_length_deg": _stats([s.get("turn_per_length_deg") for s in ok], 1),
        "turn_measured_fraction": _r(sum(1 for s in ok if s.get("turn_total_deg") is not None) / max(1, len(ok))),
        "centerline_node_count": _stats([s.get("centerline_node_count") for s in ok], 1),
        # 房ごとにノード数が違う＝turn を測ったスケールが違う。中央値同士の比較は慎重に
        "turn_scale_mixed": len({s.get("centerline_node_count") for s in ok if s.get("turn_total_deg") is not None}) > 1,
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

    # 領域別
    by_region = {}
    for reg in sorted(set(s.get("region_guess") for s in ok)):
        ss = [s for s in ok if s.get("region_guess") == reg]
        by_region[str(reg)] = {
            "count": len(ss),
            "length_geo_norm": _stats([s.get("length_geo_norm") for s in ss]),
            "root_width_norm": _stats([s["width_norm"]["t0"] for s in ss if s.get("width_norm")]),
        }
    agg["by_region"] = by_region

    # ウェイト
    gs = [s["groups"] for s in ok if s.get("groups")]
    if gs:
        agg["weights"] = {
            "groups_per_strand": _stats([g["count_sig"] for g in gs], 1),
            "groups_per_vertex_mean": _stats([g["per_vertex_mean"] for g in gs], 2),
            "root_dominant_mode": _mode([g["root_dominant"] for g in gs])[0],
            "root_dominant_fraction": _r(_mode([g["root_dominant"] for g in gs])[1] / len(gs)),
            "tip_dominant_mode": _mode([g["tip_dominant"] for g in gs])[0],
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
        head = head_from_points(pts, "hair_union_bbox_fallback")
        warnings.append("頭部基準が見つからず髪全体bboxで代用。正規化値の絶対比較は不可（同一設定同士の比較のみ有効）")

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

    agg = aggregate(strands, head, cfg)
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
    T = a.get("turn_total_deg") or {}
    TN = a.get("turn_net_deg") or {}
    TL = a.get("turn_per_length_deg") or {}
    print("turn (turn_v2): total median %s° (p25 %s / p75 %s) | net median %s° | per_length median %s°/頭幅 | 測定できた房 %s | nodes median %s" % (
        T.get("median"), T.get("p25"), T.get("p75"), TN.get("median"), TL.get("median"),
        a.get("turn_measured_fraction"), (a.get("centerline_node_count") or {}).get("median"))
        + ("  ※房ごとに測定スケールが違う" if a.get("turn_scale_mixed") else ""))
    print("cross_section_guess: %s | columns mode=%s rows median=%s grid_fit=%s | tip_split=%s" % (
        a["cross_section_counts"], a["columns_estimate_mode"], a["rows_estimate"].get("median"), a["grid_fit_fraction"], a["tip_split_fraction"]))
    print("region_guess: %s" % a["region_counts"])
    for u in out.get("uv", []):
        print("uv[%s]: along=%s tip_dir=%s coverage=%s shared_pairs=%s" % (
            u["object"], u["along_mode"], u["tip_dir_mode"], u["coverage_est"], u["shared_bbox_pairs"]))
    w = a.get("weights")
    if w:
        print("weights: groups/strand median %s | root dominant %s (%s) | tip dominant %s" % (
            w["groups_per_strand"].get("median"), w["root_dominant_mode"], w["root_dominant_fraction"], w["tip_dominant_mode"]))
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


def _curve_card(name, rows, cols, center_fn, width=0.03, taper=0.7):
    """中心線 center_fn(s)->(x,y,z) に沿った平板カード（selftest 用）。
    幅方向は接線と +Z の外積（縮退したら +Y との外積）。"""
    def pos(r, c):
        sp = r / (rows - 1.0)
        p = center_fn(sp)
        ds = 1e-4
        a = center_fn(min(1.0, sp + ds))
        b = center_fn(max(0.0, sp - ds))
        tang = _norm(_sub(a, b))
        w = _norm(_cross(tang, (0.0, 0.0, 1.0)))
        if _len(w) < 0.5:
            w = _norm(_cross(tang, (0.0, 1.0, 0.0)))
        k = (c / (cols - 1.0) - 0.5) * width * (1.0 - taper * sp)
        return (p[0] + w[0] * k, p[1] + w[1] * k, p[2] + w[2] * k)
    return _grid_mesh(name, rows, cols, pos, lambda r, c: (c / (cols - 1.0), 1.0 - r / (rows - 1.0)))


def selftest():
    head = {"center": (0.0, 0.0, 0.0), "half": (0.1, 0.1, 0.12), "width": 0.2, "source": "selftest"}
    cfg = dict(DEFAULTS)
    fails = []

    def check(cond, msg):
        print(("  OK  " if cond else "  NG  ") + msg)
        if not cond:
            fails.append(msg)

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
    card_strand = s  # 以降 s はループ変数で潰れるので退避
    print("[card]", json.dumps({k: s[k] for k in ("verts", "cross_section_guess", "columns_estimate", "rows_estimate",
                                                   "grid_fit", "width_norm", "length_geo_norm", "region_guess",
                                                   "tip_split_count", "root_ring_closed", "turn_total_deg")}, ensure_ascii=False))
    check(len(strands) == 1, "card: 1 island")
    check(s["cross_section_guess"] == "flat_card", "card: flat_card")
    check(s["columns_estimate"] == 4 and s["rows_estimate"] == 12 and s["grid_fit"], "card: 4 cols x 12 rows")
    check(abs(s["width_norm"]["t0"] - 0.15) < 0.03, "card: root width ~0.15 (0.03/0.2)")
    check(s["width_norm"]["t100"] < s["width_norm"]["t0"] * 0.5, "card: tip narrower than half of root")
    check(s["region_guess"] == "bangs", "card: region bangs")
    check(s["uv"]["along"] == "V" and s["uv"]["tip_dir"] == "-V", "card: uv along V, decreasing to tip")
    check(s["groups"]["root_dominant"] == "Head" and s["groups"]["tip_dominant"] == "Hair_1", "card: root=Head tip=Hair_1")
    check(s["tip_split_count"] == 1, "card: no tip split")
    check(uvi["along_mode"] == "V", "card: uv summary")

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
    both = MeshData("Hair_multi")
    off = 0
    for src in (card, tube):
        both.verts.extend(src.verts)
        both.vnormals.extend(src.vnormals)
        both.edges.extend([(a + off, b + off) for a, b in src.edges])
        fbase = len(both.faces)
        both.faces.extend([[v + off for v in f] for f in src.faces])
        both.loops_vert.extend([v + off for v in src.loops_vert])
        both.loops_face.extend([f + fbase for f in src.loops_face])
        off += len(src.verts)
    both.verts.append((0.3, 0.3, 0.3))  # 孤立頂点（退化島）
    both.vnormals.append((0.0, 0.0, 1.0))
    strands4, _ = analyze_mesh(both, head, cfg)
    agg = aggregate(strands4, head, cfg)
    print("[multi] strands=%d degenerate=%d regions=%s cross=%s" % (
        agg["strand_count"], agg["degenerate_islands"], agg["region_counts"], agg["cross_section_counts"]))
    check(agg["strand_count"] == 2 and agg["degenerate_islands"] == 1, "multi: 2 strands + 1 degenerate")
    check(agg["length_class_guess"] in ("very_short", "short"), "multi: length class")
    # 5) turn 計測（turn_v2）を解析形状で検証する。
    #    中心線が既知の曲線なので真値（接線の総回転量）が閉形式で分かる。
    #    ここが通らない限り turn_total_deg を samples に書かない。
    def turn_of(center_fn, rows, cols=4):
        m = _curve_card("Hair_turn", rows, cols, center_fn)
        return analyze_mesh(m, head, cfg)[0][0]

    def straight_c(sp):
        return (0.0, -0.09, 0.08 - 0.18 * sp)

    def arc90_c(sp):
        a = math.radians(90.0) * sp
        return (0.0, -0.085 + 0.10 * (1 - math.cos(a)), 0.085 - 0.10 * math.sin(a))

    def scurve_c(sp):
        return (0.0, -0.09 + 0.03 * math.sin(2 * math.pi * sp), 0.08 - 0.18 * sp)

    st8 = turn_of(straight_c, 8)
    st20 = turn_of(straight_c, 20)
    a12 = turn_of(arc90_c, 12)
    a32 = turn_of(arc90_c, 32)
    sc = turn_of(scurve_c, 20)
    print("[turn] " + json.dumps({
        "straight_rows8": st8["turn_total_deg"], "straight_rows20": st20["turn_total_deg"],
        "arc90_rows12": a12["turn_total_deg"], "arc90_rows32": a32["turn_total_deg"],
        "scurve_total": sc["turn_total_deg"], "scurve_net": sc["turn_net_deg"],
        "card_parabola": card_strand["turn_total_deg"], "card_net": card_strand["turn_net_deg"],
    }, ensure_ascii=False))
    check(st8["turn_total_deg"] < 1.0 and st20["turn_total_deg"] < 1.0, "turn: 直線カードは 0°（真値 0）")
    check(st20["straightness"] > 0.999, "turn: 直線カードの straightness ≈ 1")
    check(abs(a12["turn_total_deg"] - 90.0) < 6.0, "turn: 90°円弧カード(12行) が 90±6°")
    check(abs(a32["turn_total_deg"] - 90.0) < 6.0, "turn: 90°円弧カード(32行) が 90±6°")
    check(abs(a12["turn_total_deg"] - a32["turn_total_deg"]) < 5.0, "turn: 行数を12→32にしても値が動かない（解像度非依存）")
    check(abs(card_strand["turn_total_deg"] - 20.48) < 2.0, "turn: selftest カード(放物線中心線) が真値 20.48±2°")
    check(sc["turn_total_deg"] > 150.0 and sc["turn_net_deg"] < 15.0, "turn: S字は total 大・net 小")
    check(all(0.0 < x["straightness"] <= 1.0 for x in (st20, a12, a32, sc)), "turn: straightness は 0<x<=1")
    # 断面が t 方向に偏った房（中間が空っぽ）。スラブ内の tau がほぼ一点に固まるので、
    # 局所線形回帰の切片が外挿で飛ぶ。次数0への退避が効いていないと、真っ直ぐな房に
    # turn=187° / straightness=0.0 のような捏造値が出る（0.2.0 開発中に実際に出た）。
    clust_s = [0.0] + [0.80 + 0.02 * k for k in range(11)]
    for ccols in (2, 4):
        cm = _grid_mesh("Hair_clust", len(clust_s), ccols,
                        lambda r, c: ((c / (ccols - 1.0) - 0.5) * 0.03 * (1 - 0.5 * clust_s[r]),
                                      -0.09, 0.08 - 0.155 * clust_s[r]),
                        lambda r, c: (0.0, 0.0))
        cs = analyze_mesh(cm, head, cfg)[0][0]
        check(cs["turn_total_deg"] == 0.0 and cs["straightness"] == 1.0,
              "turn: 断面が偏った直線カード(%d列)でも turn=0 / straightness=1（外挿暴走なし）" % ccols)

    # 断面が2つしかない房は中心線が引けない → 数値を埋めずに None にする
    thin = turn_of(arc90_c, 2)
    print("[turn] 2行カード: turn=%s straightness=%s nodes=%s" % (
        thin["turn_total_deg"], thin["straightness"], thin.get("centerline_node_count")))
    check(thin.get("turn_total_deg") is None and thin.get("turn_net_deg") is None,
          "turn: 中心線が引けない房は None（埋めない）")

    out = {"meta": {"script_version": SCRIPT_VERSION}, "head": {"source": "selftest", "width": 0.2, "center": [0, 0, 0]},
           "objects": [{"name": "Hair_multi", "verts": len(both.verts), "modifiers": [], "shape_keys": []}],
           "aggregates": agg, "uv": [], "bones": {}, "warnings": []}
    print_summary(out, "(selftest)")
    json.dumps(out)  # JSON 化できることの確認

    print("\nSELFTEST %s (%d failures)" % ("PASSED" if not fails else "FAILED", len(fails)))
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
