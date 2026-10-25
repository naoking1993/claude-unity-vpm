# プロジェクト作成・シーン設定・アップロード

出典と時点：数値と条件は **SDK 3.10.5（`com.vrchat.worlds` / `com.vrchat.base` 3.10.5、2026-09-04）と VRChat 公式クリエイタードキュメントの 2026-10-20 時点のスナップショット** によるもので、それ以降の変更は反映していない。

- `docs/…`・`releases/…` は GitHub `vrchat-community/creator-docs` の `Docs/docs/`・`Docs/releases/` 以下（creators.vrchat.com の原稿）。行番号 `L` はそのスナップショットのもの。
- `Packages/com.vrchat.worlds/…`・`Packages/com.vrchat.base/…` は SDK パッケージ内のファイル（ワールドプロジェクトの `Packages/` で開いて確かめられる）。`WorldBuilder` は `Packages/com.vrchat.worlds/Editor/VRCSDK/SDK3/VRCSdkControlPanelWorldBuilder.cs`、`PanelBuilder` は `Packages/com.vrchat.base/Editor/VRCSDK/Dependencies/VRChat/ControlPanel/VRCSdkControlPanelBuilder.cs` の略。DLL の中身（`VRCSDK3-Editor.dll` 等）は逆アセンブルで読んだもの。
- Unity の一般的な操作で VRChat の資料に書かれていないものは「Unity 一般」と書く。確認できなかったものは「未確認」。

## 目次

1. 版とプロジェクト
2. シーンの土台（Scene Descriptor・スポーン・Reference Camera）
3. レイヤーと衝突マトリクス
4. ビルド・アップロードを止める条件
5. PC / Android / iOS の出し分け
6. Build & Test の要点
7. Content Info と Community Labs
8. Claude が行う範囲と本人に頼む操作

---

## 1. 版とプロジェクト

### 1.1 使う版

- 制作用 Unity は **2022.3.22f1**。これより新しい Unity でアップロードすると読み込めなくなる、と公式が警告している（`docs/sdk/upgrade/current-unity-version.md` L19-25、`Docs/src/components/CurrentUnityVersion2022.js`）。SDK は RemoteConfig の推奨版と `Application.unityVersion` が違うと Warning を出す（ブロックはしない。`PanelBuilder` L538-552）。
- Unity 2022 でアップロードしたワールドは 2019 で再アップロードできない（`docs/sdk/upgrade/unity-2022.md` L39, L105）。
- **Unity 6**：ロードマップ（2026-08-31 更新）で「Unity 6 VRChat SDK」は In progress（`docs/roadmap.md` L8-10, L31）。docs に Unity 6 用の制作手順は無い。ユーザーの `vrchat-unity-version-check` のスナップショット（2026-10-23）では、クライアントは 2026.3.3（2026-10-02）で Unity 6 に移ったが、Unity 6 対応 SDK は未リリースで「Do NOT upgrade your SDK projects to Unity 6」とされている。**制作プロジェクトは 2022.3.22f1 のまま**にし、最新状況はそのスキルで確かめる。
- SDK の安定版は 3.10.5（`releases/release-3.10.5.md`）。2026-10-23 時点で 3.10.6-beta.3 がある（`vrchat-unity-version-check` スナップショット）。ベータを入れるかは本人が決める。

### 1.2 プロジェクトの作り方

- 公式の推奨は **VRChat Creator Companion（VCC）**：Create New Project → World project → 名前と場所 → Create Project → Open Project（`docs/sdk/index.md` L15-50）。既定のインストール先は `%LocalAppData%/Programs/VRChat Creator Companion`（同 L26）。
- 開いたらタイトルバーが `PC, Mac & Linux Standalone <DX11>` で終わるかを見る。違えば `File > Build Settings...` で切り替える（同 L44-45）。
- `VRChat SDK > Show Control Panel > Authentication` でサインインする。アップロードには Trust Rank「New User」以上の VRChat アカウントが要り、Steam / Meta / Viveport のアカウントでは上げられない（同 L50）。**Build & Test はトラストランク不要**（3.9.0 以降、Visitor でもローカルテスト可。`releases/release-3.9.0.md` L30-32）。
- Unity Hub だけで作る場合は 3D テンプレートにし、HDRP/URP を使わない（VRChat は Built-in RP。`docs/sdk/index.md` L57-60）。SDK はビルド時に `m_CustomRenderPipeline` を null に戻す（`Packages/com.vrchat.base/Editor/VRCSDK/Dependencies/VRChat/EnvConfig.cs` L811-816）。
- **ALCOM**（vrc-get の GUI。コミュニティ製の VPM クライアント）でも VPM プロジェクトを扱えるが、操作手順はこの資料の一次資料に無い（未確認）。ALCOM 1.1.7 / vrc-get 1.9.2 以降は Unity 6000.x への VRCSDK 導入を止める（`vrchat-unity-version-check` スナップショット）。
- Android / iOS に出すなら、Unity に「Android Build Support」「iOS Build Support」モジュールを入れる（`docs/sdk/upgrade/current-unity-version.md` L15）。
- 新規プロジェクトの Assets にシーンが無いと、SDK が `Assets/Scenes/VRCDefaultWorldScene.unity` を自動生成する（`Packages/com.vrchat.worlds/Editor/VRCSDK/SDK3/VRCSceneTemplateInitializer.cs` L9-11, L37-46）。中身は EventSystem、Main Camera、Directional Light（**Realtime・Soft Shadow**）、VRCWorld（spawnRadius 0.2、spawnOrder Random、Reference Camera = Main Camera、RespawnHeightY -100）、Floor（`.../SDK3/VRCDefaultWorldScene.unity`）。

### 1.3 パッケージ構成（3.10.5）

| パッケージ | 依存（package.json） |
| --- | --- |
| `com.vrchat.worlds` 3.10.5 | `com.vrchat.base` 3.10.5、`com.unity.cinemachine` 2.9.7、`com.unity.textmeshpro` 3.0.6、`com.unity.inputsystem` 1.2.0、`com.unity.ai.navigation` 1.1.5 |
| `com.vrchat.base` 3.10.5 | `com.unity.postprocessing` 3.2.2、`com.unity.ugui` 1.0.0、burst / collections / mathematics / newtonsoft-json / timeline / xr.management / xr.oculus |

- **UdonSharp と ClientSim は Worlds SDK に同梱**（SDK 3.4.0 から。実体は `Packages/com.vrchat.worlds/Integrations/UdonSharp`・`.../ClientSim`。`releases/release-3.4.0.md` L2-24、`docs/worlds/udon/udonsharp/index.md` L9）。`com.vrchat.udonsharp` / `com.vrchat.clientsim` を別に入れない（worlds の package.json の `legacyPackages` で VCC が削除対象にしている）。
- Post Processing Stack v2 は base の依存として自動で入る。`docs/worlds/supported-assets.md` L17-18 の「Package Manager から import」は 3.10.5 では古い。
- アバター用のパッケージ（NDMF・Modular Avatar・lilToon 系の改変ツールなど）をワールドプロジェクトに入れない。泡ワールドでアバター系5パッケージが API 不整合を起こし、Assembly-CSharp が生成されなかった（ユーザー記録、[台帳](verified-facts.md)）。
- ProBuilder は既定では入らない（泡ワールドで未導入を実測）。入っていないと MCP の `manage_probuilder` は使えない。

### 1.4 SDK がビルド時に強制する設定

`EnvConfig.ConfigurePlayerSettings`（`Packages/com.vrchat.base/Editor/VRCSDK/Dependencies/VRChat/EnvConfig.cs`）がビルドのたびに上書きする。手で変えても戻るので、別の値を前提に作らない。

- Color Space = **Linear**（L1072-1077）、Stereo Rendering = **Single Pass**（L1098）、既定の Rendering Path = **Forward**（L789-845）、graphicsJobs / gpuSkinning / incremental GC を有効（L1085-1098）。
- シェーダーチャンク 4MB（3.10.4 から全プロジェクトに強制。`releases/release-3.10.4.md` L38-39）、Always Included / Preloaded Shaders を空にする（L789-845）。
- API 互換 .NET 4.x、`runInBackground = true`（L232-262）、IL2CPP のコンパイラ設定 Release（L1100-1101）。
- テクスチャ圧縮：アクティブターゲットが Android **以外**のときだけ `androidBuildSubtarget = ASTC` にする（L458-466）。Android ターゲット上で ASTC でなければ Error（4.3 の C2）。Multi-Platform Build で Android を含むときはビルド前に ASTC にする（`WorldBuilder` L3423-3429）。

---

## 2. シーンの土台

### 2.1 VRCWorld と PipelineManager

- VRCWorld プレハブの実体は `Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VRCWorld.prefab`（3.10.5。Builder もこのパスを使う、`WorldBuilder` L1254）。docs の別パスの記述（`docs/worlds/sdk-prefabs.md` L32-35 など）は古い。
- 中身：`VRCSceneDescriptor` ＋ `PipelineManager` ＋ UdonBehaviour 3つ。`VRCWorldSettings`（移動速度）と `AvatarScalingSettings` は **Udon Graph** のプログラム（`Samples/UdonExampleScene/UdonProgramSources/*.asset`、型は `UdonGraphProgramAsset`）、`WorldQualitySettings` だけが U#（`Samples/VRCQualitySettings/Scripts/WorldQualitySettings.cs`）。プレハブの値は spawnRadius 0、Reference Camera 未設定（`VRCWorld.prefab` L18-71, L150-261）。
- Descriptor が無いと Builder に「Add a VRCSceneDescriptor」ボタンが出る。押すと VRCWorld が**プレハブリンク無しのコピー**として置かれる（`WorldBuilder` L1217-1263）。
- `PipelineManager` は Descriptor と同じ GameObject に置く。Descriptor の Inspector を表示している間に無ければ自動で足される（`.../SDK3/Components3/VRCSceneDescriptorEditor3.cs` L72-73, L126-140）。Blueprint ID は `wrld_` で始まる（`docs/sdk/vrcpipelinemanager.md` L19-27）。空のままアップロードすると新しい ID が払い出される（`WorldBuilder` L3370-3374）。**既存ワールドの更新では ID を変えない。** PC/Android/iOS は同じ ID に上げる（`docs/platforms/android/cross-platform-setup.md` L64）。

### 2.2 Scene Descriptor の項目（Inspector の表示順）

| 項目（フィールド名） | 既定 | 意味・注意 |
| --- | --- | --- |
| Spawns（`spawns`） | VRCWorld 自身 | スポーン地点の Transform の配列。空なら Inspector が自身を足して警告を出す。null の要素は無視される |
| Spawn Radius（`spawnRadius`） | 0（新規シーンは 0.2） | 3.8.2 で追加。スポーンを中心にこの半径でばらす。0 だと同時に来た人が重なる（`docs/worlds/creating-your-first-world.md` L52） |
| Spawn Order（`spawnOrder`） | Random | First / Sequential / Random / Demo（Demo はルームスケールの中心をスポーンに合わせる。`docs/worlds/components/vrc_scenedescriptor.md` L13-17） |
| Spawn Orientation（`spawnOrientation`） | Default | Default（現在は AlignPlayerWithSpawnPoint）/ AlignPlayerWithSpawnPoint / AlignRoomWithSpawnPoint |
| Reference Camera（`ReferenceCamera`） | 未設定（新規シーンは Main Camera） | このカメラの設定（クリップ面・ポストプロセス等）がプレイヤーに適用される。実行時このカメラ自体は無効化される |
| Respawn Height Y（`RespawnHeightY`） | -100 | これより下に落ちたプレイヤーはリスポーン、Pickup はリスポーンか破棄 |
| Forbid User Portals（`ForbidUserPortals`） | false | ワールド内でのユーザーポータル作成を禁止 |
| Object Behaviour At Respawn Height（`ObjectBehaviourAtRespawnHeight`） | Respawn | Respawn / Destroy |
| Network IDs | 空 | Network ID の一覧（Network ID Utility で管理） |
| Interact Passthrough（`interactThruLayers`） | なし | ユーザーレイヤー 22〜31 のうち、Interact のレイを透過させる層。ビット i が層 22+i（ClientSim の実装 `ClientSimInteractiveLayerProvider.cs` L22, L38 で確認。クライアント本体の解釈は未確認） |

- ビルド時に上書きされる隠し項目：`gravity` は Project Settings > Physics の Gravity、`layerCollisionArr` は現在の 32×32 衝突マトリクス、`unityVersion` は `Application.unityVersion` で上書きされる（`VRCSDK3-Editor.dll` の `VRCWorldAssetExporter`）。重力・衝突は Project Settings 側で決める。
- docs の表にある「Forbid Free Modification」「User Custom Voice Falloff Range」は 3.10.5 の Descriptor にフィールドが無い（docs が古い）。
- Scene ビューには、リスポーン高さの面（緑、ドラッグで高さを変えられる）と各スポーンの向きがギズモで描かれる（3.7.0 から。`releases/release-3.7.0.md` L43-44）。Spawn Radius が 0 より大きいと半径の円、0 だと×印になる（半径は 3.8.2 から。`.../SDK3/Components3/VRCSceneDescriptorEditor3.cs` L82-124）。

### 2.3 スポーン

- 有効なスポーンが1つ以上必要（無いとビルド不可。4.3 の C9）。
- スポーン位置の x・y・z の絶対値のどれかが 1000 以上だと Warning（メニューと PhysBone に問題が出る。`WorldBuilder` L56-66, L939-1000）。スポーンの y が Respawn Height Y より下だと Warning（3.10.0 で追加、`releases/release-3.10.0.md` L34）。Spawn Order が First / Demo のときは先頭のスポーンだけが検査される。
- スポーンの真下に、プレイヤーと衝突するレイヤーの Collider（Is Trigger OFF）を置く。床が Walkthrough（17）などプレイヤーと当たらない層だと落ち続ける。Labs の審査でも「ポータルから落ち続けないか」を見られる（`docs/worlds/submitting-a-world-to-be-made-public.md` L48-54）。

### 2.4 Reference Camera

- 近・遠クリップ面とポストプロセスはここで決める（`docs/worlds/creating-your-first-world.md`）。Near は **0.001〜0.05** にクランプされ、Far は Near+0.1 以上（Udon の `VRCCameraSettings` と同じ制約、`docs/worlds/udon/vrc-graphics/vrc-camera-settings.md` L61-65）。
- ポストプロセスを使うなら Reference Camera に `PostProcessLayer` を付ける。TAA（Temporal Anti-aliasing）は非対応で Warning（Fix で None。`WorldBuilder` L659-674）。
- MainCamera タグのカメラの子に `PostProcessVolume` を置くと警告するコードがあるが、3.10.5 では後続の `ResetIssues()` で消され通常は表示されない（`WorldBuilder` L211-221, L250）。Volume はカメラと別の GameObject に置く。
- Reference Camera 以外のカメラを有効なまま画面に描かせない（コミュニティ製 VRWorld Toolkit の知見：Display 1・depth 0 超のカメラがアップロード画面の上に描かれる）。

### 2.5 VRCWorld に付いている設定スクリプトの既定値

- VRCWorldSettings（Udon Graph）：Walk 2、Run 4、Strafe 2、Jump 3。VRCWorld プレハブの公開変数はこの4つだけで、Graph も `SetWalkSpeed` / `SetRunSpeed` / `SetStrafeSpeed` / `SetJumpImpulse` しか呼ばない（`VRCWorld.prefab` の公開変数と `UdonProgramSources/VRCWorldSettings.asset` の照合）。docs の表（`docs/worlds/examples/udon-example-scene/player-mod-setter.md` L13-20）にある Gravity 1・Use Legacy Locomotion OFF は U# 版 `PlayerModSetter`（`Packages/com.vrchat.worlds/Integrations/UdonSharp/Samples~/Utilities/PlayerModSetter.cs` L12-19）の項目で、VRCWorld の Graph 版には無い。重力の強さを変えるなら `PlayerModSetter` を使うか自分で `SetGravityStrength` を呼ぶ。
- AvatarScalingSettings：拡縮を許可、最小 0.2m・最大 5m（`.../avatar-scaling-settings.md` L18-23）。
- WorldQualitySettings：Shadowmask、影の距離 High 150 / Medium 75 / Low 75 / Mobile 50、リアルタイム反射プローブ PC 有効・モバイル無効（上書きは既定 OFF。`Packages/com.vrchat.worlds/Samples/VRCQualitySettings/Scripts/WorldQualitySettings.cs` L9-37）。

---

## 3. レイヤーと衝突マトリクス

### 3.1 レイヤー表（`docs/worlds/layers.md` L17-44、実名は `Packages/com.vrchat.base/Runtime/VRCSDK/Dependencies/VRChat/Resources/VRCProjectSettings.prefab`）

| # | 名前 | 用途・注意 | ステッカー |
| --- | --- | --- | --- |
| 0 | Default | 通常の物 | 可 |
| 1 | TransparentFX | Flare、PhysBone/Contact の表示 | 不可 |
| 2 | Ignore Raycast | Unity の LayerMask 無し Raycast では無視されるが、**VRChat のレイでは無視されない** | 不可 |
| 3 | Item | ユーザーのアイテム。**アップロード時にこの層の物は Default に移される**。Item とだけ衝突（3.8.2 で reserved3 から改名） | 不可 |
| 4 | Water | ポータル、ミラー本体、ポストプロセスで使う。**ミラーに映らない** | 可 |
| 5 | UI | VRChat の UI ポインタはメニューを開いている時しか反応しない | 不可 |
| 6, 7 | reserved6, reserved7 | 予約。アップロード時に Default へ | 不可 |
| 8 | Interactive | 未使用。ステッカーを貼らせたくない Collider に推奨 | 不可 |
| 9 | Player | ローカル以外のプレイヤー | 不可 |
| 10 | PlayerLocal | ローカルプレイヤー（頭無し） | 不可 |
| 11 | Environment | 未使用 | 可 |
| 12 | UiMenu | メニューを開いている時だけ UI ポインタが反応 | 不可 |
| 13 | Pickup | VRCPickup の既定層。プレイヤーと衝突しない | 不可 |
| 14 | PickupNoEnvironment | Pickup とだけ衝突 | 不可 |
| 15, 16 | StereoLeft, StereoRight | 未使用 | 不可 |
| 17 | Walkthrough | プレイヤーと衝突しない | 可 |
| 18 | MirrorReflection | ミラー内のローカルプレイヤー。**この層のレンダラーはミラーにだけ映る**。Collider は VRChat のレイを遮らない | 不可 |
| 19 | InternalUI | 使用禁止（メニュー・ネームプレート） | 不可 |
| 20 | HardwareObjects | 使用禁止（コントローラー・トラッカー表示） | 不可 |
| 21 | reserved4 | 予約。アップロード時に Default へ | 不可 |
| 22〜30 | （ユーザー） | 自由。名前と衝突設定は上書きされない | 可（Interact Passthrough に入れると不可） |
| 31 | （ユーザー） | Unity エディタのプレビューが使う。**避ける**（docs も "Avoid using this layer"） | 可 |

### 3.2 衝突マトリクス（SDK の設定値。`VRCProjectSettings.prefab` の `layerCollisionArr` を復号。o=衝突する、.=しない、対称）

```
                 0  1  2  3  4  5  6  7  8  9 10 11 12 13 14 15 16 17 18 19 20 21
 0 Default       o  o  o  .  o  .  o  o  o  o  o  o  .  o  .  o  o  o  o  .  o  o
 1 TransparentFX o  o  o  .  o  .  o  o  o  o  o  o  .  o  .  o  o  o  o  .  o  o
 2 IgnoreRaycast o  o  o  .  o  .  o  o  o  o  o  o  .  o  .  o  o  o  o  .  o  o
 3 Item          .  .  .  o  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .  .
 4 Water         o  o  o  .  o  .  o  o  o  o  o  o  .  o  .  o  o  o  o  .  o  o
 5 UI            .  .  .  .  .  .  o  o  .  .  .  .  .  .  .  .  .  .  .  .  .  .
 6 reserved6     o  o  o  .  o  o  o  o  o  o  o  o  o  o  o  o  o  o  o  o  o  o
 7 reserved7     o  o  o  .  o  o  o  o  o  o  o  o  o  o  o  o  o  o  o  o  o  o
 8 Interactive   o  o  o  .  o  .  o  o  o  o  o  o  .  o  .  o  o  o  o  .  o  o
 9 Player        o  o  o  .  o  .  o  o  o  .  .  o  .  .  .  .  .  .  o  .  o  o
10 PlayerLocal   o  o  o  .  o  .  o  o  o  .  .  o  .  .  .  .  .  .  o  .  o  o
11 Environment   o  o  o  .  o  .  o  o  o  o  o  o  .  o  .  o  o  o  o  .  o  o
12 UiMenu        .  .  .  .  .  .  o  o  .  .  .  .  .  .  .  .  .  .  .  .  .  .
13 Pickup        o  o  o  .  o  .  o  o  o  .  .  o  .  o  o  o  o  o  .  .  .  .
14 PickupNoEnv   .  .  .  .  .  .  o  o  .  .  .  .  .  o  .  .  .  .  .  .  .  .
15 StereoLeft    o  o  o  .  o  .  o  o  o  .  .  o  .  o  .  o  o  o  o  .  o  o
16 StereoRight   o  o  o  .  o  .  o  o  o  .  .  o  .  o  .  o  o  o  o  .  o  o
17 Walkthrough   o  o  o  .  o  .  o  o  o  .  .  o  .  o  .  o  o  o  o  .  o  o
18 MirrorReflect o  o  o  .  o  .  o  o  o  o  o  o  .  .  .  o  o  o  o  .  o  o
19 InternalUI    .  .  .  .  .  .  o  o  .  .  .  .  .  .  .  .  .  .  .  .  .  .
20 HardwareObj   o  o  o  .  o  .  o  o  o  o  o  o  .  .  .  o  o  o  o  .  o  o
21 reserved4     o  o  o  .  o  .  o  o  o  o  o  o  .  .  .  o  o  o  o  .  o  o
```

読み方の要点：

- プレイヤー（9・10）と**衝突しない**：Item、UI、Player、PlayerLocal、UiMenu、Pickup、PickupNoEnvironment、StereoLeft/Right、Walkthrough、InternalUI。通り抜けられる床・プレイヤーを押さない物は Walkthrough か Pickup 系にする。
- Pickup（13）と**衝突しない**：Item、UI、Player、PlayerLocal、UiMenu、MirrorReflection、InternalUI、HardwareObjects、reserved4。床をこれらの層にすると Pickup が抜け落ちる。
- MirrorReflection（18）はプレイヤーとは衝突する。トリガー用の Collider に向く（同梱ギミックの README の推奨）が、床・壁の描画用には使わない（メインカメラに映らない）。

### 3.3 よくある落とし穴

- SDK の「Setup Layers for VRChat」は index **3〜21** の名前を上書きし、そこにあった独自レイヤーは下へ押し出される（確認ダイアログが警告する。`WorldBuilder` L386-388）。**独自レイヤーは 22〜31 に作る。** 判定は 3〜21 の名前と 0〜21 の衝突の組だけで、22 以降は設定も検査もしない（`VRCCore-Editor.dll` の `UpdateLayers`）。
- 組み込み層（0〜21）の名前・衝突を変えてもアップロード時に上書きされる。22〜31 は保持される（`docs/worlds/layers.md` L9-11）。
- Canvas を UI（5）のままにすると、VRChat のメニューを開かないと押せない。Default などに変える（`docs/worlds/components/vrc_uishape.md` L27-28）。
- 予約層（Item・reserved6/7・reserved4）や InternalUI・HardwareObjects に物を置かない（アップロード時に Default へ移る、表示が崩れる。`docs/worlds/layers.md` L22-40）。
- Interact のレイ（遠くの Pickup、UI のレーザー）を**透過する層は UiMenu・UI・PlayerLocal・MirrorReflection だけ**。他の VRChat 層とユーザー層の Collider はレイを遮る。ユーザー層を透過させるには Descriptor の Interact Passthrough に入れる。VR は手から、Desktop/Mobile はカプセル内からレイが出る（`docs/worlds/layers.md` L87-97）。
- 物理クエリ（Raycast など）は LayerMask で予約層を避け、結果を `Utilities.IsValid` で確かめる。アイテムなど保護されたオブジェクトは null になり、UdonBehaviour が止まることがある（`docs/worlds/layers.md` L54-85、`docs/worlds/items.md` L38-44）。
- Item（アイテム）はワールドの VRChat.com の設定「Default Content Settings > Items Enabled」で無効化できる（既定は有効。`docs/worlds/items.md` L12-24）。

---

## 4. ビルド・アップロードを止める条件（SDK 3.10.5）

Builder の検証欄で **Error が1件でもあるとビルドできない**（Warning / Info は止めない。`PanelBuilder` L149-152）。`Build()` は実行直前に検証をやり直してから判定する（`WorldBuilder` L2648-2685）。Build & Test・Build & Reload・Build のみも同じ `Build()` を通るので、A〜D は Publish と共通。E はアップロードのときだけ。

### 4.1 A：Builder パネルの前提

| # | 条件 | 結果 |
| --- | --- | --- |
| A1 | 読み込み中の全シーンで PipelineManager が2つ以上（非アクティブも数える） | Builder 自体が無効（"Multiple Pipeline Managers found in scene…"） |
| A2 | Scene Descriptor が0 | Builder 無効、「Add a VRCSceneDescriptor」ボタン |
| A3 | Scene Descriptor が2つ以上（全シーン合計） | Error で検証打ち切り。エクスポーターでも止まる |
| A4 | レイヤー名（3〜21）か衝突マトリクス（0〜21）が SDK 設定と不一致 | 「Setup Layers / Setup Collision Matrix」の手順だけが表示され、ビルド時 Error |

複数シーンを同時に開いていると、Descriptor と PipelineManager は全シーン分数えられる。アップロードされるのはアクティブシーンだけ。

### 4.2 B：ビルド要求コールバック（UdonSharp）

| # | 条件 | 結果 |
| --- | --- | --- |
| B1 | スクリプトのコンパイル中（U# の force compile が OFF のとき） | "Scripts are in the process of compiling, please retry build after scripts have compiled." でビルド中止 |
| B2 | U# のコンパイルでどれかのスクリプトにエラー、または U# のアップグレードが走った | "Failed to compile UdonSharp scripts for build…" で中止 |
| B3 | ユーザー独自の `IVRCSDKBuildRequestedCallback` が false | 中止（例外を投げた場合は止めずに続行） |

出典：`Packages/com.vrchat.worlds/Integrations/UdonSharp/Editor/BuildUtilities/UdonSharpBuildChecks.cs` L19-43、`UdonSharpBuildCompile.cs` L13-39。

### 4.3 C：`Build()` 内の Error（3.10.5 ではこれで全部）

| # | 条件 | メッセージ（要旨） |
| --- | --- | --- |
| C1 | SDK2 と SDK3 のコンポーネントが同じシーンにある | "This scene contains components from the VRChat SDK version 2 and version 3…" |
| C2 | Android ターゲットでテクスチャ圧縮の既定が ASTC でない | "Default texture format on Android should be set to the newer ASTC format…"（Fix あり） |
| C3 | iOS ターゲットで、ログイン中アカウントが iOS 非対応 | "iOS is not supported as a build target." |
| C4 | PipelineManager が2つ以上 | "There are multiple pipeline manager components in the scene…"（Fix あり） |
| C5 | PipelineManager が0 | "This scene has no pipeline manager component…"（Fix で Descriptor の GO に追加） |
| C6 | `VRC_EventHandler`（SDK2）がある | "You have Event Handlers in your scene that are not allowed…" |
| C7 | VRCObjectSync と同じ GameObject に Manual 同期の UdonBehaviour | "Object Sync cannot share an object with a manually synchronized Udon Behaviour" |
| C8 | VRCObjectSync と同じ GameObject に VRCObjectPool | "Object Sync cannot share an object with an object pool" |
| C9 | 有効なスポーンが0（null・空・全要素 null） | "You must add at least one valid spawn point…" |
| C10 | A4 と同じ | "You must address Layers and Collision Matrix issues before you can build." |

出典：`PanelBuilder` L554-593、`WorldBuilder` L434-523, L868-876, L953-961, L2669-2677。

### 4.4 D：エクスポーター（`VRCSDK3-Editor.dll` の `VRCWorldAssetExporter`）

- Descriptor がちょうど1つでない → ダイアログ "Scene needs exactly one VRC_SceneDescriptor" で中止。Descriptor のルート配置は要求されない（文言は「root に」だが判定は null チェックだけ）。
- 有効スポーン0 → 中止。
- **Network ID の割り当てでエラー** → "Failed to assign network IDs, N errors encountered! Try using the Network ID Utility to resolve them." で中止（`AssignSceneNetworkIDs`）。
- ビルド中のシーン処理で PipelineManager が0か2つ以上 → `BuildFailedException`（`Packages/com.vrchat.worlds/Editor/VRCSDK/SDK3/VRCWorldPostBuildValidator.cs` L11-37）。
- ビルド前に開いているシーンが保存される（`EditorSceneManager.SaveOpenScenes()`）。未保存の変更はこの時点で保存される。
- シーン内の `VRCPlayerObject` はビルド中だけ非アクティブにされ、Descriptor に登録される（`ConfigureScenePlayerPersistence`）。

### 4.5 E：アップロード（Build & Publish のときだけ）

| # | 条件 | 結果 |
| --- | --- | --- |
| E1 | 著作権（所有権）同意のモーダルに同意しない | "You must agree to the ownership agreement to upload"（`Packages/com.vrchat.base/Editor/VRCSDK/Dependencies/VRChat/VRCCopyrightAgreement.cs` L110-142） |
| E2 | アカウントにアップロード権限が無い（Trust Rank New User 未満） | "Current User does not have permissions to build and upload worlds"（`WorldBuilder` L3007-3020） |
| E3 | **モバイル（Android/iOS）で圧縮後 104,857,600 バイト（100MB）超** | "World download size is too large for the target platform. X > Y"。検証欄では Warning だが Publish は拒否される（`WorldBuilder` L2823-2836）。PC は上限なし |
| E4 | 新規ワールドでサムネイルが無い | "You must provide a path to the thumbnail image when creating a new world"。UI では "Please set a name and thumbnail before uploading"（L2568-2575, L2864-2869） |
| E5 | 既存 ID の作者が別のユーザー | "World's current ID belongs to a different user, assign a different ID"（L2871-2878） |

新規ワールドのサムネイルは 800×600 にクロップされる（L2892）。サムネイルの選択ダイアログ・同意モーダルは MCP のブリッジを止めるので、Publish は本人が行う。

### 4.6 SDK が検査しないもの（自分で確かめる）

- **許可リスト外のコンポーネント**（素の Unity `VideoPlayer`、AVPro の `MediaPlayer`、レガシー `Animation`、自作の MonoBehaviour など）は、SDK のビルドでは検出も除去もされない。SDK 3.10.5 のエディタコードに呼び出し元が無く、クライアントの読み込み時に除去されると推定（`Packages/com.vrchat.base/Runtime/VRCSDK/Dependencies/VRChat/Scripts/Validation/WorldValidation.cs` L575-650）。**アップロードできても実機で黙って消える**ので、`cct_world_audit` の許可リスト検査で見る。docs の一覧（`docs/worlds/whitelisted-world-components.md`）は SDK のリストと一部食い違う（VideoPlayer・AVPro を許可と書くが SDK3 のリストに無い）。
- ポリゴン数・マテリアル数・ライト数・ミラー数・動画プレイヤー数・ライトベイクの有無・オクルージョンの有無・Reference Camera の有無は SDK は検査しない。
- `IPreprocessCallbackBehaviour.OnPreprocess()` は 3.10.5 ではアバタービルドでだけ呼ばれ、ワールドのシーンに置いても呼ばれない（`docs/sdk/build-pipeline-callbacks-and-interfaces.md` の記述と食い違う。SDK のコードを優先）。

### 4.7 止めない警告・情報（抜粋）

AudioSource に VRC_SpatialAudioSource が無い（1件ずつ表示）、ONSPAudioSource、`autoSpatializeAudioSources` が ON、Physics.gravity が真下でない、Fog stripping が Custom、Lighting の Auto Generate が ON（ビルドが止まる恐れ）、テクスチャ最大サイズ 8192 超、Box ミップフィルタ（Kaiser 推奨）、UI/Default の UI（Supersampled UI 推奨）、TextMeshPro 不使用、Billboard パーティクルの Allow Roll、Substance 材質、モバイルで許可リスト外のシェーダー（Renderer のみ。ワールドでは警告だけ）、PhysBone+Collider や Contact が 1024 超（`WorldBuilder` L527-937、`PanelBuilder` L596-617）。

---

## 5. PC / Android / iOS の出し分け

対象：Windows（VR/Desktop）、Android（Quest 2 以降、PICO 4 以降、Vive XR Elite、Android 10 以上のスマホ/タブレット）、iOS（iOS 17.0 以上の iPhone/iPad）（`docs/platforms/index.md` L7-17）。

1. Unity に Android / iOS Build Support を入れる（`docs/platforms/android/cross-platform-setup.md` L11-18）。
2. Control Panel の Builder → Build セクションの Platform で切り替える。初回は全アセットの再インポートで時間がかかる（L20-28）。MCP の呼び出しでは切り替えない（長時間処理・ダイアログ）。
3. 切り替えた先で SDK の検証、Console、Play Mode を確かめる。最適化は「省略できない」（L30-42）。モバイルの制約は [負荷とプラットフォーム](performance-and-platforms.md) の10章。
4. **同じ Blueprint ID** に上げる（L58-64）。
5. 3.7.6 以降は Platforms で複数を選び **Multi-Platform Build & Publish** ができる（PC から順にビルドし、最後に元のプラットフォームへ戻る。`releases/release-3.7.6.md` L16-21）。
6. 3.7.2 以降は Udon Code Signing がプラットフォームごとに違うので、**全プラットフォームを上げ直す**のが推奨（`releases/release-3.7.2.md` L25-31）。

- ワールドが自分のプラットフォームに無いと、ポータルや UI から入れない（`cross-platform-setup.md` L87）。iOS 版が無いと Android 版を iOS で読み込む実験的なフォールバックがある（Standard Lite 中心なら比較的動く）が、iOS 版のアップロードが推奨（L77-83）。
- PC と Quest で共通のボックス/低ポリ Collider を空の GameObject の下にまとめ、両方で同じものを使うと「浮く」問題を防げる（L105）。透明（アルファカットアウトを含む）は避ける（L106-107）。
- プラットフォームごとに内容を変える（PC は高品質、Quest は軽量版など）には、EasyQuestSwitch（VCC で入るパッケージ）の利用が強く推奨されている（`cross-platform-setup.md` L50、`docs/platforms/android/android-best-practices.md` L29）。PC 用と Quest 用を別プロジェクトで持つ場合は、上の共通 Collider を両方にコピーして揃える（L105）。

---

## 6. Build & Test の要点

詳細は [テストとデバッグ](testing-and-debugging.md)。

- Build Type：Build & Publish Your World Online / Build & Test Your World / Build & Reload Your World / Test Your Last Build / Reload Your Last Build（`docs/worlds/creating-your-first-world.md` L115-128）。
- ローカル系は Clients（起動するクライアント数。SDK の UI で 0〜8 にクランプ、`WorldBuilder` L2509）、Force Non-VR、Enable World Reload を持つ。ローカル系はアクティブターゲットが Windows64 / Android / iOS のときだけ使える（L2551-2556）。
- Settings タブで VRChat.exe のパスを設定しておく（`docs/worlds/udon/using-build-test.md` L25-31）。

---

## 7. Content Info と Community Labs

### 7.1 Builder の Content Info（`docs/worlds/creating-your-first-world.md` L69-95）

- Name、Description、Maximum Capacity、Recommended Capacity、Content Warnings、Tags、World Debugging、Thumbnail。
- Maximum Capacity は 1〜**80** にクランプ、新規の既定 32。Recommended Capacity の既定 16（1〜Capacity）（`WorldBuilder` L56, L1434-1435, L1528-1550）。Recommended に達した Public インスタンスは一覧から外れる。Recommended が無い古いワールドは、実効の上限が設定値の2倍になる。
- World Debugging はタグ `debug_allowed` を付ける。他の人にもデバッグビュー・オーバーレイを見せる設定（[テストとデバッグ](testing-and-debugging.md) 5章）。

### 7.2 Community Labs / Public（`docs/worlds/submitting-a-world-to-be-made-public.md`）

- Public にするには Community Labs へ提出する。VRChat.com（Edit World Info → Danger Zone → World Visibility → Publish）か、Unity でのアップロード時（L9-13）。提出するとすぐ Labs を有効にした人に公開され、後に Public になる（L15）。
- **1ユーザー7日に1ワールド**まで。更新は何度でも可で、状態は変わらない。Public 後の更新に再提出は不要（L19-21）。
- 規約違反で提出権が一定期間止まる。Discord DM やメールでの承認依頼は受け付けない（L22-24）。
- サイズは **200MB 未満** が推奨（L25）。
- ペデスタルのアバターは「適度に最適化」。公開後に規約違反のアバターへ差し替えると1か月の提出停止（L27-33）。
- 性能（L35-46）：スポーンで VR 1人のとき **45 FPS 以上**、Single-Pass Stereo 対応のシェーダー、Android ではモバイルシェーダー、UI は Supersampled UI シェーダー、ポストプロセスは慎重に（特に Chromatic Aberration・SSR・SSAO）、**動画プレイヤーは1部屋2台まで**、**ミラーは1部屋1枚まで・既定 OFF・トグル**、リアルタイムライトを多用しない、ライトのベイクは非常に重要。
- テスト（L48-54）：ポータルから落ち続けないか、VR で確認、ライティングは Standard/PBR シェーダーで確認（Toon では正しく見えない）。`.blend` を直接使わず FBX にする。
- 「Content Warnings are deprecated」とこのページは書く（L23）が、`creating-your-first-world.md` L81 と 3.7.6 のリリースノート（L22-23）は Content Warnings 欄を扱っている。このページの frontmatter の updatedAt は 2023-04-21 で、記述が古い可能性がある（未確認）。
- カテゴリ：Avatar World はタイトルに avatar / avatars / avi / avis のどれか、Game World はタグ `game`（L62-97）。

---

## 8. Claude が行う範囲と本人に頼む操作

| 作業 | 誰が | 理由 |
| --- | --- | --- |
| シーン構成、コンポーネントの設定、配置、材質、ギミックの取り込みと配線 | Claude（計画提示 → 1件試行 → 読み戻し） | MCP で読み戻して確かめられる |
| 静的点検（`cct_world_audit`）、環境の確認（`cct_environment`）、撮影（`cct_capture_views`） | Claude | 読み取り専用 |
| Setup Layers / Setup Collision Matrix、プラットフォームの切り替え | 本人（Control Panel） | 確認ダイアログ・長時間の再インポート |
| ライトベイク、オクルージョンベイク | 本人（Lighting / Occlusion ウィンドウ）を既定にする | 長時間処理。1回の MCP 呼び出しで同期実行しない |
| Build & Test、Build & Reload | 本人（Control Panel） | ビルドは長時間で、失敗時にダイアログが出る |
| Build & Publish、サムネイル、著作権同意、Labs 提出、VRChat.com の設定 | 本人（明示の依頼があるときだけ） | ダイアログがブリッジを止める。公開は取り消しにくい |

SDK の公開 API（`IVRCSdkWorldBuilderApi.BuildAndTest()` など、`docs/sdk/public-sdk-api.md` L46-105）を `execute_code` から呼ぶ方法は未検証で、既定の手段にしない。
