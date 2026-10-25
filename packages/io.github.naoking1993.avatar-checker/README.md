# Avatar Checker（アバター改変チェッカー）

VRChat アバターを読み取って、パフォーマンスランクの目安と、アップロード前に直したい問題点を一覧にする Unity エディタ拡張です。**アバターやアセットは一切書き換えません。**

## 使い方

- **Tools > Avatar Checker > Open Window**：ウィンドウで確認します。PC / Quest を切り替えられます。問題点の「選択」で該当オブジェクトを選べます。
- **Tools > Avatar Checker > Check Selected (JSON)**：Hierarchy で選んだアバター（子オブジェクトを選んでも、親の VRC Avatar Descriptor までさかのぼります）をチェックし、結果を Console と `Library/AvatarChecker/last-report.json` に出します。
- **Tools > Avatar Checker > Check All Avatars In Scene (JSON)**：シーン内の全アバターをまとめてチェックし、`Library/AvatarChecker/scene-report.json` に出します。

### Claude（MCP for Unity）から使う

メニュー名は英語（ASCII）にしてあるので、MCP for Unity の `execute_menu_item` でそのまま呼べます。例：

1. `execute_menu_item` で `Tools/Avatar Checker/Check All Avatars In Scene (JSON)` を実行
2. `read_console` で `[AvatarChecker]` の行を読む（または `Library/AvatarChecker/scene-report.json` を読む）

JSON の `issues[].code` は変わらない英語の識別子（`missing-script`、`parameter-budget` など）で、`message` は日本語の説明です。C# から呼ぶ場合は `Naoking.AvatarChecker.AvatarCheckerMenu.CheckToJson(gameObject)` を使います。

## 調べる内容

| 種類 | 内容 |
| --- | --- |
| ランク（目安） | ポリゴン数、テクスチャメモリ、Skinned / 通常メッシュ数、マテリアル枠、ボーン数、PhysBone（コンポーネント・Transform・Collider・衝突チェック数）、Contact、Constraint（数と深さ）、Animator、Light、Particle（システム数・最大パーティクル数・メッシュポリゴン・Trails・Collision）、Trail / Line、Cloth（数と頂点数）、Collider、Rigidbody、AudioSource、Read/Write が無効なメッシュ |
| エラー | Missing スクリプト、シェーダーが見つからない・コンパイルエラー（ピンク）、メッシュの Read/Write 無効、Expression パラメータ 256 bit 超え、Expressions の Menu と Parameters の片方だけ設定、子オブジェクトに残った VRC Avatar Descriptor |
| 警告 | 空のマテリアル枠、メッシュ未設定、ボーン参照切れ、PhysBone の Root がアバター外・Collider 参照切れ、View Position、ルートの Animator / Avatar 未設定 |
| 情報 | Root Bone 未設定、サブメッシュより多いマテリアル枠、揺れない PhysBone、ルート以外の Animator、Humanoid でない、パラメータ残量が少ない |

- 数え方は VRChat SDK 3.10.5 の判定処理に合わせています。たとえばマテリアル枠はサブメッシュ数で数え、Local Only の Contact と、どの PhysBone からも使われていない PhysBone Collider は数えません。
- 非アクティブなオブジェクトも数えます（VRChat も数えます）。`EditorOnly` タグのオブジェクトはアップロード時に消えるので数えません。
- テクスチャメモリは、**今のビルドターゲット**の圧縮形式とサイズで計算します。Quest 向けの値を見るときは、ビルドターゲットを Android に切り替えてからチェックしてください。
- Bounds のサイズ、Raycast 数、LOD Group はランクに入れていません。Constraint の深さと PhysBone の数値は SDK の計算をまねた推定値です。**最終的な判定は VRChat SDK のビルド画面**で確認してください。
- VRChat SDK が入っていなくても動きます（その場合 PhysBone などは数えません）。SDK の型は名前で探すので、SDK のバージョンに依存しません。
- Modular Avatar などのビルド時に変わるツールを使っている場合、ここで見えるのは**ビルド前**の状態です。
- シーン全体のチェックでは、Prefab Mode で開いているプレハブは対象外です。

## 動作環境

Unity 2022.3（VRChat 推奨バージョン）。VRChat SDK 3（Avatars）があれば SDK 関連の項目も調べます。

## ライセンス

MIT License（`LICENSE.md`）。
