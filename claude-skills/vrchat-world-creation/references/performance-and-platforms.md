# 負荷とプラットフォーム別の制約

出典と時点：数値と条件は **SDK 3.10.5 と VRChat 公式クリエイタードキュメントの 2026-10-20 時点のスナップショット** によるもの。`docs/…`・`releases/…` は GitHub `vrchat-community/creator-docs` の `Docs/` 以下、`Packages/…` は SDK パッケージ内のファイル（[セットアップ](setup-and-upload.md) 冒頭の略記と同じ）。

公式 docs にライトプローブ・反射プローブ・ライトマップ解像度の**具体的な数値指針は無い**。この章で「Unity 一般」「コミュニティ」と書いたものは、Unity の一般的な手順か、コミュニティ製の検査ツール VRWorld Toolkit 3.5.1（MIT、非公式）の検査項目から取った参考で、VRChat の公式基準ではない。

## 目次

1. 公式の性能指針
2. ライティングとライトマップ
3. オクルージョンカリング
4. ミラー
5. 動画プレイヤー
6. オーディオ
7. ポストプロセス
8. テクスチャ・メッシュ・マテリアル・UI・パーティクル
9. サイズ上限
10. Android / iOS の制約
11. アップロード前の点検リスト
12. `cct_world_audit` との対応

---

## 1. 公式の性能指針

`docs/worlds/submitting-a-world-to-be-made-public.md` L35-46（Community Labs / Public の基準として書かれている）：

- スポーン地点で VR 1人のとき **45 FPS 以上**。
- シェーダーは VR の **Single-Pass Stereo** に対応していること（SDK もビルド時に Single Pass を強制する）。
- Android ではモバイルシェーダーを使う。
- ワールド内の UI は Supersampled UI シェーダー（SDK が UI/Default を見つけると提案する）。
- ポストプロセスは慎重に。特に Chromatic Aberration、SSR、SSAO。
- **動画プレイヤーを1部屋に3台以上置くと悪影響**（"more than 2 video players"）。
- **ミラーを1部屋に2枚以上置くと悪影響**。1枚でもトグルにし、**既定 OFF を強く推奨**（トグルかエリアに入ったら ON）。
- リアルタイムライトを多用しない。**ライトのベイクは非常に重要**。
- ライティングは Standard/PBR シェーダーで確かめる（Toon では正しく見えない）。

SDK はポリゴン数・マテリアル数・ライト数・ミラー数・動画プレイヤー数・ベイクの有無・オクルージョンの有無を検査しない（[セットアップ](setup-and-upload.md) 4.6）。これらは自分で確かめる。

---

## 2. ライティングとライトマップ

### 2.1 公式に書かれていること

- Android ではベイクが**必須**。リアルタイムライトは「切り捨ててもよい」ほど高価。ベイクとライトプローブを多用し、ライトマップ解像度は低めでよい（`docs/platforms/android/quest-content-optimization.md` L110-114）。
- Lighting の **Auto Generate**（`GIWorkflowMode.Iterative`）はビルドを止める恐れがあり、SDK が Warning を出す（Fix で OnDemand。`PanelBuilder` L596-617）。
- 新規の既定シーンの Directional Light は **Realtime・Soft Shadow**（`Packages/com.vrchat.worlds/Editor/VRCSDK/SDK3/VRCDefaultWorldScene.unity`）。そのままにしない（Baked か Mixed に変えてベイクする。これは推奨で、SDK の要求ではない）。
- `VRChat/Mobile/Standard Lite`（3.7.0 で大幅更新）：非線形のライトプローブサンプリング、反射プローブが無いときはライトプローブから鏡面反射、**Bakery の MonoSH ライトマップに対応**（ベイクには外部の Bakery が要る。`releases/release-3.7.0.md` L34-40）。Standard Lite はリアルタイムライトに対応しないので PC では非推奨（`docs/platforms/android/quest-content-limitations.md` L29）。3.9.0 で「Standard Lite を使うとライトマップのベイクが終わらない（compiling shaders… のまま）」不具合が直った（`releases/release-3.9.0.md` L80-81）。
- 3.10.5：`VRCQualitySettings.ShadowmaskMode` で PC の **Distance Shadowmask**（リアルタイム影＋ベイク影の混合）を Udon から有効にでき、`VRCQualitySettings.RealtimeReflectionProbes` で全プラットフォームのリアルタイム反射プローブを有効・無効にできる（`releases/release-3.10.5.md` L27-28、`docs/worlds/udon/vrc-graphics/vrc-quality-settings.md` L17-60）。VRCWorld プレハブの WorldQualitySettings の既定は Shadowmask、リアルタイム反射 PC 有効・モバイル無効（[セットアップ](setup-and-upload.md) 2.5）。

### 2.2 ベイクの手順（Unity 一般＋VRChat 固有の注意）

1. **方針を決める**：PC だけか、モバイルにも出すか。モバイルにも出すならベイク前提・ライトマップ解像度低め・リアルタイムライト無しで設計する。
2. **ライト**：動かない照明は Baked、動く物にも直接光と影が要るものだけ Mixed、Realtime は最小限（Unity 一般）。Mixed も実行時にリアルタイムの計算が残る（Unity の仕様。Shadowmask と Distance Shadowmask の違いは Unity マニュアルで確かめる）。
3. **静的オブジェクト**：動かない床・壁・家具を Contribute GI（Static）にする。ライトマップ用 UV（UV2）が無いモデルは FBX の Import 設定「Generate Lightmap UVs」を ON（Unity 一般。UV2 無しの検査は VRWorld Toolkit の項目＝コミュニティ）。
4. **ライトプローブ**：Light Probe Group を、プレイヤー・Pickup が通る高さと明暗の境目に置く。ライトプローブが無いと、ベイクした光がプレイヤーや Pickup に当たらず、ミラーに映る自分も暗くなる（コミュニティ）。プローブを足したら焼き直す。
5. **反射プローブ**：反射する材質がある部屋に Baked の Reflection Probe を置く。焼いていない反射プローブ、Baked/Mixed なのに焼いていないライト（ゲーム内でリアルタイム扱いになる）、Environment Lighting の Ambient Mode = Custom（全プローブが黒になる）は VRWorld Toolkit が検査する（コミュニティ）。
6. **Lighting 設定**：Auto Generate を OFF にし、Lighting ウィンドウの Generate Lighting で焼く（Unity 一般）。4096 のライトマップを GPU Progressive で焼くには 12GB 超の VRAM が要り、足りないと CPU にフォールバックする（コミュニティ）。
7. **焼いた後**：`cct_world_audit` で未ベイク・古いライトマップ・UV2 無し・プローブ無しを確かめ、`cct_capture_views` で同じ視点の前後を撮って比べる。`cct_capture_views` の既定の撮り方は対象を複製して中立の照明で撮る（ライトマップの参照を外し、ポストプロセスも無い）ので、ライティングの比較は実際のシーンで撮る設定にする（設定の名前は `claude-creator-tools`）。
8. **モバイル**：Android のライトマップには ASTC 4x4 block のプラットフォーム別オーバーライドが推奨されている（コミュニティ）。

MCP での注意：ベイクは長時間かかる。1回の MCP 呼び出しで同期的に焼かない（既定30秒でタイムアウトしても処理は続くので、再送すると二重に実行される恐れがある）。既定は「本人に Generate Lighting を押してもらい、終わったら Claude が読み戻して確かめる」。非同期のベイク API を `execute_code` から呼ぶ手順は未検証。焼き直しで前回のライトマップが上書きされるので、前の結果を残したいときは事前に `cct_snapshot` で退避する（ライトマップの保存場所は Unity 一般の仕様で、このスキルでは未検証）。

### 2.3 色と明るさ

- 白飛びは、ライトが多すぎる・強すぎる、またはトーンマップの検討が要る（`docs/worlds/submitting-a-world-to-be-made-public.md` L52）。
- 環境光が暗すぎるとアバターが不自然に見える。Environment Lighting は Color より Gradient が推奨（コミュニティ）。
- ワールドにトゥーンシェーダーを使わず Standard を使う、という推奨がある（コミュニティ）。

---

## 3. オクルージョンカリング

- Android ではオクルージョンカリングのベイクが「非常に重要」。そのためにメッシュを結合しすぎない（建物と地面は分ける。`docs/platforms/android/quest-content-optimization.md` L116-122, L66-88）。
- `VRCCameraSettings.UseOcclusionCulling` の既定は true で、**ベイク済みのデータがあるときだけ効く**（`docs/worlds/udon/vrc-graphics/vrc-camera-settings.md` L68）。
- 手順（Unity 一般）：遮る大きな物を Occluder Static、隠される物を Occludee Static にし、Occlusion Culling ウィンドウで Bake する。プレイヤーが行ける範囲に Occlusion Area を置く（Occlusion Area が無い・Is View Volume が OFF・キャッシュの肥大は VRWorld Toolkit の検査項目＝コミュニティ）。
- ミラーの Turn Off Mirror Occlusion（既定 ON）は、ミラー内で物がちらつくときのための設定（`docs/worlds/components/vrc_mirrorreflection.md` L15）。
- ベイクはライトと同じく本人の操作を既定にする。

---

## 4. ミラー（VRC Mirror Reflection）

- コスト対策（`docs/worlds/components/vrc_mirrorreflection.md` L24-27）：既定 OFF、近づいたら自動で ON か手動トグル、映す層を絞る（高品質/低品質の2種）、それでも重ければ解像度を下げる。同梱の `CctMirrorSwitcher`（HQ/LQ/OFF、既定は全部 OFF）が使える（[レシピ](gimmick-recipes.md)）。
- SDK の `VRCMirror.prefab`（`Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VRCMirror.prefab`）：GameObject の層は Water（4）、Reflect Layers は Everything、Mirror Resolution は Auto、Disable Pixel Lights ON、Turn Off Mirror Occlusion ON、組み込みの Quad。
- Reflect Layers に関係なく、**Water（4）と PlayerLocal（10）は常に除外**、HardwareObjects（20）は常に追加される（SDK の `VRC_MirrorReflection` の実装。クライアントも同じと推定）。自分を映すには MirrorReflection（18）を含める。アイテムを映すには Item（3）を含める（`docs/worlds/items.md` L26-36）。
- 映す層の例（コミュニティ製 VRWorld Toolkit のプリセット）：プレイヤーだけ＝Player（9）＋MirrorReflection（18）、ビット値 262656。プレイヤーとワールド＝さらに Default（0）＋Environment（11）、264705。UiMenu を含めると VRChat の UI が二重に描かれて重い。
- Mirror Resolution の Auto は HMD/モニターの解像度で最大 2048×2048（per eye）。ユーザー設定で Unlimited にもできる（同 docs 脚注）。
- **同じ GameObject に Renderer とマテリアルが必要**。無いとミラーは自分を無効化する。SDK 3.10.5 の実装はシェーダーを `FX/MirrorReflection`（か Custom Shader）に差し替え、反射像を `_ReflectionTex0` / `_ReflectionTex1`（左右の目）に書く。docs の「`_MainTex` に書き込む」は古い。自作ミラーシェーダーは `_ReflectionTex0/1` を読む必要がある。
- Camera Clear Flags を Custom Skybox にして Skybox を指定しないと背景は黒（docs）。
- ミラー本体の層が描画中のカメラの cullingMask に入っていないと、ミラーの処理自体が飛ばされる（SDK コードからの推定）。
- 3.10.2 以降、VRCMirrorReflection は元のマテリアルを書き換えずインスタンスを作る（`releases/release-3.10.2.md` L40）。

---

## 5. 動画プレイヤー

`docs/worlds/udon/video-players/index.md`：

- 使うのは VRChat のラッパーだけ：`VRCAVProVideoPlayer`（ライブ配信対応、**エディタでは再生されない**→ Build & Test で確かめる）と `VRCUnityVideoPlayer`（Play Mode で mp4/webm の直リンクは再生可、YouTube 等はクライアントでだけ、ライブ非対応）（L3, L24-29）。素の Unity `VideoPlayer` や AVPro の `MediaPlayer` は SDK3 の許可リストに無く、クライアントで除去されると推定。
- **レート制限：1ユーザーは新しい URL を5秒に1回まで**（全プレイヤー共通）。複数のプレイヤーや後から入った人が同時に読み込むと失敗する（L38-44）。自動再生のプレイヤーを複数置かない。
- mp4 は fast start（`-movflags +faststart`、HandBrake の Web Optimized）（L78-83）。
- 許可ドメイン（`docs/worlds/udon/video-players/www-whitelist.md` L26-40）：YouTube（`*.youtube.com`, `youtu.be`）、`*.googlevideo.com`、Twitch、VRCDN、Vimeo、NicoNico（`*.nicovideo.jp`）、Soundcloud、Mixcloud、Facebook、Topaz Chat、Hyperbeam、Youku、Akamai など。**Public / Group Public インスタンス**では、許可リスト外のドメインは VRChat.com の「Video Player Allowed Domains」に登録し、かつ視聴者が「Allow Untrusted URLs」を有効にしている場合だけ再生される（L42-58、3.7.5 から）。
- **Android の動画は HTTPS 必須**（`www-whitelist.md` L10）。
- SDK のサンプル同期プレイヤー（`UdonSyncPlayer (AVPro)` / `(Unity)`、`docs/worlds/sdk-prefabs.md` L37-44）は、インスタンスマスターが Allow Untrusted URLs を無効にしていると再生できないケースを扱っていない（`www-whitelist.md` L12-15）。コミュニティ製（VideoTXL、ProTV、USharpVideo）が docs で紹介されている（`index.md` L5-9）。
- 既定値：`autoPlay` true、`loop` true、`maximumResolution` 720（3.10.5 の SDK コード）。

---

## 6. オーディオ（VRC Spatial Audio Source）

- AudioSource に `VRC_SpatialAudioSource` を付けて 3D 空間化する。無いと SDK が Warning を出す（Fix で追加。2D 判定の音には無効状態で足す）。ONSPAudioSource は非推奨（Fix で変換）。`autoSpatializeAudioSources` は非推奨。
- 既定値：Gain **10**（dB、0〜24）、Far **40**m、Near 0、Volumetric Radius 0、Enable Spatialization ON（`docs/worlds/components/vrc_spatialaudiosource.md` L70-77）。実行時のアニメーションでの変更は非対応（初期化時に決まる）。
- BGM などの 2D 音は Enable Spatialization を OFF にし、AudioSource の Spatial Blend を 2D にする。ただし docs は推奨していない（L36-42）。部屋ごとの BGM は同梱の `CctAreaToggle`、音量は `CctAudioVolume` が使える。
- デバッグビューの Audio Sources はワールド作者か World Debugging 有効時だけ見られる（`docs/worlds/udon/world-debug-views.md` L101-123）。
- Android の AudioSource は「restricted」とだけ書かれ、中身は docs に無い（未確認）。

---

## 7. ポストプロセス

- 使えるのは Post Processing Stack **v2** の `PostProcessLayer` / `PostProcessVolume` / `PostProcessDebug` だけで、**PC のみ**（`docs/worlds/whitelisted-world-components.md` L171-178、SDK の `WorldValidation.cs` L48-52）。**Android/Quest では完全に無効**（`docs/platforms/android/quest-content-limitations.md` L52）。
- Reference Camera に PostProcessLayer を付ける。TAA は非対応（SDK Warning）。Volume は Reference Camera と別の GameObject に置く。
- VRChat は Forward レンダリング（SDK が強制）。避けたい効果：Chromatic Aberration、SSR、SSAO（公式）。PP の Ambient Occlusion は Forward では画面全体に乗って重い、SSR は Forward では機能しない、Depth of Field は既定 OFF、Tonemapper（Neutral/ACES）推奨、Bloom の intensity 0.01〜0.3・threshold は理想 0 で 1.0 未満、Bloom Dirt 非推奨、Vignette は控えめ、Volume Blending Layer は予約層を避ける（ここまでコミュニティ）。

---

## 8. テクスチャ・メッシュ・マテリアル・UI・パーティクル

- テクスチャの最大サイズ 8192 超は SDK Warning（Fix で 8192）。ミップの Box フィルタは Info（Kaiser 推奨。DPID を有効にしていれば DPID）。DPID は 3.7.1 のオプトイン機能で、透過テクスチャは Alpha is Transparency を ON（`releases/release-3.7.1.md` L22-27）。
- モバイルは 1024 を上限の目安にし、アトラス化する（`docs/platforms/android/quest-content-optimization.md` L100-106）。Crunch はダウンロードサイズにだけ効き、メモリには効かない（L36-40）。
- マテリアルは「物」単位でまとめる（まとめすぎるとカリングとバッチが悪化する）。**全マテリアルで Enable GPU Instancing を ON**（L78-98）。
- UI：TextMeshPro 推奨、Supersampled UI シェーダー推奨、UI 要素の Navigation = None、ScrollRect の Scroll Sensitivity = 0（`docs/worlds/components/vrc_uishape.md` L36-37, L66-69）。SDK 同梱のシェーダー名は `VRChat/Mobile/Worlds/Supersampled UI`。
- パーティクル：Billboard の Allow Roll は OFF 推奨（3.10.2 で SDK のヒント追加）。
- PhysBone / Contact はワールドでも使える（3.10.0〜）。同時に有効なのは PhysBone+Collider で 1024、Contact で 1024 まで（`docs/worlds/components/index.md` L32-39）。

---

## 9. サイズ上限

| 対象 | PC | Android / iOS |
| --- | --- | --- |
| ワールド（圧縮後） | 上限なし | **100MB（104,857,600 バイト）。超えるとアップロードを拒否**、Android ではアクセスもできない |
| ワールド（非圧縮） | 上限なし | 上限なし |
| Community Labs / Public の推奨 | **200MB 未満** | 同左 |

出典：SDK の `VRC.ValidationHelpers.GetAssetBundleSizeLimit`（`Packages/com.vrchat.base/Runtime/VRCSDK/Plugins/VRCSDKBase.dll`）、`WorldBuilder` L2823-2836、`docs/platforms/android/quest-content-optimization.md` L42-44、iOS は Android と同じ制限（`docs/platforms/iOS/setting-up-unity-for-creating-ios-content.md` L38）、`docs/worlds/submitting-a-world-to-be-made-public.md` L25。

- 判定は `サイズ > 上限`（100MB ちょうどは可）。検証欄では Warning、Build & Publish で拒否、Build & Test はできる。
- サイズの確認：ビルド後に Editor.log で「statistics」を探し、pre-compressed size を見る（`quest-content-optimization.md` L36）。`cct_world_audit` は直近ビルドのファイルサイズから判定する。
- その他の上限：String Loading は1文字列 100MB・5秒に1回（`docs/worlds/udon/string-loading.md` L6-8）、Image Loading は最大 2048×2048・5秒に1回（`docs/worlds/udon/image-loading.md` L20-28）、Persistence は 1ワールド・1プレイヤーあたり PlayerData 100KB＋PlayerObject 100KB（圧縮後、`docs/worlds/udon/persistence/index.md` L41-47）。

---

## 10. Android / iOS の制約（ワールド）

`docs/platforms/android/quest-content-limitations.md`、`quest-content-optimization.md`。iOS は Android と同じ制限（`docs/platforms/iOS/setting-up-unity-for-creating-ios-content.md` L38）。

| 項目 | ワールドでの扱い |
| --- | --- |
| シェーダー | **制限なし**（アバターはモバイルシェーダーのみ）。ただし性能優先。基本は `VRChat/Mobile/Lightmapped`＋ベイク（docs 本文の `Mobile/VRChat/Lightmapped` は誤記）。SDK は Renderer のシェーダーが許可リスト外だと**警告**する（止めない） |
| 透明 | 避ける。アルファカットアウトも透明に数える |
| ポストプロセス | **完全に無効** |
| ライト | ワールドでは使えるが、リアルタイムは非常に高価。ベイク必須 |
| カメラ | 使えるが使いすぎない |
| Rigidbody / Collider / Joint | 使えるが使いすぎない |
| Cloth | **完全に無効** |
| Unity Constraints | 使えるが重い |
| AudioSource | restricted（中身は未記載） |
| 動画 | 制限付きで可。URL は HTTPS 必須 |
| ポリゴン | ワールド全体で**約 250,000 トライアングル**を予算に |
| テクスチャ | 最大 1024 を目安、アトラス化、圧縮は **ASTC 必須** |
| マテリアル | GPU Instancing を ON |

- SDK のモバイル向けシェーダー許可リスト（`Packages/com.vrchat.base/Runtime/VRCSDK/Dependencies/VRChat/Scripts/Validation/WorldValidation.cs` L531-546）：`VRChat/Mobile/Standard Lite`、`Diffuse`、`Bumped Diffuse`、`Bumped Mapped Specular`、`Toon Lit`、`MatCap Lit`、`Lightmapped`、`Skybox`、`Particles/Additive`、`Particles/Multiply`、`VRChat/Mobile/World/Supersampled UI`、`FX/MirrorReflection`、`UI/Default`。
  - 判定は **Renderer の共有マテリアルのシェーダー名の完全一致だけ**で、Canvas 上の UI（Graphic）は対象外。リストの `World/Supersampled UI` は同梱シェーダーの実名 `Worlds/Supersampled UI` と一致しないので、MeshRenderer に使うと警告が出る。`Toon Standard`（3.8.1 追加）、TextMeshPro（3D）のシェーダー、`Particles/Alpha Blended`、Unity Standard も Renderer に付いていれば警告（いずれも止めない）。
- GrabPass は Android で表示が崩れる、Android で使えない圧縮形式のテクスチャは実機で黒くなる（コミュニティ）。
- `VRCGraphics.Blit` は Quest の GPU では `ZTest Always` か RT の depth OFF が要る（`docs/worlds/udon/vrc-graphics/index.md` L33-41）。
- モバイルの操作：`OnInputMethodChanged` / `InputManager.GetLastUsedInputMethod()` で Touch を判定、大きい文字とボタン、スクリーンスペース UI も検討（`docs/platforms/android/android-best-practices.md` L33-115）。

---

## 11. アップロード前の点検リスト

SDK が止める項目（[セットアップ](setup-and-upload.md) 4章）を先に、次に公式推奨を見る。

- [ ] Unity 2022.3.22f1、SDK 3.10.x、コンパイルエラー0（Editor.log の `error CS` と U# のエラー）
- [ ] 読み込み中のシーンは1つ（またはアップロードするシーンだけに Descriptor）。Descriptor・PipelineManager がちょうど1つ、同じ GameObject
- [ ] 有効なスポーンが1つ以上、真下に床、Respawn Height Y はスポーンより下、Spawn Radius 0.2 程度
- [ ] Setup Layers / Setup Collision Matrix 済み。独自レイヤーは 22〜31
- [ ] Reference Camera 設定済み（Near 0.001〜0.05）。PP を使うなら PostProcessLayer があり TAA でない
- [ ] VRCObjectSync と Manual 同期 / ObjectPool を同じ GameObject に置いていない
- [ ] Network ID の割り当てエラーが無い（Network ID Utility）
- [ ] Missing Script・ピンク（エラーシェーダー）が無い。許可リスト外のコンポーネントが無い
- [ ] ライトをベイクした。リアルタイムの影付きライトは最小限。ライトプローブ・反射プローブを置いた
- [ ] オクルージョンをベイクした（特にモバイル）
- [ ] ミラーは既定 OFF、1部屋1枚、映す層を絞った。動画プレイヤーは1部屋2台まで、自動再生は1台
- [ ] AudioSource に VRC_SpatialAudioSource
- [ ] UI：VRCUiShape、層は UI 以外、World Space、EventSystem、Navigation None
- [ ] モバイル：ASTC、100MB 以下、PP・透明・リアルタイムライト・Cloth に頼っていない、約 25万トライ、テクスチャ 1024 目安、HTTPS の動画
- [ ] 新規ワールドなら名前とサムネイルを本人が用意。既存の更新なら Blueprint ID を保った
- [ ] ClientSim → Build & Test（Clients 2 以上）で同期と後から入った人を確かめた（[テストとデバッグ](testing-and-debugging.md)）

---

## 12. `cct_world_audit` との対応

`cct_world_audit` は、上の内容のうちエディタで静的に判定できるものを1回で調べる読み取り専用のツール（ビルド・アップロード・ClientSim・VRChat API には触れない）。実装中のため、項目コード・重大度・引数は変わり得る。**正は `claude-creator-tools` スキル**で、下の表は 2026-10-25 時点の実装（パッケージ 0.1.0、Unity 実機で未検証）のコメントから取った対応の目安。

| この資料の項目 | 主な項目コード（目安） |
| --- | --- |
| Descriptor・PipelineManager・スポーン | `NO_SCENE_DESCRIPTOR`、`MULTIPLE_SCENE_DESCRIPTORS`、`PIPELINE_MANAGER_MISSING`、`MULTIPLE_PIPELINE_MANAGERS`、`NO_SPAWNS`、`SPAWN_TOO_FAR`、`RESPAWN_HEIGHT_ABOVE_SPAWN`、`SPAWN_NO_GROUND`、`SPAWN_RADIUS_ZERO` |
| Reference Camera・PP | `REFERENCE_CAMERA_MISSING`、`REFERENCE_CAMERA_NEAR_CLAMPED`、`REFERENCE_CAMERA_TAA`、`PP_VOLUME_ON_MAIN_CAMERA`、`PP_LAYER_MISSING` |
| レイヤー | `LAYERS_NOT_SETUP`、`COLLISION_MATRIX_NOT_SETUP`、`RESERVED_LAYER_USED` |
| ライティング（2章） | `LIGHTMAP_NOT_BAKED`、`LIGHTMAP_OUTDATED`、`LIGHTMAP_UV_MISSING`、`REALTIME_SHADOW_LIGHTS`、`BAKED_LIGHT_NOT_BAKED`、`NO_LIGHT_PROBES`、`REFLECTION_PROBE_NOT_BAKED`、`LIGHTING_AUTO_GENERATE` |
| オクルージョン（3章） | `OCCLUSION_NOT_BAKED`（モバイルでは警告） |
| ミラー（4章） | `MIRROR_ENABLED_AT_START`、`MIRROR_MULTIPLE_ENABLED`、`MIRROR_NO_RENDERER`、`MIRROR_REFLECT_EVERYTHING`、`MIRROR_NO_LOCAL_PLAYER`、`MIRROR_LAYER_NOT_RENDERED` |
| 動画（5章） | `VIDEO_PLAYER_TOO_MANY`、`VIDEO_PLAYER_AUTOPLAY_MULTIPLE`、`VIDEO_PLAYER_URL_NOT_ALLOWLISTED`、`VIDEO_PLAYER_HTTP_MOBILE` |
| 音（6章） | `AUDIO_NO_SPATIAL`、`AUDIO_ONSP_DEPRECATED`、`AUTO_SPATIALIZE_DEPRECATED` |
| テクスチャ等（8章） | `LARGE_TEXTURE`、`TEXTURE_MIP_BOX_FILTER`、`MISSING_SHADER`、`UI_DEFAULT_SHADER`、`TMP_RECOMMENDED`、`PARTICLE_BILLBOARD_ROLL` |
| サイズ（9章） | `BUNDLE_TOO_LARGE_MOBILE`、`BUNDLE_LARGE_PC` |
| モバイル（10章） | `ANDROID_TEXTURE_FORMAT_NOT_ASTC`、`ANDROID_SHADER_NOT_SUPPORTED`、`ANDROID_POST_PROCESSING`、`ANDROID_REALTIME_LIGHT`、`ANDROID_LARGE_TEXTURE`、`ANDROID_TRIANGLE_BUDGET`、`ANDROID_UNSUPPORTED_COMPONENT`、`ANDROID_TRANSPARENT_MATERIALS`、`ANDROID_GPU_INSTANCING_OFF` |
| 許可リスト・欠落 | `NOT_WHITELISTED`、`MISSING_SCRIPT`、`MISSING_REFERENCE` |
| Udon / U# | `UDON_NO_PROGRAM`、`UDON_PROGRAM_NOT_COMPILED`、`OBJECT_SYNC_MANUAL_UDON`、`OBJECT_SYNC_WITH_POOL`、`USHARP_SCRIPT_NOT_IN_USHARP_ASSEMBLY`、`EDITOR_LOG_USHARP_ERRORS` |

- 結果は段階で言えば「エディタの静的確認」。合格でもビルド成功・実機での動作の証明ではない。結果の「確認していないこと」の欄をそのまま報告に入れる。
- 判定の根拠の種類（SDK と同じ判定／docs の推奨／コミュニティ知見／ツールの推定）が項目ごとに付く。SDK と同じ判定の error を先に直し、コミュニティ・推定の項目は本人と相談して扱う。
