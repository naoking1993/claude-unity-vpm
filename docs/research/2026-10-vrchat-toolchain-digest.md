# VRChat 制作ツールチェーン 更新ダイジェスト（2026-10-23 時点）

- 調査日: 2026-10-23
- 対象期間: 2026-06-01 〜 2026-10-23
- 想定環境: Windows 11 / Unity 2022.3.22f1 / Built-in Render Pipeline / VCC（ALCOM・vrc-get も検討）/ アバターは lilToon 系、ワールドは UdonSharp / Claude Code から MCP で Unity・Blender を操作
- 推奨度の意味: **今すぐ**＝この環境で更新した方がよい、**様子見**＝更新は待つか条件付き、**不要**＝更新が出ていない・この環境では使わない
- 状況は数週間単位で変わります。更新の前に、各節の出典をもう一度確認してください。

---

## 今すぐ更新すべきもの

| ツール | 更新先 | 理由 |
|---|---|---|
| ALCOM / vrc-get | ALCOM 1.1.9 / vrc-get CLI 1.9.3（どちらも 2026-10-23） | **セキュリティ修正**。中間 CA 鍵の漏えいを受けて rustls-webpki を更新。1.1.6 以降はパッケージのハッシュ検証も強制 |
| MCP for Blender（旧 blender-mcp） | mcp-for-blender 2.2.3（2026-10-23） | **CVE-2026-66004**（Poly Haven 経由の任意ファイル書き込み。1.6.5 未満が影響）。Windows でのハングと、英語以外の Windows で setup が落ちる問題も修正。テレメトリーは環境変数で止める（後述） |
| Blender | 今使っている LTS 系列の最新版：4.5.15 または 5.2.3（タグはどちらも 2026-10-21） | 細工された .blend の読み込み時に起きるクラッシュとメモリ破壊の修正が多数。スクリプト自動実行（Auto Run）の信頼判定の不備も修正。4.2 LTS はサポート終了 |
| VRChat SDK（Avatars / Worlds） | 3.10.5（2026-09-04）。3.10.4 以前を使っている場合 | 安定版。Gesture Manager 3.9.9 が 3.10.4 以上を要求。UdonSharp の enum 誤コンパイル修正（3.10.4）を含む |
| NDMF → Modular Avatar → AAO | NDMF 1.14.8 → MA 1.18.7 → AAO 1.9.20 の順 | MA 1.18.4 以降は NDMF 1.14.7 以上が必須。MA と AAO の組み合わせで出ていた誤エラーが解消。PhysBone Collider が消える不具合なども修正 |
| lilToon | 2.4.1（2026-10-20） | 2.0.0 から続いていたライトプローブの明るさの不具合（#378）を修正。VRC Light Volumes 3.0 に最低限対応。**見た目が変わる可能性があるので、再アップロードの前に確認** |
| Meshia Mesh Simplification | 3.2.0（2026-06-16） | lilNDMFMeshSimplifier は 2026-04-11 にアーカイブされ、Meshia への移行が案内されている |
| Gesture Manager | 3.9.9（2026-08-02）。SDK 3.10.4 以上の場合 | VRCRaycast と HeadChop のシミュレーション、Box 形状 Contact に対応 |
| VizVid（使っているワールドのみ） | 1.8.2（2026-09-24） | プラットフォーム切り替え時のコンパイルエラーを修正。YouTube の読み込みに失敗したとき専用のエラーメッセージを表示。SDK 3.10.2 以上が必須 |

## 更新すると壊れる恐れがあるもの

| 対象 | 起きること | 対処 |
|---|---|---|
| Unity 6 へのプロジェクト移行 | VRChat 公式は「Do NOT upgrade your SDK projects to Unity 6. It will not work.」と明記。2022.3.22f1 より新しい Unity でアップロードしないよう警告している | **2022.3.22f1 のまま**。ALCOM 1.1.7 以降と vrc-get 1.9.2 以降も、Unity 6000.x への VRCSDK 導入を止める（テスト目的なら回避可） |
| VRChat SDK 3.10.6-beta、将来の 3.11 | 3.10.6 はまだ Open Beta で、一部の機能は Open Beta 中のクライアント 2026.4.1 を前提にしている。AAO（`com.vrchat.avatars <3.11.0`）と Gesture Manager（`<3.11.X`）には SDK の上限があるため、SDK 3.11 が出ても各ツールが対応するまで依存関係を解決できない | 3.10.5 で止める。3.11 は AAO と Gesture Manager の対応を待つ |
| Modular Avatar 1.19.0-alpha / NDMF 1.15.0-beta | NDMF のベータ版が必須になる。VRCFury 1.1373 未満と共存できない。Reactive Component の反応タイミングが変わる。条件付きの Mesh Cutter / Shape Changer が VRChat 上で効かない不具合（#2175）があり、修正はまだリリースされていない | 安定版の 1.18.7 / 1.14.8 を使う |
| Modular Avatar 1.18 系（1.17 以前から上げる場合） | 自動で割り当てる Int パラメータの値が 0〜255 を超えると、ビルドが致命的エラーで止まる | 更新後にテストビルドして確認 |
| LAC Avatar Compressor 0.9.0 | 公開 C# API が廃止された（Breaking）。lilToon の焼き込みと未使用スロットの削除が**既定で有効**。動作確認は lilToon 2.3.2 まで | 更新後にアバターごとに見た目を確認し、問題があれば「lilToon Optimizations」をオフにする |
| Meshia 3.2.0 | Read/Write が無効なメッシュがあると、ビルド全体が失敗する（#39。Unity 2022.3.22f1 で報告） | 該当するエントリを無効にするか、メッシュの Read/Write を有効にする |
| AudioLink の次のリリース（現在は master のみ） | 同期の既定値が None に変わる（PR #378、2026-10-12 マージ）。既定値を変えていないワールドは、更新すると同期しなくなる | 次のリリースに上げるときは Sync を All に戻す |
| Blender 4.5 から 5.x への系列変更 | Python が 3.13 に、NumPy が 2 に上がる。5.0 で `bpy.props` の扱いが変わり、FBX インポーターは C++ 版が既定になった。アバター用アドオンが動かない、または衣装 FBX の読み込み結果が変わる恐れがある | 4.5 LTS を使い続け、使っているアドオンが 5.2 対応を明言してから移る |

## 全ツール一覧（最新版と推奨度）

| ツール | 最新の安定版（日付） | 最新のプレリリース（日付） | 推奨 |
|---|---|---|---|
| VRChat SDK | 3.10.5（2026-09-04） | 3.10.6-beta.3（2026-10-23） | 今すぐ（3.10.5 へ）／ベータは様子見 |
| Unity（制作用） | 2022.3.22f1（指定は変わらず） | — | 不要（変えない） |
| VCC | 2.4.5（2025-12-10） | 2.5.0-beta.2（年は未確認） | 不要（期間内の更新なし） |
| ALCOM / vrc-get | 1.1.9 / 1.9.3（2026-10-23） | — | **今すぐ** |
| Modular Avatar | 1.18.7（2026-09-01） | 1.19.0-alpha.2（2026-10-17） | 今すぐ（1.18.7）／alpha は様子見 |
| NDMF | 1.14.8（2026-08-29） | 1.15.0-beta.2（2026-10-15） | 今すぐ（1.14.8）／beta は様子見 |
| Avatar Optimizer | 1.9.20（2026-10-01） | （1.9.20 より新しいものはなし） | 今すぐ |
| lilToon | 2.4.1（2026-10-20） | なし | 今すぐ（見た目を確認） |
| lilNDMFMeshSimplifier | 0.3.0（2025-07-27）。2026-04-11 にアーカイブ | — | 不要（Meshia へ移行） |
| Meshia Mesh Simplification | 3.2.0（2026-06-16） | （3.2.0 より新しいものはなし） | 今すぐ（#39 に注意） |
| LAC Avatar Compressor | 0.9.0（2026-07-17） | （0.9.0 より新しいものはなし） | 様子見 |
| GoGoLoco | 1.8.6（BOOTH での公開日は未確認） | 1.8.6-beta.1（2025-04-06、VPM 版） | 不要（期間内の更新なし） |
| Gesture Manager | 3.9.9（2026-08-02） | なし | 今すぐ（SDK 3.10.4 以上の場合） |
| VRCFaceTracking | Steam 版が現行（版は未確認）。GitHub の 5.2.3.0 はサポート終了 | なし（Avalonia 版を Steam で試験中） | 不要（機材がない場合） |
| Baballonia | v1.1.1.0rc6（2026-03-12）。推奨は Steam 版 | VRCFT Module 3.2.0 Alpha（2026-06-27） | 不要（機材がない場合） |
| UdonSharp | SDK 3.10.5 に同梱 | SDK 3.10.6-beta | 今すぐ（SDK を上げる形で） |
| ClientSim | SDK 3.10.5 に同梱 | SDK 3.10.6-beta | SDK に合わせる（単体での対応は不要） |
| iwaSync3 | 未確認（BOOTH は v3.8.0 の表示、更新確認用ファイルは v3.6.14） | 未確認 | 様子見（BOOTH の更新履歴を確認） |
| VizVid | 1.8.2（2026-09-24） | 1.8.3-beta.0（2026-10-18） | 今すぐ（使っているワールドのみ） |
| AudioLink | 3.1.2（2026-02-15） | なし | 不要（期間内のリリースなし） |
| Blender | 5.2.3 LTS（タグ 2026-10-21）／4.5.15 LTS（タグ 2026-10-21） | 5.3 Beta（正式版は 2026-11-10 予定） | **今すぐ**（使っている系列の最新版へ） |
| MCP for Blender | mcp-for-blender 2.2.3（2026-10-23） | — | **今すぐ**（設定を絞ったうえで） |

---

## 1. VRChat 本体・SDK・Unity の方針

### 1-1. VRChat の Unity バージョン方針（Unity 6 / URP）

**結論: 制作用の Unity は引き続き 2022.3.22f1 のみです。Unity 6 になったのはプレイヤー側のクライアントだけで、SDK はまだ Unity 6 に対応していません。URP への移行は、時期を含めて未発表です。**

| 層 | 状態（2026-10-23） |
|---|---|
| クライアント（遊ぶ側） | 2026.3.3（Build 1919、2026-10-02）で、全プラットフォームが Unity 6 で正式に動き始めた |
| 制作用の Unity Editor | **2022.3.22f1** のまま。公式ページは「It is safe to remain on VRChat's supported version of the Unity editor (`2022.3.22f1`)」 |
| SDK / VCC | Unity 6 対応版は未リリース。2026-08-31 更新のロードマップでは「Unity 6 VRChat SDK」は In progress |

- **公式の警告**: 2026.3.3 のパッチノートには「Nothing has changed about the SDK side」「Do NOT upgrade your SDK projects to Unity 6. It will not work.」とあり、準備ができたら告知するとしています。告知の X 投稿にも「Unity 6 (DO NOT UPGRADE YOUR SDK YET!)」とあります。
- **経緯**
  - 2026-06-25 の Developer Update: Unity 6 版クライアントの Open Beta を別ブランチで開始（Unity 6000.0.60f1）。「doesn't change anything about our render pipeline」として既存コンテンツとの後方互換を明言しました。URP への移行については「Not yet. We'll talk more about our migration plans in the future.」との回答です。
  - 2026-08-27 の Developer Update: Unity 6 版 SDK と VCC の対応を QA 中。Open Beta が始まれば、VCC から Unity 6 を自動で入れ、数クリックでプロジェクトを移行できるようにする予定です。**日付は示されていません。**
- **URP**: VRChat の公式発表は見つかりませんでした（ロードマップにも項目がありません）。Unity 側が Built-in RP を非推奨にするという話は SNS 経由の二次情報しか見つからず、一次資料は**未確認**です。
- **Unity の脆弱性**: 2025-10 に公表された Unity の脆弱性について、VRChat の公式ページは「does not apply when using the Unity Editor to create content for VRChat」としています。
- **推奨: 不要（Unity は変えない）。** 公式が「更新してよい」と告知するまで待ってください。間違えて Unity 6 でプロジェクトを開くとダウングレードは公式サポート外なので、作業前にバックアップを取るか、VCC で 2022.3.22f1 のプロジェクトを新しく作って資産を移してください。
- 出典
  - 制作用 Unity の指定（公式ドキュメントのソース）: https://github.com/vrchat-community/creator-docs/blob/main/Docs/docs/sdk/upgrade/current-unity-version.md （公開ページは https://creators.vrchat.com/sdk/upgrade/current-unity-version/ ）
  - ロードマップ（2026-08-31 更新）: https://github.com/vrchat-community/creator-docs/blob/main/Docs/docs/roadmap.md
  - 2026.3.3 のパッチノート: https://docs.vrchat.com/docs/vrchat-202633 （本文は取得できず、検索結果の抜粋で確認）
  - 告知の X 投稿: https://x.com/VRChat/status/2106126219829715239
  - Developer Update 2026-06-25: https://ask.vrchat.com/t/developer-update-25-june-2026/48618 （検索結果の抜粋で確認）
  - Developer Update 2026-08-27: https://ask.vrchat.com/t/developer-update-27-august-2026/48877 （検索結果の抜粋で確認。本文は途中で切れていた）

### 1-2. VRChat SDK（Avatars / Worlds）

- **最新版**
  - 安定版: **3.10.5（2026-09-04）**。その前は 3.10.4（2026-06-17）
  - プレリリース: **3.10.6-beta.1 / beta.2（2026-10-19）、beta.3（2026-10-23）**
- **3.10.4 の主な変更**
  - アバターの PhysBone Collider をグローバルにできる（1アバターにつき追加4個まで）
  - Box 形状の Contact
  - Udon に VRCTween（DOTween の一部機能）を追加。ワールドの PhysBone Collider を Udon から操作可能に
  - 全プロジェクトで 4MB のシェーダーチャンク分割を強制
  - VRCPickup を付けると Pickup レイヤーに自動で移動
  - UdonSharp で enum の使い方によって誤コンパイルされる不具合を修正
- **3.10.5 の主な変更**
  - VRCRaycast に Root Transform を追加
  - グローバル PhysBone Collider に Allow Self / Allow Others を追加
  - Toon Standard が AudioLink に対応
  - Udon から `VRCQualitySettings.RealtimeReflectionProbes` / `ShadowmaskMode` を操作可能に
  - UdonSharp で Assembly Version Defines が使える
  - VRCPickup に Outline Renderers を追加
- **3.10.5 の動作変更**（既存プロジェクトで注意）
  - 1つの GameObject に置ける PipelineManager は1つまで
  - SDK が知らない `VRC_ENABLE_` で始まるスクリプト定義シンボルは削除される
  - `EditorOnly` のオブジェクトを消すタイミングが、シーンの読み込み前から読み込み後に変わった
  - 自動生成される Contact / PhysBone Collider の独自 Transform は、アップロード時に検証される
- **3.10.6-beta**（以下はコミュニティのまとめ経由で、ベータ版リリースノートの本文は未確認）
  - VRCBillboard、VRCVelocityTracker、VRCPlacementZone、Interact() の Outline Renderers などを追加。一部の機能はクライアント 2026.4.1（Open Beta）が前提です。
  - 2026-10-22 の Developer Update によると、アウトライン対象の Skinned Mesh を自動で集める処理を取りやめる予定です。2026.4.1 のあとでアウトラインが期待どおりに出ない場合は、SDK 3.10.6 に上げて Renderer を明示的に指定するよう案内されています。
- **破壊的変更・非推奨・セキュリティ**: リリースノートに明記されたものはありません（上の動作変更を除く）。
- **既知の問題**: まれに「All pipe instances are busy」でビルドが失敗します（Unity 側の問題で、エディタを再起動すれば直る）。
- **推奨**
  - 3.10.4 以前なら 3.10.5 へ **今すぐ**。
  - 3.10.6-beta は **様子見**。SDK とクライアントの両方が正式リリースになってからにしてください。
- 出典
  - リリースノートのソース: https://github.com/vrchat-community/creator-docs/blob/main/Docs/releases/release-3.10.4.md 、https://github.com/vrchat-community/creator-docs/blob/main/Docs/releases/release-3.10.5.md （公開ページは https://creators.vrchat.com/releases/release-3-10-5/ ）
  - パッケージのリリース: https://github.com/vrchat/packages/releases
  - 3.10.6-beta のまとめ（コミュニティ）: https://github.com/niaka3dayo/agent-skills-vrc-udon/issues/370 。公式の情報源は https://vrc-beta-docs.netlify.app/releases/release-3-10-6 と https://ask.vrchat.com/t/developer-update-22-october-2026/49154 （どちらも取得できず）

### 1-3. VCC（VRChat Creator Companion）

- **最新版**
  - 安定版: **2.4.5（2025-12-10）**。変更点は「Updated built-in templates to use the VRChat SDK 3.10.x」の1点のみ。
  - プレリリース: 2.5.0-beta.2。GitHub での公開日は「6/25」の表示のみで、**年は未確認**です。ドキュメント上の beta.1 は 2025-06-18。2.5.0-beta.1 では、Unity の起動パラメータ設定、Changelog ボタン、リポジトリのキャッシュ見直しが入り、SemVer の解釈も変わりました（`^1.0.0` がプレリリース版を受け付けなくなる）。
- **2026-06 以降**: 新しいリリースは確認できませんでした。
- **未解決の不具合報告**
  - #679（2026-09-20）: 2.4.5 でリポジトリの更新とパッケージの取得が繰り返し失敗する
  - #672（2026-08-10）: 同じ VPM リポジトリを何度も読み込み、通信量が多い
  - #678（2026-09-13）: 管理者として実行しないと起動しない
- **Unity 6 への対応**: 1-1 のとおり QA 中で、時期は未発表です。
- **セキュリティ**: 記載なし。
- **推奨: 不要**（新しいリリースがない）。リポジトリの更新が不安定なら ALCOM を併用するのも手です。
- 出典
  - リリースノートのソース: https://github.com/vrchat-community/creator-companion/tree/main/Docs/news/releases （公開ページは https://vcc.docs.vrchat.com/news/release-2.4.5/ ）
  - リリース一覧: https://github.com/vrchat-community/creator-companion/releases
  - issue 一覧: https://github.com/vrchat-community/creator-companion/issues

### 1-4. ALCOM / vrc-get

- **最新版**
  - ALCOM（GUI）: **1.1.9（2026-10-23）**
  - vrc-get（CLI）: **1.9.3（2026-10-23）**
- **期間内の変更**
  - **1.1.9 / CLI 1.9.3**: rustls-webpki を 0.103.16 に更新する hotfix。eMudhra の中間 CA 鍵が漏えいしたことへの対応で、CVE 番号は振られていません。GUI 側では、macOS で `vcc:` のディープリンクが動かない問題と、プロジェクト作成がダイアログを出さずに失敗する問題も修正しています。
  - **1.1.8（2026-07-11）**: Windows で全ユーザー向けにインストールした場合のアップデーターの失敗と、WebView2 のインストール失敗を修正。
  - **1.1.7（2026-07-10）/ CLI 1.9.2（2026-07-12）**
    - Unity 2022 用の VRCSDK を Unity 6000.x へ入れるのをブロック（エラーではないので、テスト目的なら無視できる）
    - SDK 3.10.2〜3.10.3 向けの VRCDefaultWorldScene の回避処理を削除（SDK 3.10.4 で修正されたため）
    - 作成日でのプロジェクト並べ替え、テンプレートの複製、aarch64 版（実験的）を追加
  - **1.1.6（2026-06-02）/ CLI 1.9.2**
    - パッケージのハッシュ検証を強制（原文「It is now enforced for security.」）。ハッシュが合わないパッケージは入らなくなります。
    - settings.json のバックアップ、バックアップの高速化
- **未リリース**: ライセンスを MPL-2.0 に変更。プロジェクト一覧を ALCOM 自身で管理し、VCC との同期方法を設定で選べるようにする予定です。
- **推奨: 今すぐ**（セキュリティ修正のため）。
- 出典
  - CHANGELOG: https://github.com/vrc-get/vrc-get/blob/master/CHANGELOG-gui.md 、https://github.com/vrc-get/vrc-get/blob/master/CHANGELOG.md
  - リリース: https://github.com/vrc-get/vrc-get/releases/tag/gui-v1.1.9

---

## 2. アバター改変ツール

### 2-1. Modular Avatar（MA）

- **最新版**
  - 安定版: **1.18.7（2026-09-01）**
  - プレリリース: **1.19.0-alpha.2（2026-10-17）**
- **期間内の主な変更**
  - **1.18.0（2026-08-02）**
    - `MA Move To` と `MA Outfit Root` を追加（Scale Adjuster の値を衣装にコピーできる）
    - Vertex Filter に UV タイル指定、UV チャンネル選択、三角形単位の選択を追加
    - Merge Motion で複数のオブジェクトを同時に編集できる
  - **1.18.1〜1.18.7（08-03〜09-01）**
    - VRCSDK 3.7.0 でのコンパイルエラーを修正
    - BlendShape Sync を、1.18 より前の挙動を前提にするツールとも合うよう修正（#2097）
    - Unity 6.1〜6.7 を実験的にサポート（1.18.4）
    - Mesh Cutter、Shape Changer、Floor Adjuster、Reactive Component のプレビューなどを修正
  - **1.19.0-alpha.0〜alpha.2（09-01〜10-17）**
    - Reactive Components を blend tree 方式に作り直した
    - `MA Reactive Move` を追加
- **破壊的変更・注意点**
  - 1.18.0: 自動で割り当てる Int パラメータの値が 0〜255 を超えると、ビルドが致命的エラーで止まるようになりました（#2028）。
  - 1.19.0-alpha.0
    - 「Modular Avatar no longer supports co-existence with VRCFury versions older than 1.1373.」
    - Reactive Component の反応タイミングがわずかに変わる可能性があります。
  - 1.19.0-alpha.1: `UnityObjectIDHelper` を公開 API から削除しました（NDMF 側のものを使う）。
  - 1.19 系で作ったアセットを 1.18 系で開くと警告が出ます（#2060）。
  - 1.19 alpha の未リリースの不具合: 条件で動かす Mesh Cutter / Shape Changer（削除モード）が VRChat 上で効かない（#2175）。2026-10-21 に修正がマージされましたが、まだリリースされていません。
- **必要な NDMF のバージョン**
  - 1.18.4〜1.18.7: `>=1.14.7`
  - 1.19.0-alpha.1 以降: `>=1.15.0-beta.1`（NDMF のプレリリース版が必須）
  - Unity の指定はどの版も `2022.3` です。
- **セキュリティ**: 記載なし。
- **推奨**
  - 1.18.7 へ **今すぐ**（NDMF 1.14.8 と一緒に）。更新後にテストビルドで Int パラメータの範囲エラーが出ないか確認してください。
  - 1.19 alpha は **様子見**。
- 出典
  - リリース: https://github.com/bdunderscore/modular-avatar/releases
  - CHANGELOG: https://github.com/bdunderscore/modular-avatar/blob/main/CHANGELOG.md 、https://github.com/bdunderscore/modular-avatar/blob/main/CHANGELOG-PRERELEASE.md
  - 依存バージョン: https://raw.githubusercontent.com/bdunderscore/modular-avatar/1.18.7/package.json
  - issue: https://github.com/bdunderscore/modular-avatar/issues/2175

### 2-2. NDMF

- **最新版**
  - 安定版: **1.14.8（2026-08-29）**
  - プレリリース: **1.15.0-beta.2（2026-10-15）**
- **期間内の主な変更**
  - 1.14.0（2026-06-29）
    - 「Fix compatibility issues with Unity 6000.x」
    - アバターのルートが EditorOnly のときに明示的なエラーを出すようにした
  - 1.14.1: ステップ実行できるデバッガーを追加
  - 1.14.2〜1.14.3: VRCConstraints の修正、プレビューの高速化
  - 1.14.5: blend tree が使うパラメータを Float に揃えるようにした（動作変更）
  - 1.14.7: Unity 6.1〜6.7 を実験的にサポート。どちらを選ぶか曖昧な platform provider はエラーとして報告するようにした
  - 1.15.0-beta: ShadowBoneManager を追加。API の互換性は保証されていません
- **非推奨**: `CloneContext.MapClipOnClone()` が何もしないようになり、obsolete 扱いになりました（プレリリース版の CHANGELOG のみに記載）。
- **セキュリティ**: 記載なし。
- **推奨**
  - 1.14.8 へ **今すぐ**（MA 1.18.7 を入れる前提条件）。
  - 1.15 beta は **様子見**。
- 出典
  - リリース: https://github.com/bdunderscore/ndmf/releases
  - CHANGELOG: https://github.com/bdunderscore/ndmf/blob/main/CHANGELOG.md

### 2-3. Avatar Optimizer（AAO）

- **最新版**
  - 安定版: **1.9.20（2026-10-01）**
  - プレリリース: 1.9.20 より新しいものはありません。
- **期間内の主な変更**
  - 1.9.15（06-24）: Merge Bone で起きる z-fighting を修正（髪の透け表現との互換性）
  - 1.9.16（07-13）: テクスチャ使用量の見積もりを、VRCSDK を使う方法から独自のロジックに切り替えた
  - 1.9.17（08-08）: PhysBone の EndBone を置き換えたときの不具合を修正
  - 1.9.18（08-31）
    - Unity 6000.x に初期対応（正式サポートではない）
    - SDK 3.10.5 の Toon Standard の AudioLink 対応に追従
    - MA の BlendShape Sync との組み合わせで出ていた誤エラーを修正（#1778）
  - 1.9.19（09-11）: bHaptics 用の Contact が削除される問題を修正
  - 1.9.20（10-01）
    - x100 スケールのアバターで、3mm 未満の近いボーンが誤って統合される問題を修正
    - 同じ Transform に無効なコライダーがあると、PhysBone Collider が削除される問題を修正
- **破壊的変更**: 期間内にはありません。
- **依存関係**: `com.vrchat.avatars >=3.7.0 <3.11.0`、`nadena.dev.ndmf >=1.8.0 <2.0.0`。**SDK 3.11 が出ると、AAO が対応するまで依存関係を解決できなくなります。** README には、最低要件はマイナーバージョンで上がることがあると書かれています。
- **セキュリティ**: 記載なし。
- **推奨: 今すぐ。**
- 出典
  - リリース: https://github.com/anatawa12/AvatarOptimizer/releases
  - CHANGELOG: https://github.com/anatawa12/AvatarOptimizer/blob/master/CHANGELOG.md
  - 依存バージョン: https://raw.githubusercontent.com/anatawa12/AvatarOptimizer/v1.9.20/package.json

### 2-4. lilToon

- **最新版**
  - 安定版: **2.4.1（2026-10-20）**
  - プレリリース: なし。3.x などの次世代版の公式な計画も見つかりませんでした（**未確認**）。
- **期間内の変更**
  - 2.3.3（06-16）: プリセットで HDR カラーが保存されない、テクスチャの UV 設定がコピーされない、などを修正
  - 2.3.4（06-25）: URP の追加ライトをピクセルシェーダーに移動（Built-in には関係なし）。GIF パーサーを内蔵
  - 2.4.0（10-19）
    - 「最低限のVRCLV 3.0対応」
    - UI Toolkit のアセットを含むシーンをビルドするとクラッシュする問題を修正
    - lilToonMulti のアウトラインの RenderQueue を修正
  - 2.4.1（10-20）: 「ライトプルーブの明るさが正しくない」を修正。2.0.0 から続いていた照明の不具合（#378）への対応です。
- **破壊的変更・非推奨**: 記載なし。
- **セキュリティ**: 記載なし。
- **推奨: 今すぐ**（2.4.1 へ。2.4.0 は飛ばしてよい）。ライトプローブの下での明るさが変わる可能性があるので、ワールドで見た目を確認してから再アップロードしてください。
- 出典
  - CHANGELOG: https://github.com/lilxyzw/lilToon/blob/master/Assets/lilToon/CHANGELOG_JP.md
  - issue: https://github.com/lilxyzw/lilToon/issues/378
  - VPM リスティング: https://github.com/lilxyzw/vpm-repos

### 2-5. lilNDMFMeshSimplifier

- **状態: 開発終了。** リポジトリは 2026-04-11 にアーカイブされています。README には「This project is discontinued and is no longer maintained.」「Please use Meshia Mesh Simplification instead.」とあります。
- **最終版**: 0.3.0（2025-07-27）。Meshia への移行ボタンが付いています。
- **推奨: 不要**（もう更新は出ない）。使っている場合は Meshia に移行してください。
- 出典: https://github.com/lilxyzw/lilNDMFMeshSimplifier

### 2-6. Meshia Mesh Simplification

- **最新版**
  - 安定版: **3.2.0（2026-06-16）**
  - プレリリース: 3.2.0 より新しいものはありません。
- **期間内の変更**
  - 3.1.1（06-09）: UV1〜7 で max UV distance が無視される問題を修正
  - 3.2.0（06-16）
    - `[RunsOnAllPlatforms]` に対応
    - 既定で手のボーンを保持するようにした（新しく追加したエントリにだけ効くとみられるが、既存のエントリへの影響は**未確認**）
- **既知の問題**
  - #39（2026-08-01、未解決）: 読み取り不可（Read/Write 無効）のメッシュがあると、NDMF のパスで InvalidOperationException が出てビルド全体が失敗する。報告環境は 3.2.0 と Unity 2022.3.22f1 です。
  - #34（未解決）: AAO の Remove Mesh と併用すると、表示上の頂点数とビルド結果が食い違う。
- **破壊的変更・セキュリティ**: 記載なし。
- **推奨: 今すぐ。** ビルドが失敗したら、まず #39 を疑ってください。
- 出典
  - リリース: https://github.com/RamType0/Meshia.MeshSimplification/releases
  - issue: https://github.com/RamType0/Meshia.MeshSimplification/issues/39

### 2-7. LAC Avatar Compressor（LAC Texture Compressor）

- **特定**: 「LAC Texture Compressor」は、Limitex/avatar-compressor（表示名「Avatar Compressor」、パッケージ名 `dev.limitex.avatar-compressor`）に含まれるコンポーネントの名前です。名前が似た別のプロジェクト（例: Markcreator/VRCTextureCompressor）とは別物です。
- **最新版**
  - 安定版: **0.9.0（2026-07-17）**
  - プレリリース: 0.9.0-rc.1（2026-07-10）。これより新しいものはありません。
- **0.9.0 の主な変更**
  - lilToon のテクスチャ焼き込みを追加。色調補正、2nd/3rd レイヤー、アルファマスクなどをビルド時に焼き込みます（**既定で有効**）。
  - 使われていない lilToon のテクスチャスロットを検出して削除（**既定で有効**）。
  - VRCFury と併用したときに、全テクスチャが過度に圧縮される問題を修正。
- **破壊的変更**: 「The package no longer exposes a public C# API」。全ての型が internal になりました。コンポーネント、プリセット、インスペクターは影響を受けません。エディタ拡張や MCP から実行するスクリプトで LAC の型を直接参照している場合は、コンパイルが通らなくなります。
- **注意**
  - lilToon との動作確認は「1.8.0 through 2.3.2」までです。
  - lilToon のバージョンが想定より新しいと焼き込みを自動で止める仕組みがあり、lilToon 2.4.1 では止まらない（焼き込みは行われる）ことをソースで確認しました。
  - 2026-05-01 の 0.8.0 から除外パスの既定値が空になっています。それより前の版から上げる場合は、除外設定を見直してください。
- **要件**: Unity 2022.3.22f1、VRChat SDK Avatars 3.10.0 以上、NDMF 1.10.0 以上。
- **セキュリティ**: 記載なし。
- **推奨: 様子見。** すでに使っているなら、更新後にアバターごとに見た目を確認してください。問題があれば「lilToon Optimizations」をオフにします。
- **未確認**: BOOTH（https://booth.pm/ja/items/7856254 ）と公式ドキュメント（https://lac.limitex.dev ）は開けませんでした。
- 出典
  - リポジトリ: https://github.com/Limitex/avatar-compressor
  - CHANGELOG: https://github.com/Limitex/avatar-compressor/blob/main/CHANGELOG.md

---

## 3. アバターの確認・トラッキング

### 3-1. GoGoLoco

- **最新版**: BOOTH では **1.8.6** と表示されています（検索結果のタイトルで確認。公開日は**未確認**）。VPM 版（Spokeek/goloco）も 1.8.6 が最新で、タグの日付は 2024-09-28 です。プレリリースは 1.8.6-beta.1（2025-04-06）です。
- **2026-06 以降**: リリースはありません。
- **配布元の変化**: `franada/gogoloco` は `Franada/gogoloco-legacy` に転送されます。VPM のリスティングは https://spokeek.github.io/goloco です（README には「for now, links to personal listing」とあります）。
- **セキュリティ**: 記載なし。
- **推奨: 不要。**
- 出典
  - https://github.com/Franada/goloco
  - https://github.com/Spokeek/goloco/tags
  - BOOTH: https://booth.pm/en/items/3290806 （開けず）

### 3-2. Gesture Manager

- **最新版**
  - 安定版: **3.9.9（2026-08-02）**
  - プレリリース: GitHub にはありません。
- **主な変更**
  - Remote Clones（他人から見た自分のアバターを確認できる）
  - VRCRaycast のシミュレーション、VRCHeadChop のエミュレーション
  - パラメータのスライダーと数値入力
  - Box 形状の Contact Receiver に対応
  - Parameter Driver と Contact の localOnly に対応
- **破壊的変更**
  - 動作要件が「Only for Unity 2022 and VRCSDK 3.10.4 or above!」に上がりました。
  - package.json は `"com.vrchat.avatars": ">=3.10.4 < 3.11.X"` で、上限が付いています。上限の撤廃を求める issue #81 は未解決です。
- **既知の退行**: #100「Global phsybones colliders collide with physbones with no self interaction」が未解決で、次のリリースで修正予定です。
- **セキュリティ**: 記載なし。
- **推奨: 今すぐ（SDK 3.10.4 以上の場合）。** SDK が 3.10.3 以前なら、先に SDK を上げてください。グローバル PhysBone Collider の確認が中心なら、#100 が直るまで 3.9.8 のままでも構いません。
- 出典
  - リリース: https://github.com/BlackStartx/VRC-Gesture-Manager/releases/tag/v3.9.9
  - 依存バージョン: https://raw.githubusercontent.com/BlackStartx/VRC-Gesture-Manager/master/package.json
  - issue: https://github.com/BlackStartx/VRC-Gesture-Manager/issues

### 3-3. VRCFaceTracking

- **最新版**
  - 現在の配布は Steam 版です。Steam でのバージョンは**未確認**です。
  - GitHub で Latest になっている 5.2.3.0 には、本文の先頭に「WE DO NOT SUPPORT THIS VERSION ANYMORE.」とあります。
- **2026-06 以降**: タグの付いたリリースはありません。master では開発が続いています。
  - PR #372「Avalonia UI switch」（2026-09-07 マージ）: WinUI3 から Avalonia に移行し、Linux に対応。vcruntime が不要になりました。作者は Steam のベータブランチでのテストを呼びかけています。
  - 2026-10: OSC の UTF-8 対応。アバター名に Unicode を含むと再読み込みできない問題を修正。
- **破壊的変更**: モジュール API の変更は明記されていません。
- **セキュリティ**: 記載なし。
- **推奨: 不要（フェイストラッキングの機材を使っていない場合）。** 使っているなら、GitHub の旧版から Steam 版に移っているかだけ確認してください。Avalonia 版は様子見です。
- 出典
  - https://github.com/benaclejames/VRCFaceTracking/releases/tag/5.2.3.0
  - https://github.com/benaclejames/VRCFaceTracking/pull/372

### 3-4. Baballonia（Project Babble の後継）

- **最新版**
  - GitHub の Latest は **v1.1.1.0rc6（2026-03-12）** です。README では Steam 版を推奨しています。
  - プレリリース: **Expressions Alpha - VRCFT Module 3.2.0（2026-06-27）**
- **2026-06 以降**: VRCFT モジュール 3.2.0 のみです（空の画像を無視するようにした、ログの削減）。アプリ本体のリリースはありません。
- **移行**
  - VRChat で使うには、VRCFaceTracking と `VRCFT-Babble` モジュールが必要です。
  - rc2 で .NET 10 に上がりました。
  - 旧 ProjectBabble アプリは「legacy desktop app」という位置づけです。最終版は v2.0.7（2025-01-15）。サポート終了の明言や、設定を移す手順は見つかりませんでした（**未確認**）。
- **セキュリティ**: 記載なし。
- **推奨: 不要（Babble の機材がない場合）。** 旧アプリを使っているなら、Steam 版の Baballonia と VRCFT-Babble モジュールに移るのが公式の推奨経路です。
- 出典
  - https://github.com/Project-Babble/Baballonia
  - https://github.com/Project-Babble/Baballonia/releases

---

## 4. ワールド制作

### 4-1. UdonSharp

- **状態**: SDK 3.4.0（2023-10）から Worlds SDK に同梱されています。単体版は 1.1.9（2023-08-01）が最後です。変更点は VRChat SDK のリリースノートに書かれます。
- **期間内の変更**
  - SDK 3.10.4
    - enum の誤コンパイルを修正
    - `UdonSharpBehaviour` が `IUdonEventReceiver` を実装
    - VRCTween を UdonSharp から使える
    - VRCDataList / VRCDataDictionary を容量を指定して作れる
  - SDK 3.10.5
    - Assembly Version Defines が使える（SDK のバージョンなどでコードを切り替えられる）
    - UdonSharp の更新中はアセットのインポートを止める
    - VRCJson の解析を厳しくした（閉じ括弧の不一致などを拒否する）
- **注意**: 3.10.5 から、SDK が知らない `VRC_ENABLE_*` のスクリプト定義シンボルは削除されます。独自の定義を使っている場合は確認してください。
- **セキュリティ**: 記載なし。
- **推奨: 今すぐ（SDK 3.10.5 に上げる形で）。**
- 出典
  - https://github.com/vrchat-community/creator-docs/blob/main/Docs/releases/release-3.4.0.md
  - 1-2 節のリリースノート

### 4-2. ClientSim

- **状態**: SDK に同梱されています。単体版は 1.2.7（2023-08-22）が最後です。
- **期間内の変更（SDK 3.10.5）**
  - プレイモードから編集モードに切り替える間に `OnDisable()` などのイベントが走らないようにした
  - player persistence の保存が重ならないようにした
- リポジトリには 2026-07-23 に修正 PR が2件マージされています（#134、#135）。どの SDK に入るかは**未確認**です。
- **推奨: SDK に合わせる**（単体での対応は不要）。
- 出典
  - https://github.com/vrchat-community/ClientSim
  - 1-2 節のリリースノート

### 4-3. iwaSync3

- **最新版: 未確認。**
  - 配布元の BOOTH（https://booth.pm/ja/items/2666275 ）は開けませんでした。検索結果の抜粋には「iwaSync3_v3.8.0.zip」とあります。
  - 一方、アップデート確認用のファイル（https://github.com/hoshinolabs-vrchat/iwaSync3-Public/blob/main/releases.txt 、最終更新 2025-02-27）は v3.6.14 を示していて、食い違っています。
  - 旧 GitHub リポジトリはアーカイブされています。
- **2026-06 以降の変更・後継製品**: **未確認。**
- **推奨: 様子見。** BOOTH の更新履歴をブラウザで直接確認してください。
- **動画ワールド全般の注意（VRChat 側の仕様）**
  - VRChat の allowlist ページに「Video restrictions in Public instances」の節が加わりました（ドキュメントのソースへの反映は 2026-10-15）。原文は「In Public and Group Public instances of your world, videos from untrusted URLs cannot be played unless you add their domain to your world's allowlist」で、Udon に直書きした URL にも適用されます。
  - Public で外部ドメインの動画を流すワールドは、ワールド設定の「Video Player Allowed Domains」を確認してください。
  - この制限自体がいつ始まったか、登録できるドメインの上限数は**未確認**です。
  - 出典: https://github.com/vrchat-community/creator-docs/commit/599ad35 （公開ページは https://creators.vrchat.com/worlds/udon/video-players/www-whitelist/ ）

### 4-4. VizVid（VVMW）

- **最新版**
  - 安定版: **1.8.2（2026-09-24）**
  - プレリリース: **1.8.3-beta.0（2026-10-18）**
- **期間内の変更**
  - 1.7.5（06-04）: 自動スケーリングを修正。「Add screen target」「Add audio source」の入力欄を廃止し、インスペクターにドロップする方式に変更。
  - 1.8.0（09-03）
    - YouTube の読み込みに失敗したとき専用のエラーメッセージ
    - VRC Light Volumes V3 に対応、LTCGI CRT の明るさ調整
    - 5.1ch / ステレオ用の Audio Source Configurator
  - 1.8.1（09-22）: プラットフォーム切り替え時のコンパイルエラー（#94）を修正
  - 1.8.2（09-24）: VRCLV を初めてインポートしたときのコンパイルエラーを修正
  - 1.8.3-beta.0（10-18）
    - スーパーサンプルモードを追加
    - プレイヤーを無効にすると再生も止まるように変更
- **要件の変更**: 1.8.0 から `com.vrchat.worlds` が `>=3.10.2` に上がりました（1.7.5 は `>=3.9.0`）。
- **破壊的変更**: 明記はありません。インスペクターの配置が変わっているので、既存のワールドでは設定を見直してください。
- **セキュリティ**: 記載なし。
- **推奨: 今すぐ（使っているワールドのみ、1.8.2 へ）。** 1.8.3-beta は様子見です。
- 出典
  - リリース: https://github.com/JLChnToZ/VVMW/releases
  - 依存バージョン: https://raw.githubusercontent.com/JLChnToZ/VVMW/1.8.2/Packages/idv.jlchntoz.vvmw/package.json

### 4-5. AudioLink

- **最新版**
  - 安定版: **3.1.2（2026-02-15）**
  - プレリリース: なし。
- **2026-06 以降**: リリースはありません。
- **master の未リリースの変更**
  - 2026-07-16: パッケージのプロジェクトを Unity 2022.3.22f1 に更新
  - **PR #378（2026-10-12 マージ）**: 「Default the controller to no sync」。原文は「Syncing is now opt-in per world.」です。prefab の同期モードを変えていないワールドは、更新後に None になります。従来の挙動に戻すには All に設定し直す必要があります。
- **過去の破壊的変更（古い版から上げる場合）**: 3.1.0（2026-01）で AudioReactive 系のコンポーネントのフィールド名と型が変わりました。スクリプトから直接参照している場合は影響があります。
- **関連**: SDK 3.10.5 で、Toon Standard が AudioLink に対応しました。
- **セキュリティ**: 記載なし。
- **推奨: 不要**（期間内のリリースなし）。次のリリースでは、Sync の設定を確認してから上げてください。
- 出典
  - リリース: https://github.com/llealloo/audiolink/releases
  - PR: https://github.com/llealloo/audiolink/pull/378

---

## 5. Blender と MCP

### 5-1. Blender

- **最新版**
  - **5.2.3 LTS**（タグ 2026-10-21。blender.org での公開は 2026-10-22 と検索結果に表示）
  - **4.5.15 LTS**（タグ 2026-10-21）
  - 5.2 LTS の各版: 5.2.0（2026-07-14）、5.2.1（08-25）、5.2.2（09-15）
  - 4.2 LTS はサポート終了（最後は 4.2.23、タグ 2026-07-20）
- **サポート期限**（検索結果の抜粋）: 5.2 LTS は 2028-07 まで、4.5 LTS は 2027-07 まで。
- **プレリリース**: 5.3 Beta。RC は 2026-11-04、正式版は 2026-11-10 の予定です（検索結果の抜粋）。Windows では Vulkan バックエンドが既定になる予定です。
- **2026-06 以降の主な変更（5.2 LTS）**: 検索結果の抜粋より
  - Geometry Nodes ベースの物理（実験的）
  - オンラインのアセットライブラリ
  - LoopTools の一部機能を本体に統合
  - ボーンの「Duplicate and Rename」
  - Ctrl+L の「Copy Constraints」
- **FBX**
  - エクスポーターは、期間内に機能の変更はありません。
  - C++ 版インポーターで「FBX importer silently drops some animations」（#160660）が修正されました。
- **破壊的変更（アドオンへの影響）**
  - 5.2 の Python API
    - Geometry Nodes モディファイアの入力が、本物の RNA プロパティに変わりました。既存の RNA パスは使えなくなります。
    - `paint.eraser_brush` を削除しました。
  - 4.5 から 5.x に上げる場合
    - 同梱の Python が 3.11 から 3.13 に、NumPy が 1.26 から 2.3 に上がります。
    - 5.0 で `bpy.props` のプロパティがカスタムプロパティと別の入れ物になり、`scene['cycles']` のような辞書アクセスが使えなくなりました。
    - 5.0 から FBX インポーターは C++ 版が既定です。
- **セキュリティ関連の修正**（4.5.15 と 5.2.3 の両方。2026-10-20 のコミット）
  - .blend の読み込み処理の堅牢化が数十件あります。不正なデータで起きるクラッシュ、範囲外読み取り、解放後使用など。
  - 「Fix: Core: Auto-run scripts trusted for unintended files」: 1つのファイルを信頼すると、意図しない別のファイルまでスクリプト自動実行が信頼されてしまう不具合の修正です。
  - 「Live edit should not be loaded from blend file」「Freestyle: Python style modules ignore auto-run setting」「Stop using `eval()` in Rigify code」なども修正されています。
  - 公式のセキュリティアドバイザリや CVE との対応は**未確認**です。公式は「security」と明記していません。
  - 検索結果には CVE-2026-60103（.blend の範囲外読み取り、影響は 3.0.0〜5.1.2）も出てきましたが、本体のページを取得できず**未確認**です。
- **推奨**
  - 使っている LTS 系列の最新版（4.5.15 または 5.2.3）へ **今すぐ**。配布物の .blend を開く機会が多いためです。
  - 4.2 を使っている場合は 4.5.15 へ。
  - 4.5 から 5.2 への移行は **様子見**（アバター用アドオンの 5.2 対応を待つ）。
  - 5.3 Beta は **不要**。
- **取得経路の注記**: blender.org は DNS エラーで開けませんでした。そのため、GitHub のミラー（タグ、コミット、ソース内のバージョン定義）と検索結果の抜粋で確認しています。
- 出典
  - タグ: https://github.com/blender/blender/tags
  - 4.5.15 のコミット: https://github.com/blender/blender/commits/v4.5.15?since=2026-09-15&until=2026-10-22
  - 5.2.3 のコミット: https://github.com/blender/blender/commits/v5.2.3?since=2026-09-15&until=2026-10-22
  - Auto Run の修正: https://github.com/blender/blender/commit/752e3134302d8afbf6d8b0d467cf30abc05f9a5e
  - 公開ページ: https://www.blender.org/download/lts/ 、https://developer.blender.org/docs/release_notes/5.2/python_api/

### 5-2. MCP for Blender（旧 ahujasid/blender-mcp）

- **改名**
  - リポジトリは `ahujasid/mcp-for-blender` です（旧 URL は転送されます）。PyPI のパッケージ名も `mcp-for-blender` に変わりました（2026-09-16）。
  - 旧パッケージ `blender-mcp` の 2.0.0 は、新パッケージを呼び出す互換ラッパーです。README には「Existing setups keep working; no config change is required.」とあります。
- **最新版**
  - **2.2.3（2026-10-23）**
  - GitHub Releases にリリースはなく、変更点はコミット履歴でしか追えません。
- **期間内の主な変更**（コミット履歴より）
  - 06-04
    - get_*_status の docstring からプロンプトインジェクションになりうる行を削除
    - アドオンの登録時に TCP サーバーを自動で起動
  - 07-20: Poly Haven 経由の任意ファイル書き込みを修正（#258）
  - 07-21: テレメトリーの同意を opt-in に変更
  - 08-16: Windows で起きる無期限のハングを修正
  - 09-02: コード実行を制限する Safe Mode を追加
  - 09-15: `export_scene` ツール、`--host` / `--port` オプションを追加
  - 10-05
    - `update` コマンドを追加
    - 英語以外の Windows で setup が落ちる問題を修正
  - 09〜10月: Premium（有料）、PolyPizza、Meshy などの外部連携を追加
- **セキュリティ**（GitHub Advisory Database）
  - **CVE-2026-66004 / GHSA-4h8q-hh2j-755w**（Moderate、CVSS 6.0、2026-07-24 掲載）: `download_polyhaven_asset` のパストラバーサルにより、任意のファイルを書き込める。影響は `blender-mcp` 1.6.5 未満で、1.6.5 で修正済み。
  - **CVE-2026-10661 / 10662 / 10688**（いずれも Low・未レビュー、2026-06-03 掲載）: Hunyuan3D 連携のインジェクションと SSRF、`execute_blender_code` へのコードインジェクション。本家で修正されたかは**未確認**です。
  - 設計上の性質（README）
    - 任意の Python を Blender 内で実行できます。
    - localhost:9876 のソケットサーバーには「no authentication or encryption」とあります。
  - テレメトリー
    - プロンプトやコードなどの内容は、同意したときだけ送られます。
    - 匿名の最小限の利用記録（ツール名、成否、所要時間、版、OS、Blender のバージョン、永続 ID）は既定で送られることを、ソースで確認しました。
    - `DISABLE_TELEMETRY=true` で完全に止められます。
- **更新手順**
  1. `uvx mcp-for-blender@latest update` を実行します。サーバーと、インストール済みのアドオンをまとめて更新し、置き換えたファイルの `.bak` を残します。初回は `@latest` が必要です（2.1.7 より前の版には `update` コマンドがないため）。
  2. Claude Code と Blender を再起動します。
  3. Claude Code の MCP 設定を見直します。
     - 新しい登録方法は `claude mcp add blender uvx mcp-for-blender` です。
     - `env` に `DISABLE_TELEMETRY=true` を加えます。
     - 毎回最新版を取りに行かないよう、`uvx mcp-for-blender@2.2.3` のように版を固定するのが安全です。
  4. 使わない外部連携（Poly Haven、Sketchfab、Hyper3D、Hunyuan3D、Premium など）は、アドオンの設定でオフにします。
  5. Safe Mode（`BLENDER_MCP_SAFE_MODE=1`）は、FBX 出力など普段の作業が通ることを確かめてから有効にします。
- **推奨: 今すぐ**（上の設定を絞ったうえで）。
- 出典
  - リポジトリ: https://github.com/ahujasid/mcp-for-blender
  - コミット履歴: https://github.com/ahujasid/mcp-for-blender/commits/main
  - テレメトリーのソース: https://github.com/ahujasid/mcp-for-blender/blob/main/src/blender_mcp/telemetry.py
  - PyPI: https://pypi.org/project/mcp-for-blender/ 、https://pypi.org/project/blender-mcp/
  - Advisory: https://github.com/advisories/GHSA-4h8q-hh2j-755w 、https://github.com/advisories?query=blender-mcp

---

## 付録: 調査方法と限界

- **取得経路**
  - この環境からは、creators.vrchat.com、ask.vrchat.com、docs.vrchat.com、vcc.docs.vrchat.com、booth.pm、blender.org、steamcommunity.com などが DNS エラーで開けませんでした。
  - そのため、次の経路で確認しています。
    - VRChat 公式ドキュメントのソース（github.com/vrchat-community/creator-docs、creator-companion）
    - VRChat の GitHub（github.com/vrchat/packages）
    - 各リポジトリの CHANGELOG、Releases、Atom フィード、package.json
    - WebSearch の検索結果の抜粋
  - 抜粋でしか確認できなかったものは、各節に「検索結果の抜粋で確認」と明記しています。
- **日付の年**: GitHub の Releases ページを WebFetch で読むと年が表示されません。年は CHANGELOG の日付付き見出し、Atom フィード、コミット履歴、PyPI の履歴で確かめました。確かめられなかったものは「年は未確認」としています。
- **未確認のまま残ったもの**
  - VRChat SDK 3.10.6-beta のリリースノート本文
  - Developer Update（2026-10-08 / 10-22）の本文
  - iwaSync3 と GoGoLoco の BOOTH 上の最新版と公開日
  - VRCFaceTracking と Baballonia の Steam 版のバージョン
  - LAC の公式ドキュメント
  - VPM リスティング（vpm.nadena.dev、vpm.anatawa12.com など）との照合
  - Blender の修正に対応する公式アドバイザリ
