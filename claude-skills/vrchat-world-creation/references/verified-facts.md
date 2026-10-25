# 検証済み事実の台帳（VRChat ワールド制作）

形式：事実 / 出典 / 検証日。
**記憶ベースの値をここに書かない。** 一次資料（SDK のソース・DLL、公式ドキュメント）か実測だけを書く。確かめられなかったものは「未確認」、推論は「推定」と書く。誤りが分かったら「【訂正】」と書いて直す。

出典の略記：
- `docs/…`・`releases/…`：GitHub `vrchat-community/creator-docs` の `Docs/docs/`・`Docs/releases/`（creators.vrchat.com の原稿）。**2026-10-20 時点のスナップショット**を読んだ。
- `W/…`：`Packages/com.vrchat.worlds/`（**3.10.5**）、`B/…`：`Packages/com.vrchat.base/`（3.10.5）。DLL（`VRCSDK3-Editor.dll` など）の内容は逆アセンブル・逆コンパイルで読んだ（参照のみ）。
- `WB`：`W/Editor/VRCSDK/SDK3/VRCSdkControlPanelWorldBuilder.cs`、`PB`：`B/Editor/VRCSDK/Dependencies/VRChat/ControlPanel/VRCSdkControlPanelBuilder.cs`、`U#`：`W/Integrations/UdonSharp/`。
- 「資料読解 2026-10-24」は、上の資料をクラウドセッションで読み、敵対的な再確認（引用行の機械照合、衝突マトリクスの再復号、許可リストの機械照合）をした日。「資料読解 2026-10-25」は、このスキルの記述を同じ資料で読み直した日（衝突マトリクスの3回目の復号、引用ファイルと行範囲の機械照合を含む）。どちらも Unity 実機・VRChat 実機での確認ではない。

---

## 1. 版

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| 制作用 Unity は 2022.3.22f1。これより新しい Unity でアップロードすると読み込めなくなる、と公式が警告 | `docs/sdk/upgrade/current-unity-version.md` L19-25、`Docs/src/components/CurrentUnityVersion2022.js` L3-5 | 資料読解 2026-10-24 |
| SDK 3.10.5 のリリースは 2026-09-04 | `releases/release-3.10.5.md` | 資料読解 2026-10-24 |
| `com.vrchat.worlds` 3.10.5 の `unity` は "2022.3"、依存は base 3.10.5・cinemachine 2.9.7・textmeshpro 3.0.6・inputsystem 1.2.0・ai.navigation 1.1.5 | `W/package.json` | 資料読解 2026-10-24 |
| `com.vrchat.base` 3.10.5 は postprocessing 3.2.2・ugui 1.0.0 などに依存 | `B/package.json` | 資料読解 2026-10-24 |
| UdonSharp と ClientSim は Worlds SDK に同梱（3.4.0、2023-10-04 から）。旧単体パッケージは `legacyPackages` | `releases/release-3.4.0.md` L2-24、`W/package.json` | 資料読解 2026-10-24 |
| Unity 2022 対応の最初の SDK は 3.5.0（2023-12-08） | `releases/release-3.5.0.md` L2-17 | 資料読解 2026-10-24 |
| ロードマップ（2026-08-31 更新）で「Unity 6 VRChat SDK」は In progress | `docs/roadmap.md` L8-10, L31 | 資料読解 2026-10-24 |
| クライアントは 2026.3.3（2026-10-02）で全プラットフォーム Unity 6。Unity 6 対応 SDK は未リリース。SDK 3.10.6-beta.3（2026-10-23）。VCC 2.4.5（2025-12-10） | ユーザーのスキル `vrchat-unity-version-check` のスナップショット節（正はそちら） | 2026-10-23（同スキルの記録） |
| Unity 2022 でアップロードしたワールドは 2019 で再アップロード不可 | `docs/sdk/upgrade/unity-2022.md` L39, L105 | 資料読解 2026-10-24 |

## 2. プロジェクト構成と SDK が強制する設定

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| アップロードには Trust Rank New User 以上。Steam/Meta/Viveport アカウント不可 | `docs/sdk/index.md` L50 | 資料読解 2026-10-24 |
| 3.9.0 以降、Build & Test にトラストランクは不要 | `releases/release-3.9.0.md` L30-32 | 資料読解 2026-10-24 |
| SDK はビルド時に Linear、Single Pass Stereo、Forward、シェーダーチャンク 4MB、Always Included Shaders 空、SRP null、.NET 4.x、IL2CPP を設定 | `B/Editor/VRCSDK/Dependencies/VRChat/EnvConfig.cs` L232-262, L789-845, L1072-1103 | 資料読解 2026-10-24 |
| Android 以外のターゲットのとき `androidBuildSubtarget = ASTC` にする。Android ターゲット上では強制せず、ASTC でなければ Error | 同 L458-466、`PB` L568-581 | 資料読解 2026-10-24（【訂正】旧メモは逆に書いていた） |
| シーンが無い新規プロジェクトでは `Assets/Scenes/VRCDefaultWorldScene.unity` を自動生成。その Directional Light は Realtime（`m_Lightmapping: 4`） | `W/Editor/VRCSDK/SDK3/VRCSceneTemplateInitializer.cs` L9-11, L37-46、`VRCDefaultWorldScene.unity` L296-354 | 資料読解 2026-10-24 |
| VRCWorld プレハブの実体は `W/Samples/UdonExampleScene/Prefabs/VRCWorld.prefab`（docs の別パスは古い） | `WB` L1254 | 資料読解 2026-10-24 |
| VRCWorld の UdonBehaviour 3つのうち VRCWorldSettings・AvatarScalingSettings は Udon Graph（`UdonGraphProgramAsset`）、WorldQualitySettings だけ U#。VRCWorldSettings の公開変数は jump 3・walk 2・run 4・strafe 2 の4つで、Gravity・Use Legacy Locomotion は U# 版 `PlayerModSetter` にしか無い | `VRCWorld.prefab` の programSource と公開変数の復号、`W/Samples/UdonExampleScene/UdonProgramSources/VRCWorldSettings.asset`、`U#/Samples~/Utilities/PlayerModSetter.cs` L12-19 | 資料読解 2026-10-25（【訂正】旧版は「U# 3つ」、Gravity 1 も VRCWorld の既定と書いていた） |
| `VRC_ENABLE_PLAYER_PERSISTENCE` は、ClientSim の型があれば EnvConfig が定義シンボルに足す | `B/.../EnvConfig.cs` L1310-1322 | 資料読解 2026-10-24（クライアント側は未確認） |

## 3. Scene Descriptor とスポーン

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| 既定値：spawnOrder Random(2)、spawnOrientation Default(0)、RespawnHeightY -100、ObjectBehaviourAtRespawnHeight Respawn(0)、ForbidUserPortals false。新規シーンは spawnRadius 0.2 | `B` の `VRCSDKBase.dll`（ctor）、`VRCDefaultWorldScene.unity` L436-503 | 資料読解 2026-10-24 |
| spawnRadius は 3.8.2 で追加 | `releases/release-3.8.2.md` L19-22 | 資料読解 2026-10-24 |
| スポーンの x・y・z の絶対値のどれかが 1000 以上で Warning（`WORLD_MAX_SPAWN_OFFSET = 1000f`） | `WB` L56-66, L939-1000 | 資料読解 2026-10-24 |
| スポーンの y が RespawnHeightY より下で Warning（3.10.0 追加） | 同、`releases/release-3.10.0.md` L34 | 資料読解 2026-10-24 |
| ビルド時に Descriptor の gravity ← Physics.gravity、layerCollisionArr ← 32×32 の衝突マトリクス、unityVersion ← Application.unityVersion | `VRCSDK3-Editor.dll` `VRCWorldAssetExporter.ExportCurrentSceneResource` | 資料読解 2026-10-24 |
| Interact Passthrough のビット i はレイヤー 22+i | ClientSim `W/Integrations/ClientSim/Runtime/System/ClientSimInteractiveLayerProvider.cs` L22, L38 | 資料読解 2026-10-24（クライアント本体は未確認） |
| docs の「Forbid Free Modification」「User Custom Voice Falloff Range」は 3.10.5 の Descriptor にフィールドが無い | `docs/worlds/components/vrc_scenedescriptor.md` L27-29 と DLL の照合 | 資料読解 2026-10-24 |
| Reference Camera の Near は 0.001〜0.05 にクランプ、Far は Near+0.1 以上 | `docs/worlds/udon/vrc-graphics/vrc-camera-settings.md` L61-65 | 資料読解 2026-10-24 |
| Maximum Capacity は 1〜80、新規の既定 32、Recommended の既定 16 | `WB` L56, L1434-1435, L1528-1550 | 資料読解 2026-10-24 |
| Build & Test の Clients は 0〜8 にクランプ | `WB` L2509 | 資料読解 2026-10-24 |

## 4. レイヤーと衝突

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| レイヤー 0〜21 の名前は VRChat が決め、22〜31 はユーザー用 | `docs/worlds/layers.md` L9-44、`B/Runtime/VRCSDK/Dependencies/VRChat/Resources/VRCProjectSettings.prefab` | 資料読解 2026-10-24 |
| 3 は 3.8.2 で reserved3 → Item に改名。Item 同士だけ衝突、アップロード時に Item 層の物は Default へ | `releases/release-3.8.2.md` L18、`docs/worlds/layers.md` | 資料読解 2026-10-24 |
| 3・6・7 は 3.6.0 で VRChat の予約になった | `releases/release-3.6.0.md` L28 | 資料読解 2026-10-24 |
| Setup Layers は index 3〜21 の名前を上書き。判定も 3〜21 の名前と 0〜21 の衝突だけ | `VRCCore-Editor.dll` `UpdateLayers`（`numLayers: 22`）、`WB` L367-428 | 資料読解 2026-10-24 |
| 衝突マトリクス 0〜21 の表（[セットアップ](setup-and-upload.md) 3.2）は `VRCProjectSettings.prefab` の `layerCollisionArr` 1024 要素の復号で、独立した再復号と完全一致・対称 | 同 prefab | 資料読解 2026-10-24 |
| Interact のレイを透過するのは UiMenu・UI・PlayerLocal・MirrorReflection だけ | `docs/worlds/layers.md` L87-93 | 資料読解 2026-10-24 |
| VRCPickup は Add Component（`Reset()`）で Pickup 層へ移り version 1.1 になる | `releases/release-3.10.4.md` L43、`VRC_Pickup.Reset()`・`VRCPickup.Reset()`（逆コンパイル） | 資料読解 2026-10-24。スクリプトからの AddComponent で Reset が呼ばれるかは未確認（【訂正】旧版は「呼ばれない」と推定していた） |

## 5. ビルド・アップロードの条件

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| 検証欄の Error が1件でもあればビルド不可。Warning/Info は止めない | `PB` L149-152、`WB` L2648-2685 | 資料読解 2026-10-24 |
| `Build()` 内の Error 条件は C1〜C10 の10件（[セットアップ](setup-and-upload.md) 4.3） | `PB` L554-593、`WB` L434-523, L868-876, L953-961, L2669-2677 | 資料読解 2026-10-24 |
| U# のコンパイル中・コンパイルエラー・アップグレードでビルド中止 | `U#/Editor/BuildUtilities/UdonSharpBuildChecks.cs` L19-43、`UdonSharpBuildCompile.cs` L13-39 | 資料読解 2026-10-24 |
| Network ID の割り当てエラーでビルド中止 | `B` の `AssignSceneNetworkIDs`（逆コンパイル） L14-47 | 資料読解 2026-10-24 |
| モバイル（BuildTarget 9 / 13）で圧縮後 104,857,600 バイト超は Upload() が `UploadException`。PC は上限なし | `WB` L2823-2836、`VRCSDKBase.dll` `ValidationHelpers.GetAssetBundleSizeLimit` | 資料読解 2026-10-24（【訂正】旧メモは「Warning でブロックしない」と書いていた） |
| 新規ワールドはサムネイル必須、800×600 にクロップ | `WB` L2864-2869, L2892 | 資料読解 2026-10-24 |
| 著作権同意（`content.copyright.owned`）に同意しないとアップロード不可 | `B/Editor/VRCSDK/Dependencies/VRChat/VRCCopyrightAgreement.cs` L22, L110-142 | 資料読解 2026-10-24 |
| 許可リスト外のコンポーネントは SDK のビルドでは検出も除去もされない（呼び出し元0件）。クライアントで除去されると推定 | `B/Runtime/VRCSDK/Dependencies/VRChat/Scripts/Validation/WorldValidation.cs` L575-650、エディタ DLL の grep | 資料読解 2026-10-24（実機は未確認） |
| `IPreprocessCallbackBehaviour` は 3.10.5 ではアバタービルドでだけ呼ばれる | `B` の `PreprocessCallbackBehaviours`（逆コンパイル） L7-24 | 資料読解 2026-10-24（docs と食い違い） |
| 「プリミティブ Mesh 参照」「Camera.main 配下の PostProcessVolume」の Warning は `ResetIssues()` で消され通常は表示されない | `WB` L198-221, L250 | 資料読解 2026-10-24 |
| Multi-Platform Build & Publish は 3.7.6 から | `releases/release-3.7.6.md` L16-21 | 資料読解 2026-10-24 |
| Udon Code Signing（3.7.2）でプラットフォームごとに署名が違うので、全プラットフォームの上げ直しが推奨 | `releases/release-3.7.2.md` L25-31 | 資料読解 2026-10-24 |

## 6. 性能・プラットフォームの数値

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| スポーンで VR 1人 45 FPS 以上、動画プレイヤーは1部屋2台まで、ミラーは1部屋1枚まで・既定 OFF | `docs/worlds/submitting-a-world-to-be-made-public.md` L35-46 | 資料読解 2026-10-24 |
| Labs/Public のサイズ推奨は 200MB 未満、Labs への提出は1ユーザー7日に1ワールド | 同 L19-25 | 資料読解 2026-10-24 |
| Android のワールドは約 250,000 トライアングルを予算に、テクスチャは最大 1024 を目安、GPU Instancing ON | `docs/platforms/android/quest-content-optimization.md` L56-58, L96-104 | 資料読解 2026-10-24 |
| Android でポストプロセスは完全に無効、Cloth は完全に無効、ワールドのシェーダーは制限なし | `docs/platforms/android/quest-content-limitations.md` L45-57、`quest-content-optimization.md` L143-183 | 資料読解 2026-10-24 |
| iOS は Android と同じ制限 | `docs/platforms/iOS/setting-up-unity-for-creating-ios-content.md` L38 | 資料読解 2026-10-24 |
| モバイルのシェーダー許可リストは13件。判定は Renderer のシェーダー名の完全一致だけ（UI の Graphic は対象外）。`Toon Standard` はリストに無い | `B/.../Validation/WorldValidation.cs` L531-546、`B` の `ValidationUtils`（逆コンパイル） | 資料読解 2026-10-24 |
| 同梱の Supersampled UI の実名は `VRChat/Mobile/Worlds/Supersampled UI`（許可リストの `World/` と不一致） | `B/Runtime/VRCSDK/Sample Assets/Shaders/Mobile/VRChat-Mobile-SupersampledUnlit.shader` L4、`WB` L708 | 資料読解 2026-10-24 |
| テクスチャ最大サイズ 8192 超で Warning（`MAX_SDK_TEXTURE_SIZE = 8192`） | `PB` L65, L1129-1166 | 資料読解 2026-10-24 |
| ワールドの PhysBone+Collider、Contact は同時に各 1024（`MAX_SHAPES_WORLD = 0x400`） | `docs/worlds/components/index.md` L32-39、`VRC.Dynamics.dll` | 資料読解 2026-10-24 |
| ミラーの cullingMask は Water と PlayerLocal を常に除外し、HardwareObjects を常に足す。反射像は `_ReflectionTex0/1` に書く | `B` の `VRC_MirrorReflection`（逆コンパイル） L134-195, L596-598, L718-761 | 資料読解 2026-10-24（docs の `_MainTex` と食い違い。クライアントも同じと推定） |
| Mirror Resolution Auto は最大 2048×2048 | `docs/worlds/components/vrc_mirrorreflection.md` 脚注 | 資料読解 2026-10-24 |
| 動画：1ユーザーは新しい URL を5秒に1回、Android は HTTPS 必須、Public では許可リスト外ドメインに制限（3.7.5〜） | `docs/worlds/udon/video-players/index.md` L38-44、`www-whitelist.md` L10, L42-58 | 資料読解 2026-10-24 |
| VRC_SpatialAudioSource の既定：Gain 10dB、Far 40m、Near 0、Volumetric Radius 0、Enable Spatialization ON | `docs/worlds/components/vrc_spatialaudiosource.md` L70-77、`VRCSDKBase.dll`（ctor） | 資料読解 2026-10-24 |
| 3.10.5 で `VRCQualitySettings.ShadowmaskMode`（PC の Distance Shadowmask）と `RealtimeReflectionProbes` を追加 | `releases/release-3.10.5.md` L27-28 | 資料読解 2026-10-24 |
| String / Image Loading は5秒に1回、画像は最大 2048×2048 | `docs/worlds/udon/string-loading.md` L6-8、`image-loading.md` L20-28 | 資料読解 2026-10-24 |

## 7. Udon・ネットワーク

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| Udon の送信は約 11KB/秒、Manual の1回は約 280,496 バイト、Continuous の1回は約 200 バイト | `docs/worlds/udon/networking/network-details.md` L14-16 | 資料読解 2026-10-24 |
| 同期配列が1つでも未初期化だと、その UdonBehaviour は同期されない | 同 L95-98 | 資料読解 2026-10-24 |
| Manual と Continuous が同じ GameObject にあると両方 Manual 扱い | 同 L90-93 | 資料読解 2026-10-24 |
| 引数付きネットワークイベント（最大8引数、U# は `[NetworkCallable]`、既定 5回/秒・最大 100回/秒、`NetworkEventTarget.Others` / `Self`）は 3.8.1 から | `releases/release-3.8.1.md` L16-24、`docs/worlds/udon/networking/events.md` | 資料読解 2026-10-24 |
| イベント引数は1イベント 16KB まで、1024 バイト超は分割されてレート制限に数えられる | `events.md` L171-190 | 資料読解 2026-10-24 |
| ネットワークイベントは後から入った人に再送されない | `docs/worlds/udon/networking/late-joiners.md` L7-9 | 資料読解 2026-10-24 |
| 同期するコンポーネントや同期変数の型・数・順序が変わると旧インスタンスと互換性が無くなる | `docs/worlds/udon/networking/compatibility.md` L8-14 | 資料読解 2026-10-24 |
| U# はオーバーロードとプロパティを使える（docs の FAQ「不可」は古い） | `U#/Tests~/TestScripts/Core/MethodCallsTest.cs`、`PropertyTest.cs` | 資料読解 2026-10-24 |
| U# Assembly Definition の無い asmdef 内の U# スクリプトは "does not belong to a U# assembly" | `U#/Editor/Compiler/UdonSharpCompilerV1.cs` L385-407 | 資料読解 2026-10-24（全体が止まることはユーザーの実測、12章） |
| `UdonSharpEditorUtility.ConvertToUdonBehaviours` は `[Obsolete(…, true)]` で中身は `NotImplementedException` | `U#/Editor/UdonSharpEditorUtility.cs` L73-89 | 資料読解 2026-10-24 |
| ClientSim は `Others` 宛てのイベントを実行しない | `W/Integrations/ClientSim/Runtime/Networking/ClientSimNetworkCalling.cs` L22-25 | 資料読解 2026-10-24 |
| `_` で始まる名前をネットワークで拒否するのは `[NetworkCallable]` の無い旧来のイベントだけ（`[NetworkCallable]` 付きは `_` でも通る） | 同 L49-56、`docs/worlds/udon/networking/events.md` L288-296 | 資料読解 2026-10-25（ClientSim の実装。クライアント本体は推定） |
| Udon は Start 前のイベントを実行しない（`RunEvent` は `canRunBeforeStart: false`） | `W/Runtime/Udon/UdonBehaviour.cs` L1331-1332 | 資料読解 2026-10-25 |
| ClientSim のメニューは `VRChat SDK > ClientSim`（docs の `VRChat SDK/Utilities/ClientSim` は古い） | `W/Integrations/ClientSim/Editor/Windows/ClientSimSettingsWindow.cs` L64、`docs/worlds/clientsim/systems/editor/settings-window.md` L4 | 資料読解 2026-10-25 |
| ClientSim のキー割り当て（WASD・Shift・Space・C・Z・Tab・左右クリック・ホイール・I/K・J/L・U/O・Esc） | `W/Integrations/ClientSim/Runtime/Input/ClientSimInputMapping.inputactions` | 資料読解 2026-10-25（押しての確認はしていない） |
| VRCObjectSync の Get 系（GetKinematic / GetGravity）は 3.10.5 で Udon に公開されていない | `VRC.Udon.VRCWrapperModules.dll` の公開一覧 | 資料読解 2026-10-24（docs と食い違い） |

## 8. 永続化

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| 1ワールド・1プレイヤーあたり PlayerData 100KB、PlayerObject 100KB（圧縮後） | `docs/worlds/udon/persistence/index.md` L41-47 | 資料読解 2026-10-24 |
| ローカルプレイヤーの `OnPlayerLeft` の中では保存できない。読み書きは `OnPlayerRestored` の後 | 同 L45-50、`player-data.md` L32-48 | 資料読解 2026-10-24 |
| 保存先：公開版はサーバー、Build & Test はローカル（閉じると消える）、ClientSim はプロジェクト内の JSON | `persistence/index.md` L26-39 | 資料読解 2026-10-24 |
| Persistence は 3.7.4 で追加、使用量 API は 3.10.0 で追加 | `releases/release-3.7.4.md` L16-29、`release-3.10.0.md` L26-29 | 資料読解 2026-10-24 |

## 9. テスト・ログ

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| VRChat の output_log は `C:\Users\<ユーザー名>\AppData\LocalLow\VRChat\VRChat` に `output_log_<時刻>.txt` | `docs/worlds/udon/debugging-udon-projects.md` L23-31 | 資料読解 2026-10-24 |
| Udon の停止は "…this UdonBehaviour will be halted." で、"halted" で検索する | 同 L102-105 | 資料読解 2026-10-24 |
| デバッグビューのショートカットは Right Shift + 1 の左のキー + 数字 | `docs/worlds/udon/world-debug-views.md` L9-15 | 資料読解 2026-10-24 |
| `--watch-worlds` で自分で起動したクライアントも Build & Reload の対象になる | `docs/worlds/udon/using-build-test.md` L59-62 | 資料読解 2026-10-24 |
| `VRChat SDK/Udon Sharp/Parse Logs from File`（3.10.2） | `releases/release-3.10.2.md` L17-19 | 資料読解 2026-10-24 |
| ビルド中に稀に "All pipe instances are busy"（Unity 再起動で解決） | `releases/release-3.10.5.md` L89-91 | 資料読解 2026-10-24 |

## 10. MCP for Unity（ワールド作業に関係する分）

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| MCP for Unity 9.7.1 の HTTP 経由のコマンドは既定 30 秒でタイムアウト。実行中の同期処理は中断されず続く | MCP for Unity 9.7.1 のソース（`plugin_hub.py:96`、`TransportCommandDispatcher.cs`）の調査 | 資料読解 2026-10-24（ユーザー環境の改修版サーバーでの挙動は未確認） |
| モーダルダイアログが開いているとブリッジが `no_unity_session` になる | `unity-mcp-operations/references/verified-facts.md` L81（ユーザーの実測） | 2026-08-09 |
| MCP 経由の GameObject 削除はエディタの Undo に載らない | 同 L108（ユーザーの実測） | 2026-08-09 |
| execute_code のコンパイラは泡ワールドでは Roslyn、ota の BPM 作業では CodeDom C#6 と、記録が食い違う。毎回確かめる | 同 L59、`vrchat-bpm-controls/references/wotagei-ota-bpm.md` L59（ユーザーの記録） | 2026-08-09 / 2026-09-11 |
| 9.7.1 のカスタムツール（`cct_*`）は、サーバーの Python の版によって単独 tool として出ず `execute_custom_tool` 経由になることがある | MCP for Unity 9.7.1 のソースの調査。詳細は `claude-creator-tools` | 資料読解 2026-10-24（ユーザー環境は未確認） |

## 11. 同梱ギミック（Claude Creator Tools 0.1.0）

| 事実 | 出典 | 検証日 |
| --- | --- | --- |
| 8種（CctLocalToggle、CctGlobalToggle、CctMirrorSwitcher、CctTeleporter、CctPickupResetter、CctAudioVolume、CctAreaToggle、CctPlayerCounter）、名前空間 `Naoking.ClaudeWorldGimmicks` | `Samples~/WorldGimmicks/README.md`、`Scripts/*.cs` | 2026-10-24（ファイルの読解） |
| 同期方式：GlobalToggle は Manual、PickupResetter は NoVariableSync、他は None | 各スクリプトの `[UdonBehaviourSyncMode]` | 2026-10-25（ファイルの読解） |
| 検証は SDK 3.10.5 の DLL に対する C# 9 コンパイル、U# 非対応構文の静的チェック、Udon 公開 API の照合だけ。Unity 実機・ClientSim・VRChat は未検証 | README「制限事項」 | 2026-10-24 |

---

## 12. ユーザーのワールド記録（泡ワールド）

出典はすべてユーザーのスキル **`unity-mcp-operations/references/verified-facts.md`**（行番号は 2026-10-24 にこのセッションへ同期された写しのもの）。ユーザーが泡ワールドで実測した記録で、この台帳の作成時に再確認はしていない。

| 事実 | 出典（同ファイルの行） | 検証日（記録の日付） |
| --- | --- | --- |
| プロジェクト `C:/Users/naoki/AppData/Local/VRChatCreatorCompanion/VRChatProjects/泡ワールド`、**VRChat Worlds SDK 3.7.3**、Unity 2022.3.22f1。初回接続プロトコルをここで実施 | L40（SKILL.md の原則0） | 2026-08-09 |
| インスタンス名は `泡ワールド@c0510ee8a17c63d1` の形式 | L15 | 2026-08-09 |
| ProBuilder 未導入（manifest.json 実測）→ `manage_probuilder` は使えないはず | L65 | 2026-08-09 |
| execute_code は Roslyn で動いた（「ファイル名または拡張子が長すぎます」は発生せず）。`using` ディレクティブは書けない（フル修飾） | L59, L74, L76 | 2026-08-09 |
| execute_code の Roslyn は Assembly-CSharp を参照しない → 自作の型はリフレクションで扱う | L80 | 2026-08-09 |
| **`GameObject.Find` は非アクティブの GameObject を見つけない**。壁の復元コードが無条件に成功を返し、空振りが隠れた。復元は親から `transform.Find` で辿り、activeSelf を読み戻す | L75 | 2026-08-09 |
| **FBX のルートに内在の localScale がある製品**（hiromu sofa1_obj.fbx = 0.5）を `Vector3.one * 係数` で上書きし、実寸が2倍になった。`アセットのlocalScale × 係数` で設定する | L77 | 2026-08-09 |
| **read_console がエラー0件でも Assembly-CSharp が生成されていなかった**。アバター系5パッケージ（lilycalinventory / krt-material-tools / lilemo / misc-vrc-tools / MA-resonite）が NDMF/MA/lilToon と API 不整合。真実は Editor.log の `error CS` の集計。VCC で該当パッケージを外して対処 | L78 | 2026-08-09 |
| ドメインリロードと execute_code が重なると `success:false, message:null` でも書き込みは成功していることがある → 再送の前に read | L79 | 2026-08-09 |
| **VRChat SDK の Select thumbnail ダイアログ（モーダル）でブリッジが `no_unity_session` になった**。閉じると保留中のコンパイル等が走ってから復帰 | L81 | 2026-08-09 |
| ブリッジの失敗ループ（pid ファイル消失、8080 の TimeWait 大量）と復旧手順 | L82（日本語パスの文字化けと同根の可能性、L85） | 2026-08-09 |
| SDK 3.10.4 統合の UdonSharp で `ConvertToUdonBehaviours` は NotImplementedException。リフレクションによる U# のヘッドレス組み立て手順（AbukuLightSwitch で実証） | L100 | 2026-08-09 |
| **U# Assembly Definition の無い asmdef 内に UdonSharpBehaviour があると、プロジェクト全体の U# コンパイルがジョブ生成前に無言で中断**（Console に出ず Editor.log だけに "does not belong to a U# assembly"）。VRC ビルドも不能。LTCGI の U# Assembly Definition 欠落が原因で、CreateInstance で作って修復 | L101 | 2026-08-09 |
| `UdonBehaviour.publicVariables` のゲッターはエディタで NullReferenceException を投げ得る。Play 中は `UdonBehaviour.Interact()` のリフレクション呼び出しで Interact の機能を確かめられた（ClientSim） | L102 | 2026-08-09 |
| AudioLink 3.1.2 の AudioReactiveLight.prefab はコンポーネントを2個持ち、1個は裏の UdonBehaviour が無い残骸 | L103 | 2026-08-09 |
| VizVid（vvmw）は `Core.audioLink` に AudioLink を挿すだけで再生音声を接続する。AudioReactiveLight は `intensity = amplitude × multiplier` の置換式なので、常時点灯のライトには使わず加算用の別ライトに載せる | L104 | 2026-08-09 |
| MCP 経由の delete は Undo に載らない（create・リネームは載る） | L108 | 2026-08-09 |

記録の食い違い（要確認）：泡ワールドの SDK は 3.7.3 と記録されている一方、U# の事実は「SDK 3.10.4 統合 UdonSharp」として書かれている。泡ワールドが後で 3.10.4 に上がったのか、別プロジェクトでの観測なのかは分からない。泡ワールドの現在の SDK 版は `cct_environment` で取り直す。

PC のローカルスキル `world-udon-gimmick`・`world-lighting-bake` の名前が同ファイル L44 に記録されているが、中身はこの環境から読めない（未確認）。

---

## 13. 未確認・食い違い

- Unity 6 対応 SDK の公開時期と版（2026-10-23 時点で未リリース）。
- 許可リスト外のコンポーネントがクライアントで除去されること、予約層の物が Default へ移されること、Interact Passthrough のビットの解釈は、クライアント本体では未確認（SDK のコードと docs からの推定）。
- Android の AudioSource の「restricted」の中身（docs に記載なし）。
- ライトプローブ・反射プローブ・ライトマップ解像度の公式の数値指針（docs に無い。参考はコミュニティ製 VRWorld Toolkit の検査項目だけ）。
- ClientSim のキー割り当てを実際に押して確かめてはいない（SDK の inputactions ファイルから読んだだけ。docs では画像のみ）。
- Windows の Editor.log の既定の場所（Unity 一般の `%LOCALAPPDATA%\Unity\Editor\Editor.log` と思われるが、このスキルの一次資料では確かめていない。エディタ内では `Application.consoleLogPath` で読める）。
- スクリプトからの AddComponent で VRCPickup の `Reset()` が呼ばれ、Pickup 層・version 1.1 になるかどうか（未確認。MCP で付けたら読み戻す）。
- オーナー自身のクライアントで `OnDeserialization` が呼ばれないこと（SDK 同梱サンプルの書き方からの推定）。
- `is` / `as` / `?.` / パターンマッチ / LINQ が U# で使えないこと（コンパイラの実装からの推定。実際のエラー表示は未確認）。
- Content Warnings が非推奨かどうか（`submitting-a-world-to-be-made-public.md` と `creating-your-first-world.md` で食い違う）。
- SDK の公開 API（`IVRCSdkWorldBuilderApi`）を MCP の execute_code から呼んだときの挙動。
- 同梱ギミックと `cct_*` ツールの Unity 実機での動作。

## 14. 還流先

- SDK・Unity の版と恒久的な SDK の事実：`vrchat-unity-version-check` のスナップショット節（正はそちら。ここには要点だけ）。
- MCP for Unity の tool の仕様・接続の不具合：`unity-mcp-operations` の verified-facts。
- `cct_*` ツールの仕様と出力：`claude-creator-tools`。
- ワールド制作の手順・ワールド固有の失敗：この台帳。
