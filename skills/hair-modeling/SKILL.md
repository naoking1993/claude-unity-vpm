---
name: hair-modeling
description: Blender上の髪型メッシュ（VRChatアバター用ヘアアセット）のリバースエンジニアリング・構造の数値化・再現レシピ化と、将来の「指示から髪型を生成する」ための知見蓄積の方法論。Use this skill whenever the user asks Claude to analyze, measure, reverse-engineer, compare, or (re)create hair in Blender — 「この髪型を解析して」「髪をリバースエンジニアリングして」「房の数を数えて」「房の幅/長さ/UV/ウェイト/法線を調べて」「髪型をレシピ化して」「samples/patterns.md を更新して」「同じ構造の髪を作って」「髪型を生成して」「前髪/サイド/後ろ髪の構造」— or mentions ヘアカード・房・毛先・ヘアメッシュ・法線転写・髪ボーン, or a hair asset is open in Blender (GUI or Blender MCP) even for a single measurement. Always run scripts/inspect_hair.py instead of eyeballing; never state strand counts, widths, or UV layout from a screenshot alone. Check references/verified-facts.md BEFORE assuming Blender API behavior, classification thresholds, FBX import behavior, or head-reference settings already verified there.
---

# 髪型のリバースエンジニアリングと生成レシピ化（Blender）

**ステータス: v0（2026-08-30作成。inspect_hair.py 0.2.1 は合成データの自己テスト 91 項目のみ通過、
Blender実機は未実測）。**
原則0（初回実測プロトコル）の完了で v1 へ昇格する。
実測値・閾値校正・既知不具合は `references/verified-facts.md` を参照。

## なぜこのスキルが必要か

- **学習の実体は references に書かれたファイルだけ。** セッション内で解析しても次回の
  Claude には残らない。よって全ての作業の目的は「次回の Claude が読んで再現できる文書」を
  残すことに置く。
- **完成メッシュには構築法が残っていない。** FBX 経由ならモディファイア・カーブは消えている。
  「カーブ＋ベベルで作られた」等は常に推定であり、往復テスト（第3段）でしか検証できない。
- **目視は根拠にならない。** スクリーンショットから房数・幅・UV を語るとセッションごとに
  数値がぶれ、捏造の温床になる。数値はスクリプト出力からのみ取る。
- **N=1 で分かるのは作者の癖。** 一般則は横断統合（第2段）でしか作れない。

## 原則1: 実測主義

- 房数・頂点数・幅・長さ・曲がり・UV方向・ウェイト・法線は `scripts/inspect_hair.py` の
  JSON からのみ取る。スクショはシルエット確認と region 分類の妥当性確認にだけ使う。
- 接尾辞 `_guess` / `_estimate` の値はルール付き推定。文書に書くときは
  「推定（region_v1）」のようにルール名を付ける。ルール本文は JSON の `meta.rules`。
- スクリプトが出せない値は **「未測定」と書く。埋めない。**
- 警告（`warnings`）を先に処理する。特に `hair_union_bbox_fallback` が出た解析の
  正規化値は他サンプルと比較できない（同一設定同士の比較のみ有効）。

## 原則2: 3層分離（実測／推定／一般則）

samples の各項目は次の 3 種のどれかを明示する:

| 層 | 書式 | 例 |
|---|---|---|
| 実測 | 値＋JSONキー | 房数 137（`aggregates.strand_count`） |
| 推定 | 値＋ルール名＋根拠 | 前髪 31 房（region_v1、root_norm f>0.35） |
| 一般則候補 | 「仮説(N=1)」表記 | 「根元1列はHead100%固定」仮説(N=1) |

「この形は○○で作られた」は必ず推定層に置き、支持するデータと矛盾するデータを併記する。

## 原則3: 形容詞ではなくパラメータ

「ふんわりした前髪」は再現できない。「前髪 9 房、根元幅/毛先幅比 3.2（中央値）、
長さ 0.4〜0.6（頭幅比）、turn 20〜40°」なら再現できる。samples とレシピは
数値と JSON キーで書き、形容詞は使わない。

## 原則4: 一般化を挟む（特定商品の再現手順を書かない）

- `samples/` は解析記録、`patterns.md` は一般則。レシピは patterns.md の語彙で書く。
- 購入アセット（BOOTH 等）は利用規約上、改変物や再現物の配布に制約があり得る。
  スキルの汎用性の面でも、特定商品の頂点配置をなぞる手順は書かない。
  数値範囲（系統別範囲表）と規約（断面・格子・UV・ウェイト）に一般化してから使う。

## 原則5: トークン効率

- JSON はファイルへ。コンテキストに載せるのは print サマリー（10行程度）と、必要な部分
  （`aggregates` / `aggregates.by_region` / 特定 id の `strands[]`）だけ。
  `strands[]` 全件をコンテキストに読み込まない（Python で id や region をフィルタして読む）。
- Blender MCP 経由なら execute 系 tool で `exec(open(...).read())` → 戻り値はサマリーのみ。
- 同じ状態を二度 read しない・失敗後に盲目的に再実行しない（unity-mcp-operations 原則2・6 と同根）。
  Blender MCP の tool 体系は実装ごとに異なるので、tool 名は実測してから使う（同原則0）。

## 原則0: 初回実測プロトコル（状態: 未実施）

初めて Blender（GUI または MCP）で本スキルを使うセッションで、実作業の前に実行する。
完了したら本節を「済（日付・Blender版・実行経路）」に書き換え、ステータスを v1 にする。

1. Blender バージョン、実行経路（GUI Text Editor / Blender MCP 実装名とバージョン /
   background）、OS を記録。
2. Blender 同梱 Python で `inspect_hair.py --selftest` を実行し、numpy の有無と
   API 分岐（`corner_normals` があるか、`calc_normals_split` に落ちるか）の実挙動を記録。
3. 房数が目視で数えられる簡単な髪（自作カード数枚でも可）で実行し、
   房数・`region_guess`・`cross_section_guess`・`columns_estimate` をユーザーの目視と突き合わせる。
   ずれた項目は RULES の閾値を校正し、校正値と根拠を verified-facts.md に記録。
4. FBX インポート品で、カスタム法線（`custom_normals_base`）・ウェイト・シェイプキー・
   モディファイアがそれぞれ残っているかを実測。
5. 使用アバターごとに頭部基準の推奨設定（`head_object` または `head_vertex_group`）と
   `front_axis` の実際の向きを記録。
6. 全て verified-facts.md へ（事実 / 出典=実測 / 検証日）。

## 手順

### 第1段: サンプル解析（1サンプル = 1ファイル）

- 出力先 `references/samples/<サンプル名>.md`。テンプレは `references/samples/TEMPLATE.md`。
- 開始時に確認する 3 点: 対象オブジェクト名 / 出所（FBX インポート or .blend 付属）/
  基準アバターと頭部基準設定。出所で「構築法」の推定可能範囲が変わる。
- 流れ: スクリプト実行 → サマリー確認 → 警告処理 → `aggregates` と `by_region` を読む →
  必要なら特定房の `strands[]` を読む → テンプレに沿って記述 → 一般則候補は 3 件以内、
  全て「仮説(N=1)」。既存 patterns.md と矛盾するものはその旨を明記。

### 第2段: 横断統合（3〜5サンプルたまったら）

- `references/patterns.md` を更新する。昇格規則:
  N≥3 で一致 → 確定則（根拠サンプル名を列挙）/ 作者で割れる → 作者依存（選択肢と使い分け）/
  N≤2 → 仮説のまま。
- 系統別数値範囲表 A（基本: 房数・長さ・幅・断面・格子）と
  B（形状・シルエット: taper・中膨れ率・turn・twist・ピッチ・radial 分位点・エンベロープ）を更新。
  領域別の表も同じ列を持つ。
- **受け皿（節・列）が無くて書けなかった項目は、patterns.md の「第2段の昇格候補台帳」に積む。**
  台帳は 指標を実装 → 節・列を新設 → サンプルで埋める の順で片付ける。
  気づいた時点で台帳に書くこと。**書かなければ次のセッションには残らない**（原則0の理由と同じ）。
- 昇格・降格は変更履歴に残す。patterns.md を編集するのはこの段だけ。

### 第3段: 往復検証

- patterns.md と `samples/<名前>.md` のレシピ **だけ** を根拠に bpy で同等構造を生成する。
- 元と再生成の両方を inspect_hair.py にかけ、`scripts/compare_hair_json.py` で比較。
  ★ 項目＝レシピの欠落候補として `samples/<名前>.md` に追記。
- 目視比較はシルエットのみ（並置スクショ）。数値で言えることを目視で言わない。

## スクリプト

- `scripts/inspect_hair.py` — 解析本体。使い方と設定キーはファイル冒頭の docstring。
  主要設定: `targets` / `head_object` / `head_vertex_group` / `head_bbox_override` /
  `front_axis`（既定 -Y）/ `use_evaluated`（モディファイア適用後を見る）/ `out`。
  Blender 不要の `--selftest` あり。
- `scripts/compare_hair_json.py` — 往復検証の差分表（Blender 不要）。
- **0.2.x で追加した集計**（第2段の昇格候補 7 件に対応。詳細は patterns.md の台帳）:
  幅プロファイル（板の中膨れ）/ radial 分位点とレイヤー / エンベロープ（頭部固定 z 帯の水平半径）/
  房ピッチと重なり / 左右対称性 / 根元固定帯 / 領域別の taper・turn・twist。
  比較表もこれらを見るので、レシピが形を取りこぼしていれば第3段の ★ に出る。
- **判定ルールの落とし穴（0.2.1 で修正済み。同じ間違いを繰り返さないこと）**:
  ①中膨れは「中央 vs 両端の**平均**」ではなく「中央 vs 両端の**どちらか**」で判定する。
  平均と比べると単調に細るだけの板が mid_bulge / waisted に化ける。
  ②層は 3D 半径ではなく**水平半径**で測る。3D 半径には z が入るので、
  頭皮に密着した長い房が外側レイヤーに化ける。
  どちらも「一見それらしい指標が、実は別のものを測っていた」型の誤り。
  新しい指標を足すときは、**その指標が高くなる別の理由**を先に潰すこと。
- **既知の限界（0.2.0）**: 連結成分＝房の前提（頂点結合済みの髪は 1 島になる。UV 島分割は未実装）/
  region・断面・格子推定は正則四角格子と `front_axis` の仮定に依存 / ねじれ推定に面内曲がりが混入 /
  頭部基準に体全体のメッシュを渡すと正規化が狂う（警告は出る）/
  房ピッチとミラーは O(房数²)（`max_pairwise_strands`=2000 超で省略。実アセットでの所要時間は未実測）/
  頭部基準が代用のとき envelope の頭部依存量は測れないので None になる（縦横比だけ残る）/
  頭部基準は常に評価前メッシュから作るので、`use_evaluated=True` だと基準がずれ得る（警告は出る）/
  エンベロープは targets の頂点で計算するので髪以外を混ぜると狂う
  （面に属さない孤立頂点は 0.2.1 で除外済み）/
  Mirror モディファイアが未適用のまま `use_evaluated=False` だと片側しか見ない（警告は出る）/
  頭部基準が代用のときエンベロープの z 帯は頭部を指さない（`envelope.head_source` で判別）/
  0.2.0 で追加した閾値はすべて未校正（判定ラベルより生の数値を信用する）/
  numpy が無い環境では幅・厚み・断面（PCA 依存）が None になる
  （`--selftest` はその分を SKIP 表示にして完走する。レイヤー・エンベロープ・ピッチ・
  ミラー・根元固定帯・UV 方向は numpy 無しでも出る）。

## 原則6: 対話プロトコル

- 解析指示 → 設定（対象・頭部基準・出所）を 1〜3 行で確認 → 実行 → サマリー提示 →
  警告処理 → 記述。設定が既に verified-facts.md にあるアバターなら確認を省く。
- 「良い感じの髪を作って」への現段階の対応: patterns.md に確定則が無いうちは生成に着手しない。
  samples の蓄積状況（N と系統）を提示し、第2段の実施を提案する。
  着手できる条件は、断面・格子・UV 規約・根元ウェイトの 4 項目に確定則が揃っていること。
  4 項目とも 0.2.0 で「測る手段」は揃った（根元ウェイトは `root_lock_t` ほか）。
  残っているのはサンプル数だけなので、この段で足りないものは常に「N」と答える。
- 生成器（カーブ＋ベベル / Geometry Nodes Curve to Mesh / Data Transfer 法線転写）の実装は、
  確定則が揃った時点で本 SKILL.md に第4段として追記する。

## 原則7: スキル成長ループ

- セッション終盤、または「スキルに反映して」の指示で: 判明した API 挙動・閾値校正・
  アバター別設定を verified-facts.md へ（事実/出典/検証日）。
- スクリプトの不具合は再現手順と一緒に verified-facts.md の「既知エラー」に記録し、
  修正したら `SCRIPT_VERSION` を上げて本 SKILL.md のステータス行を更新。
- 2 回以上繰り返した手順は一般化して該当原則へ追記。

## 一般化

「完成品から構築規約を逆算し、生成器の校正データにする」作業全般に流用できる
（衣装メッシュの構造分析、ワールドのモジュール構造、他人の Unity プレハブ構成の解析等）:
①固定スクリプトで実測 → ②実測/推定/一般則を分けて記録 → ③N≥3 で昇格 → ④往復テストで検証。
