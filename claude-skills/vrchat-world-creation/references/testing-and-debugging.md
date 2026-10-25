# テストとデバッグ

出典と時点：**SDK 3.10.5 と VRChat 公式クリエイタードキュメントの 2026-10-20 時点のスナップショット**（`docs/…`・`releases/…` は GitHub `vrchat-community/creator-docs` の `Docs/` 以下、`Packages/…` は SDK パッケージ内）、ユーザーのスキル `unity-mcp-operations` の verified-facts（泡ワールド、2026-08-09）、同梱ギミックの README。症状の表で「推定」「Unity 一般」「コミュニティ」と書いたものは一次資料で確かめていない。

## 目次

1. 確認の段階
2. ClientSim
3. Build & Test（PC）
4. モバイルの Build & Test
5. ワールドデバッグビュー
6. ログ
7. 症状から原因を探す
8. 報告の形

---

## 1. 確認の段階

| 段階 | 手段 | これで言えること |
| --- | --- | --- |
| 静的 | Inspector の読み戻し、`cct_world_audit`、SDK Builder の検証欄、Editor.log | 構成と SDK が止める条件 |
| エディタ実行 | ClientSim（Play） | ローカルプレイヤーの操作、Udon の例外、UI・Pickup・トリガー |
| ローカルクライアント | Build & Test（Clients 1）、Force Non-VR の有無 | VRChat 上の見た目と Udon（ローカル） |
| 複数クライアント | Build & Test（Clients 2 以上）、Build & Reload | 同期、オーナー移譲、後から入った人 |
| 公開後 | アップロード後の VRChat（PC / Android / iOS 別、VR とデスクトップ別） | 公開版の実際の挙動 |

上の段階の合格で下の段階を合格にしない。ClientSim の確認が終わったら、同期が絡むものは必ず Build & Test の複数クライアントで確かめる。

---

## 2. ClientSim（`docs/worlds/clientsim/index.md`）

- Worlds SDK 同梱のエディタ内シミュレーター。Play で起動し、マウス・キーボード・ゲームパッドで操作する。Udon 変数の確認、Pickup・Interact・UI・Station が使える。Play に入ると EditorOnly のオブジェクトを消す（L11-17）。**公開前に必ず VRChat で確かめる**（L19）。
- 窓：Settings、Play 中に **Esc** でゲーム内メニュー（リスポーン、リモートプレイヤーの追加など）、PlayerData / PlayerObject のデバッグウィンドウ（L21-28）。3.10.5 のメニューは **`VRChat SDK > ClientSim`**・`ClientSim PlayerData`・`ClientSim PlayerObjects`（`Packages/com.vrchat.worlds/Integrations/ClientSim/Editor/Windows/ClientSimSettingsWindow.cs` L64 など）。docs の「`VRChat SDK/Utilities/ClientSim`」（`clientsim/systems/editor/settings-window.md` L4）は古い。
- 設定：Enable ClientSim（アップロード時は強制的に無効）、Console Logging、Remove "EditorOnly"、Target FrameRate、Startup Delay、Spawn Player Controller、Show Desktop Reticle、Show Tooltips、Invert Mouse Look、Player Height（0.2〜80）、Local Player Name、Local Player Is Master、Is Instance Owner、Hide Menu on Launch（`docs/worlds/clientsim/systems/runtime/settings.md`）。
- プロジェクトの要件：旧 Input と新 Input System の両方を有効、Oculus Audio Spatializer、VRChat のレイヤーと衝突マトリクス。満たしていないと Settings ウィンドウに直すボタンが出る（`docs/worlds/clientsim/systems/editor/settings-window.md`）。
- **できないこと**：リモートプレイヤーの同期（デシリアライズはローカルだけ）、`Others` 宛てのイベント（何もしない）、AVPro の再生、Camera Dolly、`VRCCameraSettings.PhotoCamera`（null）。`OnPostSerialization` / `OnDeserialization` の値は実機と違う（`clientsim/index.md` L38-64）。
- キー割り当て（キーボード・マウス。docs では画像だけなので、SDK の `Packages/com.vrchat.worlds/Integrations/ClientSim/Runtime/Input/ClientSimInputMapping.inputactions` から読んだ。実際に押しての確認はしていない）：WASD で移動、左 Shift で走る、Space でジャンプ、C でしゃがむ、Z で伏せる、Tab でマウスの固定を外す、左クリックで掴む・使う（Interact）、右クリックで離す、ホイールで持った物を前後、I/K・J/L・U/O で持った物を回転、Esc でメニュー。
- MCP からの使い方：`manage_editor` で Play / Stop し、`read_console` と読み戻しで結果を見る。Play 中の変更は保存されない（`unity-mcp-operations` 原則2）。Play 中に `UdonBehaviour.Interact()` をリフレクションで呼んで Interact の機能を確かめた記録がある（泡ワールド）。
- 3.8.2 以降、Scene Descriptor が無くても Play に入れる（`releases/release-3.8.2.md` L25）。Play できたことはシーンの構成が正しい証拠ではない。

---

## 3. Build & Test（PC）

`docs/worlds/udon/using-build-test.md`、`docs/worlds/creating-your-first-world.md` L115-128。

1. Control Panel の Settings タブで VRChat Client（VRChat.exe）のパスを設定する。既定の例：Steam `C:\Program Files (x86)\Steam\steamapps\common\VRChat\VRChat.exe`（L25-31）。
2. Builder でレイヤーと衝突マトリクスを設定済みにする。
3. Build Type を「Build & Test Your World」にし、**Clients**（起動するクライアント数）を決める。同期とネットワークイベントの確認には **2 以上**（L49-50）。SDK の UI では 0〜8 にクランプ（`WorldBuilder` L2509）。0 にするとクライアントを起動しない（開いているクライアントがあれば Build & Reload で新しい版へ移る。L52-57）。最初に読み込んだクライアントがマスター。
4. **Force Non-VR**：VR 機器があってもデスクトップで起動。**Enable World Reload**：ビルドのたびに起動中のクライアントを新しい版へ自動で移す。
5. 自分で起動したクライアントも `--watch-worlds` を付ければ Build & Reload の対象になる。例：`VRChat.exe --watch-worlds --profile=0 --no-vr --enable-debug-gui --enable-sdk-log-levels --enable-udon-debug-logging -screen-width 1920 -screen-height 1080`（L59-62）。
6. Build & Reload した後に Build & Test でクライアントを足すと、正しい部屋に入らないことがある。Clients を 0 に戻して Build & Reload か Reload Last Build で揃え直す（L64-66）。

- ローカルテスト（Build & Test / Reload / Test Last / Reload Last）はアクティブターゲットが Windows64 / Android / iOS のときだけ（`WorldBuilder` L2551-2556）。
- Build & Test はトラストランク不要（3.9.0 以降。`releases/release-3.9.0.md` L30-32）。ビルド自体の検証（[セットアップ](setup-and-upload.md) 4章の A〜D）は Publish と同じ。
- Persistence：Clients 2 以上ではクライアントごとに別保存。Build & Test の保存はクライアントを閉じると消える（`docs/worlds/udon/persistence/index.md` L26-39）。
- Build & Test は本人が押すのを既定にする（長時間・失敗時のダイアログ）。押した後、Claude は Editor.log と output_log を読んで結果をまとめる。

### 3.1 複数クライアントでの確認手順（同期ギミック）

1. A で操作 → B に反映。B で操作 → A に反映。
2. A で状態を変えた後、B を入り直す（Build & Reload か、B だけ閉じて入り直す）→ B が A と同じ状態で始まる（後から入った人）。
3. 最後に操作した人（オーナー）のクライアントを閉じる → 残った方で状態が保たれ、操作できる（オーナー退出）。
4. ローカル専用のギミックは、片方で操作してももう片方が変わらないこと。

---

## 4. モバイルの Build & Test

`docs/platforms/android/build-test-mobile.md` L13-162、`docs/platforms/iOS/build-test-mobile.md` L33-94。

- Android（3.7.2〜）：ADB（USB デバッグ・開発者モード）。初回は端末で VRChat アプリを一度起動して閉じておく。端末のロックを外しておく。既定のパッケージ名は `com.vrchat.mobile.playstore`。Wi-Fi の ADB も可。
- Quest（3.7.5〜）：Meta Quest Developer Hub で開発者モードにする。
- iOS（3.8.1〜）：VRChat Test Protocol（VTP、ポート 9002）。iOS の設定の「SDK IP Address」に PC のローカル IP を入れる。Unity の Console に `[VTP] Listening on port 9002...` / `[VTP] Client connected.` が出る。
- プラットフォームの切り替えは再インポートで時間がかかる。本人の操作で行う。

---

## 5. ワールドデバッグビュー（`docs/worlds/udon/world-debug-views.md`）

- 開き方：Quick Menu の Settings ページ下部の「Toggle Debug UI」、またはショートカット **`Right Shift` + `~` + 数字**。`~` は「1 の左のキー」の位置で判定するので、日本語配列でも 1 の左のキーを使う（L5-15）。
- 起動時に `--enable-debug-gui` を付けると、ゲーム内でログを見られる（`docs/worlds/udon/debugging-udon-projects.md` L17-19）。

| 番号 | ページ・オーバーレイ | 使いどころ |
| --- | --- | --- |
| 1 | AssetBundle and Memory | 読み込み中のアセット。Force GC は押さない（性能を悪化させ得る） |
| 2 | Version & Info | 版とホットキー。1回目は簡易 HUD |
| 3 | Log Viewer | output_log。Udon の停止、`Debug.Log`、エラー |
| 4 | Players | プレイヤー一覧 |
| 6 | Net Objects（制限付き） | ネットワークオブジェクトのオーナー・送信量 |
| — | Audio Sources（制限付き） | 音源の状態 |
| 7 | PhysBone & Contact オーバーレイ | |
| 8 | Network Object Info オーバーレイ | オブジェクトごとの同期状態 |
| 9 | Player Info オーバーレイ | |
| 0 | VRC_UiShape オーバーレイ | UI の当たり判定 |

**制限付き**（6、Audio Sources、オーバーレイ）は、ワールドの作者か、VRChat.com のワールド設定で **World Debugging** を有効にした場合だけ見られる。設定を変えたら入り直す（L17-21, L131）。Builder の Content Info の World Debugging はタグ `debug_allowed` を付ける。

---

## 6. ログ

### 6.1 Unity 側

- **Editor.log**：コンパイルの実状態は Editor.log の `error CS` を集計して確かめる。read_console がエラー0件を返しても Assembly-CSharp が生成されていなかった実例がある（泡ワールド）。U# Assembly Definition の欠落による U# コンパイルの中断も Editor.log にだけ出た（"does not belong to a U# assembly"）。`cct_world_audit` はこの集計を結果に含める（`cct_environment` が返すのは「コンパイル中か」だけで、ログのエラーは数えない）。場所はエディタ内なら `UnityEngine.Application.consoleLogPath` で読める（`cct_world_audit` もこれを使う）。Windows での既定の場所は `%LOCALAPPDATA%\Unity\Editor\Editor.log`（Unity 一般。このスキルの一次資料では未確認）。
- ビルドのサイズはビルド後の Editor.log の「statistics」（`docs/platforms/android/quest-content-optimization.md` L36）。
- UdonSharp は VRChat の output_log から Udon の例外を拾い、U# の何行目で起きたかを Unity の Console に出す（Project Settings の Listen for Client Exceptions、既定 ON。`docs/worlds/udon/debugging-udon-projects.md` L132-141）。3.10.2 以降は `VRChat SDK/Udon Sharp/Parse Logs from File` でログファイルから解析できる（`releases/release-3.10.2.md` L17-19）。

### 6.2 VRChat 側（`docs/worlds/udon/debugging-udon-projects.md`）

- 場所（Windows）：**`C:\Users\<ユーザー名>\AppData\LocalLow\VRChat\VRChat`** に、起動ごとに `output_log_08-55-48.txt` のような時刻付きのファイルができる（L23-31）。
- 既定のログには Udon の詳細が出ない。Build & Test は詳細ログ付きで起動する。自分で起動するなら `VRChat.exe --no-vr --enable-debug-gui --enable-sdk-log-levels --enable-udon-debug-logging`（バッチファイル、VCC の VRC Quick Launcher、Steam の起動オプションのどれでも。L33-63）。デバッグを常時 ON にすると重くなりログも大きくなる（L65-69）。
- Udon が例外で止まると `[UdonBehaviour] An exception occurred during Udon execution, this UdonBehaviour will be halted.` が出る。ログを **"halted"** で検索し、2行目の `EXTERN to '…'` と例外の種類を読む（L102-123）。止まった UdonBehaviour はそのセッションでは動かない。
- 調べたい箇所の前後に `Debug.Log` で固有の文字列を出す（L71-100, L125-130）。
- クラウドのセッションからは PC のログファイルを読めない。本人に該当部分を貼ってもらうか、ローカルの Claude Code で読む。

---

## 7. 症状から原因を探す

各表は「確かめる順」に並べた。原因が分かったら最小の変更で直し、同じ段階で再確認する。

### 7.1 何も同期しない・他の人に反映されない

| 確かめること | 原因の例 | 根拠 |
| --- | --- | --- |
| ClientSim だけで判断していないか | ClientSim はリモートへの同期を再現しない | `docs/worlds/clientsim/index.md` L38-44 |
| 値を書いたのはオーナーか | オーナーでない側の書き換えは送られない | `docs/worlds/udon/networking/variables.md` L33-35 |
| Manual なら `RequestSerialization()` を呼んだか | 呼ばないと送られない | `network-components.md` |
| 同期方式が None / NoVariableSync になっていないか | 同期変数が送られない | `docs/worlds/udon/udonsharp/attributes.md` |
| 同期配列を初期化したか | 未初期化の配列が1つでもあると全体が同期されない | `network-details.md` L95-98 |
| Continuous で 200 バイトを超えていないか、IsClogged | 上限超えで送れない | `network-details.md` L14-21 |
| 同期する GameObject が非アクティブでないか | 非アクティブだと受け取れない（同梱 `CctGlobalToggle` の注意） | 同梱 README |
| ObjectSync と Manual の UdonBehaviour が同居していないか | 干渉し、ビルドも止まる | SDK の検証 C7 |
| 状態をイベントで扱っていないか | 後から入った人に届かない | `late-joiners.md` |
| 新旧のワールドバージョンが混在していないか、同期の構成を変えたか | イベントが捨てられる／互換性が無くなる | `events.md` L273-275、`compatibility.md` L8-14 |

### 7.2 Interact が反応しない

| 確かめること | 原因の例 | 根拠 |
| --- | --- | --- |
| 同じ GameObject に Collider と UdonBehaviour があるか | どちらかが無い | `event-nodes.md` L15-19 |
| U# のコンパイルと Program Asset | Program Asset が無い・コンパイルエラーで裏の UdonBehaviour にプログラムが無い | U# のエラー表示、`cct_world_audit` の `UDON_NO_PROGRAM` 等 |
| `Interact` を正しく override しているか、`DisableInteractive` が true でないか | | [UdonSharpとネットワーク](udon-networking.md) 12章 |
| 手前に Interact のレイを遮る Collider が無いか、ユーザーレイヤーに置いていないか | 透過する層は UiMenu・UI・PlayerLocal・MirrorReflection だけ。ユーザー層は Interact Passthrough に入れないと遮る | `docs/worlds/layers.md` L87-97 |
| Proximity（既定 2）より遠くないか | Desktop は頭から、VR は手からの距離 | `docs/worlds/components/vrc_pickup.md` L69-94 |
| GameObject・コンポーネントが有効か、スイッチ自身を非表示にしていないか | | 同梱 README |

### 7.3 UI が反応しない

| 確かめること | 原因の例 | 根拠 |
| --- | --- | --- |
| Canvas に VRCUiShape があるか | 無いと VRChat のポインタが当たらない | `docs/worlds/components/vrc_uishape.md` |
| Canvas の層が UI（5）か UiMenu（12）のままでないか | メニューを開いている時しか反応しない | 同 L27-28 |
| World Space か、EventSystem と GraphicRaycaster があるか | | 同 L45-57 |
| 透明な要素がボタンを覆っていないか、Raycast Target、Canvas の Z+ の向き | | 同 |
| On Click の呼び先が UdonBehaviour の `SendCustomEvent` で、メソッド名が正確か（大文字小文字、`_` の有無） | 打ち間違い、引数付き・private のメソッド | [UdonSharpとネットワーク](udon-networking.md) 2.1 |
| UnityEvent の呼び先が許可されたものか | 許可リスト外の呼び出しは Play に入ったとき（エディタ）と VRChat 内で削除される。UdonBehaviour に呼べるのは `SendCustomEvent`・`RunProgram`・`Interact` と `enabled` だけ | `Packages/com.vrchat.worlds/Runtime/VRCSDK/SDK3/UnityEventFilter.cs` L108-111, L644-657、`docs/worlds/components/vrc_uishape.md` L61、`docs/worlds/udon/ui-events.md` |
| オーバーレイ 0（VRC_UiShape）で当たり判定を見る | BoxCollider の大きさが合っていない | `world-debug-views.md` |

### 7.4 Pickup が床を抜ける・持てない

| 確かめること | 原因の例 | 根拠 |
| --- | --- | --- |
| Pickup に Rigidbody と Collider があるか | Collider が無いと持てない | `vrc_pickup.md` L15 |
| Pickup の層と床の層が衝突するか | 床が MirrorReflection・UI・Item・InternalUI・HardwareObjects・reserved4 だと Pickup（13）は抜ける。PickupNoEnvironment（14）は Pickup とだけ衝突 | 衝突マトリクス（[セットアップ](setup-and-upload.md) 3.2） |
| MCP で付けた Pickup の層 | スクリプトからの AddComponent で Pickup 層に自動で移るかは未確認。読み戻して確かめる | [レシピ](gimmick-recipes.md) 5.1 |
| 動く Rigidbody に凹型の Mesh Collider を付けていないか | Unity では非対応 | `cct_world_audit` の `MESH_COLLIDER_NONCONVEX_RIGIDBODY`（推定） |
| 速く投げて薄い床を抜けていないか | 衝突検出のすり抜け | Unity 一般（推定） |
| Respawn Height Y より下に落ちていないか | Descriptor の設定でリスポーンか破棄 | `vrc_scenedescriptor.md` |
| 他の人と位置がずれる | VRCObjectSync が無い | `vrc_pickup.md` L17 |

### 7.5 ミラーが黒い・映らない・重い

| 確かめること | 原因の例 | 根拠 |
| --- | --- | --- |
| 同じ GameObject に Renderer とマテリアルがあるか | 無いとミラーは自分を無効化する | SDK の `VRC_MirrorReflection` |
| Camera Clear Flags が Custom Skybox で Skybox 未指定でないか | 背景が黒になる | `vrc_mirrorreflection.md` |
| Custom Shader が `_ReflectionTex0/1` を読むか | 読まないと反射が出ない | SDK 3.10.5 の実装（docs の `_MainTex` は古い） |
| Reflect Layers に映したい層があるか | Water と PlayerLocal は常に映らない。自分を映すには MirrorReflection（18） | 同上、[負荷](performance-and-platforms.md) 4章 |
| ミラー本体の層がカメラに描かれる層か | 描かれない層だと処理が飛ばされる | SDK コードからの推定 |
| 物がちらつく | Turn Off Mirror Occlusion を ON | `vrc_mirrorreflection.md` L15 |
| 重い | 既定 ON、2枚以上が同時に ON、Everything を映している、解像度 | 同 L24-27 |

### 7.6 Quest 版が無い・ピンク・黒い

| 確かめること | 原因の例 | 根拠 |
| --- | --- | --- |
| Android / iOS 版をアップロードしたか、同じ Blueprint ID か | 自分のプラットフォームの版が無いとポータルや UI から入れない | `docs/platforms/android/cross-platform-setup.md` L58-64, L87 |
| iOS 版が無いだけか | Android 版で代用する実験的フォールバックはあるが、iOS 版の上げ直しが推奨 | 同 L77-83 |
| 100MB を超えていないか、ASTC か | 超えると Publish 拒否、ASTC でないと Android でビルドできない | SDK の検証 E3・C2 |
| iOS 非対応アカウントでないか | iOS ターゲットでビルドできない | SDK の検証 C3 |
| ピンク：シェーダーが見つからない／そのプラットフォームでコンパイルできない | エラーシェーダー（Hidden/InternalErrorShader） | `cct_world_audit` の `MISSING_SHADER`（推定）、Unity 一般 |
| 黒：Android で使えない圧縮形式のテクスチャ | | コミュニティ（VRWorld Toolkit） |
| 表示崩れ：GrabPass を使うシェーダー | Android で崩れる | コミュニティ |
| ポストプロセスが効かない | Android では完全に無効 | `quest-content-limitations.md` L52 |
| 暗い | リアルタイムライト頼み。ベイクしていない、ライトプローブが無い | `quest-content-optimization.md` L110-114 |

### 7.7 アップロード（ビルド）が止まる

まず SDK Builder の検証欄の Error、Console、Editor.log の順に全文を読む。条件の一覧は [セットアップ](setup-and-upload.md) 4章。よくあるもの：

| 症状・メッセージ | 原因 |
| --- | --- |
| Builder が無効、"Multiple Pipeline Managers…" | PipelineManager が2つ以上（複数シーンを開いている場合も） |
| "A VRCSceneDescriptor is required…" / "Scene needs exactly one VRC_SceneDescriptor" | Descriptor が0か2つ以上（読み込み中の全シーン） |
| "You must address Layers and Collision Matrix issues…" | Setup Layers / Setup Collision Matrix 未実施、独自レイヤーが 3〜21 にある |
| "Failed to compile UdonSharp scripts for build…" / "Scripts are in the process of compiling…" | U# のコンパイルエラー、コンパイル中 |
| "Failed to assign network IDs…" | Network ID の割り当てエラー。Network ID Utility で解消 |
| "Object Sync cannot share an object…" | ObjectSync と Manual の UdonBehaviour / ObjectPool の同居 |
| "Default texture format on Android should be set to … ASTC" | Android ターゲットで ASTC でない（Fix ボタン） |
| "World download size is too large for the target platform" | モバイルで 100MB 超 |
| "Please set a name and thumbnail before uploading" | 新規ワールドに名前・サムネイルが無い |
| "You must agree to the ownership agreement to upload" | 著作権同意モーダルに同意していない |
| "Current User does not have permissions…" | Trust Rank が New User 未満 |
| "World's current ID belongs to a different user…" | 他人の Blueprint ID が付いている |
| ビルド中に稀に "All pipe instances are busy" | Unity 側の既知の問題。Unity の再起動で解決（`releases/release-3.10.5.md` L89-91） |
| Lighting の Auto Generate が ON | ビルドが止まる恐れ（SDK Warning） |
| アップロードできたのに実機でコンポーネントが消える | 許可リスト外のコンポーネント（SDK は検査しない） |

Console の Error Pause が ON だとアップロードが失敗する原因になる、という指摘がある（コミュニティ、VRWorld Toolkit）。

### 7.8 U# がコンパイルされない・Add Component に出ない

| 症状 | 対処（同梱 README「トラブルシューティング」ほか） |
| --- | --- |
| "Unable to find valid U# program asset associated with script…" / "U# behaviour is not pointing to a valid U# program asset" | Program Asset が無い。[レシピ](gimmick-recipes.md) 2章の手順で作る |
| "Script X is referenced by 2 UdonSharpProgramAssets" | Program Asset が複数。片方の .asset を削除する（削除前に `cct_snapshot`） |
| Add Component に出てこない | C# のコンパイルエラー（Console と Editor.log） |
| CS0101「… already contains a definition for 'Cct…'」 | 同じサンプルを2か所に取り込んだ。片方のフォルダを削除する |
| "Script … does not belong to a U# assembly" | asmdef のあるフォルダに入れた。asmdef の外へ移すか U# Assembly Definition を作る。**この状態はプロジェクト全体の U# コンパイルを止める**（泡ワールドでは Console に出ず Editor.log だけ） |
| アバタープロジェクトでコンパイルエラー | ワールド専用のサンプルを入れた。取り込んだフォルダを削除する |
| ワールドプロジェクトで Assembly-CSharp が作られない | アバター用パッケージの API 不整合（泡ワールドの実例）。VCC で不整合のパッケージを外す |

---

## 8. 報告の形

- 「変更」「確認した段階と環境（エディタ／ClientSim／Build & Test のクライアント数・VR かデスクトップか／実機のプラットフォーム）」「観測した結果」「未確認・残る制約」に分ける。
- 観測・再現・仮説・検証済みの修正・未解決を区別する。ログやメッセージは要点の行を引用し、全文は本人の手元にあることを書く。
- ClientSim や Build & Test の結果を、公開後の結果として書かない。
