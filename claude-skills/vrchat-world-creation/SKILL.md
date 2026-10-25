---
name: vrchat-world-creation
description: "VRChatのワールドをUnity 2022.3.22f1とWorlds SDK 3.10.5で作る・直す・アップロード前に点検するときに使う。シーンとVRC Scene Descriptor（スポーン・リスポーン高さ・Reference Camera）、レイヤーと衝突マトリクス、ライトベイク・ライトプローブ・オクルージョン、ミラー・動画プレイヤー・音、Udon/UdonSharpのギミックと同期（オーナー・RequestSerialization・後から入った人）、ワールドのQuest/Android/iOS対応、ClientSimとBuild & Testでの確認、SDKがアップロードを止める原因の切り分けを扱う。ワールドについての「ワールド作りたい」「ワールドのシーン作って」「スポーン地点決めて」「ワールドにミラー置いて」「ミラーをボタンで切り替えたい」「椅子に座れるようにして」「テレポートさせたい」「物を持てるようにして」「動画プレイヤー置きたい」「BGM流したい」「ライトベイクして」「暗い」「重い」「Quest対応したい」「スマホでも入れるように」「Udonで同期するスイッチ作って」「同期しない」「後から来た人だけずれる」「ボタンが押せない」「ワールドをアップロードしたい」「アップロードできない」「Build & Testして」等の相談で、軽い言い方でも使う。アバターの改変・軽量化・Quest対応・表情はvrchat-avatar-optimizationなどvrchat-*のアバター用スキル、アバターの物をワールド空間に置いておくワールド固定ギミックはvrchat-expression-debugging、手持ち小物はvrchat-hand-prop-setupへ。Unity/MCPの接続確認と操作の規律はunity-mcp-operations、cct_*ツールの引数と出力の読み方はclaude-creator-tools、SDK・Unityの対応版や更新可否だけの質問はvrchat-unity-version-checkへ。"
---

# VRChatワールド制作（Unity × MCP）

**ステータス: v0（2026-10-25 作成・Unity実機/VRChat実機で未検証。一次資料: VRChat公式ドキュメント 2026-10-20 時点、SDK 3.10.5）。**

VRChatのワールドを、MCP for Unity経由でClaudeがUnityを操作して作る・直すための手順。ワールドは「アップロードできる」だけでは足りない。SDKが止める条件、実行時の同期、プラットフォームごとの負荷と制約を、それぞれ別の段階で確かめる。

このスキルの作成・参照だけを頼まれた場合は、Unityを変更しない。スキルを読み込んだだけでは、Unityへの接続や操作を許可したことにはならない。

ワールドの作成・修正の依頼だけで、Build & Publish（アップロード）やCommunity Labsへの公開まで行わない。アップロードは本人が明示したときだけ、本人の操作で行う（SDKの著作権同意・サムネイル選択のダイアログがMCPのブリッジを止めるため）。

Unityエディタ上の静的な確認、ClientSim、Build & Test（ローカルのVRChatクライアント）、アップロード後のVRChat実機（PC/Android/iOS別）は別の証拠として扱う。確認していない段階を合格と書かない。

## 最初に確かめること

1. 接続先：Unity MCPのtoolが今のセッションに実在するか、接続中のプロジェクト・開いているシーン・Play中かを読む（`unity-mcp-operations` 原則1）。`cct_environment` があれば、プロジェクトの種類（ワールド/アバター）、Unityの版、`com.vrchat.worlds` などのパッケージの版、開いているシーン、Play中か、コンパイル中かをまとめて取る。Editor.log のコンパイルエラー（`error CS`・U#）の集計は `cct_environment` ではなく `cct_world_audit` が行う。
2. ワールドプロジェクトか：VRC Scene Descriptorの型が無い（Avatars SDKだけ）なら、ワールド作業を始めずに伝える。逆に、ワールドプロジェクトにアバター用パッケージが入ってAssembly-CSharpが作られなかった実例がある（泡ワールド、[verified-facts](references/verified-facts.md)）。
3. 版：制作用は Unity 2022.3.22f1 と SDK 3.10.x。Unity 6 へ上げない。版の最新状況・SDK更新の可否は `vrchat-unity-version-check` で確かめる。
4. 対象プラットフォーム：PCだけか、Android（Quest）・iOSにも出すか。後から足すと素材・シェーダー・ライトを作り直すことになるので、最初に決める。
5. 新規か既存ワールドの更新か：更新ならPipelineManagerのBlueprint ID（`wrld_…`）を保つ。全プラットフォームを同じIDに上げる。
6. 何を残し何を変えてよいか、どの段階の確認まで求められているか（エディタ／ClientSim／Build & Test／実機）。

## 作業に応じて読む

| 作業 | 参照 |
| --- | --- |
| 新規プロジェクト、SDKの構成、Scene Descriptor・スポーン・Reference Camera、レイヤーと衝突マトリクス、アップロードが止まる、PC/Android/iOSの出し分け、Community Labs | [セットアップとアップロード](references/setup-and-upload.md) |
| ライトベイク、ライト/反射プローブ、オクルージョン、ミラー・動画・音・ポストプロセスの負荷、サイズ上限、Quest/iOSの制約、アップロード前の点検 | [負荷とプラットフォーム](references/performance-and-platforms.md) |
| UdonSharpを書く・直す、同期方式、オーナー、後から入った人、ネットワークイベント、永続化 | [UdonSharpとネットワーク](references/udon-networking.md) |
| 同梱の8ギミックの設置、Pickup・椅子・ミラー・動画・UI・ObjectPoolの部品 | [ギミックの設置レシピ](references/gimmick-recipes.md) |
| ClientSim、Build & Test、ログの場所、症状から原因を探す表 | [テストとデバッグ](references/testing-and-debugging.md) |
| 版・上限・レイヤー番号などの数値を答える、ユーザーの過去のワールド記録 | [検証済み事実の台帳](references/verified-facts.md) |

参照は必要な工程で読む。このスキルを使うたびに全てを読み込まない。数値は台帳と一次資料から答え、記憶で答えない。

## 作業の流れ

1. **計画**：対象（シーン・オブジェクト）、操作、破壊的か、どの段階で確かめるかを1〜3行で示す。
2. **シーンの土台**：VRCWorld（Scene Descriptor＋PipelineManager）が読み込み中の全シーンでちょうど1つあること、有効なスポーンが1つ以上あること、Respawn Height Y がスポーンより下にあること、Reference Camera が設定されていることを確かめる。レイヤーと衝突マトリクスはControl Panelの「Setup Layers for VRChat」「Setup Collision Matrix」で合わせる（確認ダイアログが出るので本人に押してもらう）。
3. **配置**：床・壁にはプレイヤーと衝突するレイヤーのColliderを付ける。FBXはアセット自身のlocalScaleを確かめ、`アセットのlocalScale × 係数` で拡縮する（上書きで実寸が2倍になった実例）。
4. **見た目と負荷**：動かない物をベイク対象にしてライトマップ、ライトプローブ、反射プローブ、オクルージョンを焼く。ミラーは既定OFF＋トグル、動画プレイヤーは1部屋2台まで。モバイルにも出すなら最初からモバイル向けの材質・テクスチャ・ポリゴン予算で作る。
   ベイクや材質の前後は `cct_capture_views` で同じ視点から撮って比べる。既定の撮り方は対象を複製して中立の照明で撮るので、焼いた光とポストプロセスは写らない。ライティングを比べるときは実際のシーンで撮る設定にする（設定は `claude-creator-tools`）。画像はエディタでの見た目で、VRChat実機の見た目ではない。
5. **ギミック**：同梱サンプルは `cct_world_gimmicks` で、UdonSharpの有無を確かめてから取り込む。Program Asset、Inspectorの配線、UIのSendCustomEventを[レシピ](references/gimmick-recipes.md)どおりに設定する。自作のU#は[同期の決まり](references/udon-networking.md)に沿って書く。
6. **点検**：`cct_world_audit` で対象プラットフォーム（PC/Android/iOS）を含めて点検し、SDKが止める項目（error）を先に潰す。項目と本文の対応は[負荷とプラットフォーム](references/performance-and-platforms.md)の12章。SDKのBuilder画面の検証欄も本人に見てもらう。
7. **テスト**：ClientSim → Build & Test（Clients 2以上）で同期と後から入った人を確かめる。アップロードと実機確認は本人が行い、その結果を観測として受け取る。

## MCPで操作するときの決まり（ワールドで効く要点）

規律の本文と根拠は `unity-mcp-operations` にある。ここでは繰り返さず、ワールド作業で特に効くものだけ挙げる。

- 実行前に計画を示す。配置・材質差し替え・コンポーネント付与などの一括操作は、1件で試してユーザーが目視してから残りに適用する。同種の操作が約10件を超えるならUnity側で1回にまとめる。
- MCP経由のGameObject削除はUndoに載らない。削除・上書き・インポート・ベイクのやり直し・シーン保存の前に `cct_snapshot` で退避する。変更する外部アセット（Material・Mesh・Lightmap等）も対象に含める。
- 購入アセットや既存のギミックを消す・置き換える前は、`cct_dependency_report` でそれを参照している物と依存パッケージを確かめる。
- モーダルダイアログを出さない。SDKのBuild & Publish（著作権同意・サムネイル選択）、Setup Layersの確認、Build Aborted、ファイル選択、保存確認はブリッジを `no_unity_session` にする（泡ワールドでSelect thumbnailが止めた実例。他のダイアログも同じモーダルなので同様と推定）。これらは本人に押してもらう。
- ライトベイク・オクルージョンベイク・SDKビルドのような長い処理を、1回のMCP呼び出しで同期的に実行しない。既定30秒でタイムアウトしても処理は続くので、再送すると二重に実行される恐れがある。
- 書いたら読み戻す（activeSelf、Transform、レイヤー、コンポーネントの値）。`GameObject.Find` は非アクティブを見つけない。復元コードが成功を装った実例がある。
- コンパイルの真実はEditor.logで確かめる（`error CS` と UdonSharpのエラー）。read_consoleが0件でもU#のコンパイルが全体で止まっていた実例がある。
- write系が失敗・タイムアウト・`success:false, message:null` を返したら、再送する前にreadで実状態を確かめる。
- `cct_*` の引数・出力の読み方・呼び出し方（単独toolとして見えるか）は `claude-creator-tools` を読む。このスキルでは名前と目的、`cct_world_audit` の項目コードとこのスキルの章の対応（目安）だけを書く。

## 証拠の段階を分ける

| 段階 | 分かること | 分からないこと |
| --- | --- | --- |
| Unityエディタの静的確認（Inspector、`cct_world_audit`、SDKの検証欄） | 構成、SDKがビルド・アップロードを止める条件、公式推奨からの外れ | 実行時の動作、同期、実機の性能・見た目 |
| ClientSim（Play） | ローカルのInteract・UI・Pickup・トリガー・Udonの例外 | リモートプレイヤーへの同期、AVPro動画、実機の見た目と性能 |
| Build & Test（ローカルのVRChat、Clients 2以上） | 同期、オーナー移譲、後から入った人、output_logのUdonエラー | アップロード後のサーバー挙動、他の人の環境、別プラットフォーム |
| アップロード後のVRChat（PC/Android/iOS別） | 公開した版の実際の挙動 | 確認していないプラットフォームと人数 |

## 関係する専門スキル

- Unityを MCP で操作する工程では `unity-mcp-operations` を併読する（接続確認、破壊的操作の前の確認、1件試行、失敗後の再送禁止）。
- `cct_environment`・`cct_world_audit`・`cct_world_gimmicks`・`cct_snapshot`・`cct_capture_views`・`cct_dependency_report` の使い方は `claude-creator-tools`。
- 対応Unity版・SDK更新・Unity 6の状況は `vrchat-unity-version-check`（SDKの恒久的な事実の還流先もこちら）。
- アバター側の作業（軽量化・アバターのQuest対応は `vrchat-avatar-optimization`、表情・ワールド固定ギミックは `vrchat-expression-debugging`、手持ち小物は `vrchat-hand-prop-setup`、アバター用ギミックの書き出しは `vrchat-gimmick-packaging`）。アバターのワールド固定はワールド制作ではない。
- どのスキルを使うか迷う依頼は `avatar-world-skill-guide`。
- PCのローカルスキル `world-udon-gimmick`・`world-lighting-bake` は目的が重なる可能性がある（中身は未確認）。両方が発火しそうなら、どちらを使うか本人に確かめる。

## 完了の判断

元の依頼・症状の条件で、求められた段階まで確かめたことを完了とする。静的点検に通ったことはビルド成功ではない。ClientSimで動いたことは同期の確認ではない。Build & Testで動いたことは公開後の確認ではない。

報告は「変更」「確認した段階と環境（エディタ／ClientSim／Build & Testのクライアント数／実機のプラットフォーム）」「観測した結果」「未確認・残る制約」に分ける。「完成」「完璧」と書かない。

片付けは作った物だけにする（テスト用オブジェクト、撮影画像、一時シーン、不要になったスナップショット）。シーンの保存は区切りごとに提案する。

新しく判明した仕様・失敗は [verified-facts](references/verified-facts.md) に「事実 / 出典 / 検証日」で追記し、ステータス行を更新する。SDKの恒久的な事実は `vrchat-unity-version-check` へ還流する。
