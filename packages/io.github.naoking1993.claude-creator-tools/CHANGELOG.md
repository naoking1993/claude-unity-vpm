# 変更履歴

このパッケージの変更点を記録します。版の付け方は [Semantic Versioning](https://semver.org/lang/ja/) に従います。0.x のあいだは、issue コード・引数・出力のキーが変わることがあり、変わったときはここに書きます。

## [0.1.2] - 2026-10-25

0.1.1 を同じワールド「泡ワールド」（Unity 2022.3.22f1・VRChat SDK 3.10.5）で再監査しました（2026-10-25 22:39）。0.1.1 で直した 9 項目と PhysBone の数え方はすべて期待どおりで、0.1.0 の結果 warning 11 / info 15 が warning 6 / info 16 になりました（warning のうち 1 件は、シーンに一時的に現れた VizVid の物による `MISSING_REFERENCE` です。シーンを開き直した後は warning 5）。0.1.0 で正しかった 16 件のうち 14 件は前後で同じ結果でした。残りの `SCENE_UNSAVED` と `UDON_PROGRAM_SOURCE_MISSING` は、どちらの版でも出ない状態だったため確かめられていません。0.1.2 は、この再監査の検証で見つかった細かいずれの修正です。機能の追加はありません。**0.1.2 自体はまだ Unity の中で実行していません**（Unity 2022.3.22f1 の DLL に対するコンパイルと、オフラインの確認だけです）。

### 修正

- `NOT_WHITELISTED`: iwaSync3 の `DesktopBar` / `Queuelist` / `ListTab` も、`IwaSync3BuildProcessor` がビルド時に削除する型として扱う（`reason`: `stripped_at_build`）。0.1.1 ではこの 3 つが既知の型に無く、「推定」の文面の info になっていました。
- `NOT_WHITELISTED`: 削除の前に設定をどこへ写すかを、型ごとに正しく書く。`Speaker` は Udon ではなく、子の AudioSource と VRC Spatial Audio Source に写します（Live 用なら VRC AVPro Video Speaker も付けます）。`IwaSync3` は子の Udon と VRC 動画プレイヤーに写します。
- `VIDEO_PLAYER_NO_AUDIO`: ビルド時に Unity プレイヤーへ入る Speaker を、iwaSync3 自身の決め方（`GetMainIwaSync3`）で数える。順番は、親にある IwaSync3、シーンに IwaSync3 が 1 つだけならそれ、Speaker の `iwaSync3` 参照です。IwaSync3 の `iwaSync3` 参照はメインまでたどります。0.1.1 はその iwaSync3 を直接参照する Speaker だけを数えていたので、参照が空の Speaker や、サブの iwaSync3 を指す Speaker では、ビルド時に音が入るのにこの項目が出ることがありました。AudioSource の無い Speaker は数えません。
- `AVATAR_PACKAGES_IN_WORLD_PROJECT`: 入っている他のパッケージから必要とされていないものは「単独で VCC から外せます」と書く。0.1.1 では、どれか 1 つにでも依存元があると、この案内が消えていました。

### 確認したが変えなかったもの

- `ANDROID_LARGE_TEXTURE` の Non-Power of 2 の丸め（`npotScale`）は、Sprite / Editor GUI 型でも同じように当てます。Unity 2022.3.22f1 で試すと、600×300 の Sprite・Editor GUI は、`npotScale` が ToNearest なら 512×256 で取り込まれました。型を Sprite / GUI に変えると `npotScale` が None になり、そのときは 600×300 のままです。どちらの場合も `npotScale` を読めば実際の取り込みと合います。

## [0.1.1] - 2026-10-25

0.1.0 の初回確認（2026-10-25、Unity 2022.3.22f1・VRChat SDK 3.10.5。アバター「ひかるん」・ワールド「泡ワールド」）で、`cct_world_audit` の 28 件をシーン・SDK・同梱パッケージのソースと突き合わせ、誤検知と不正確な文面を直しました。機能の追加はありません。**0.1.1 自体はまだ Unity の中で実行していません**（Unity 2022.3.22f1 の DLL に対するコンパイルと、オフラインの確認だけです）。

### 修正（誤検知）

- `NOT_WHITELISTED`: ビルド時にシーン処理（`IProcessSceneWithReport`）で消えるコンポーネントを warning にしていた。iwaSync3 の `IwaSync3` / `Playlist` / `Screen` / `Speaker` は、`IwaSync3BuildProcessor` が設定を Udon に写してから削除するので info（対応不要）にした。それ以外でも、同じパッケージのエディタ用アセンブリにシーン処理（`IProcessSceneWithReport` / `[PostProcessScene]`）がある型は、推定として info にした（`reason`: `stripped_at_build` / `scene_processor_may_strip`）。
- `NOT_WHITELISTED`: 子に VRChat で使われるコンポーネント（UdonBehaviour、AudioSource など）がある GameObject に「EditorOnly タグを付ける」と助言していた（子ごと消えてプレイヤーが壊れる）。その場合は付けないよう案内する（`shipping_component_in_subtree`）。
- `VIDEO_PLAYER_NO_AUDIO`: iwaSync3 はビルド時に Unity プレイヤーの Target Audio Sources へ Speaker を入れる（保存値は空のまま）ため、iwaSync3 の下のプレイヤーで、その iwaSync3 を指す Speaker があれば出さない（`video_players.items[].audio_sources_at_build`）。同じ GameObject の AVPro プレイヤーに Speaker がある場合は、文面を「Unity プレイヤーのモードのときだけ音が出ない可能性」に弱めた。

### 修正（文面・重さ）

- `MULTIPLE_SCENES_LOADED`: NDMF が自動で開くプレビューシーン（閉じられない）を数えない。追加のシーンに GameObject があるときだけ warning、空なら info。出どころを SDK から EST に直した（SDK 3.10.5 に複数シーンの検査は無い）。`scenes.loaded[].ndmf_preview` を追加。
- `STATION_NO_COLLIDER`: 別の GameObject の UdonBehaviour / U# がその VRC Station を参照していれば（例: 椅子の Interact から UseStation）出さない（`interactables.stations.no_collider_but_used_from_other_udon`）。文面を「このオブジェクト自体を Interact しても座れません」に直した。
- `PICKUP_NOT_SYNCED`: Udon が置き場所を決めているピックアップ（自分に UdonBehaviour があり開始時は掴めない、または祖先に Manual / Continuous の UdonBehaviour がある）を数えず、別枠にした（`interactables.pickups.not_synced_udon_controlled`）。助言に「Udon の位置制御と競合することがある」を足した。
- `LIGHTMAP_UV_MISSING`: UV2 が無くても、UV0 が 0〜1 に収まった重なりの無い展開（Unity 組み込みの Quad など）なら出さない（`lighting.lightmaps.uv0_fallback_ok`）。文面の「UV1 で代用」を「UV0 (1 番目の UV)」に直し、組み込みメッシュには Generate Lightmap UVs が無いことを書いた。
- `ANDROID_LARGE_TEXTURE`: Non-Power of 2（`npotScale`）の丸めを反映してから 1024 px と比べる（2 の累乗でない 1300〜1440 px 程度の画像は 1024 で取り込まれるため数えすぎていた）。
- `UI_NO_EVENT_SYSTEM`: warning → info。ClientSim は自前の EventSystem を使い、VRChat クライアントでも EventSystem の無いワールドで UI が動いた例があるため、「クライアントで UI が反応しないときだけ確認」と書いた。
- `AVATAR_PACKAGES_IN_WORLD_PROJECT`: どのパッケージがそれを必要としているか（`package.json` の `dependencies` / `vpmDependencies`）を示し（`project.avatar_packages_required_by`）、理由を「それを使うアバター用プラグインが API と合わずにコンパイルできないと Assembly-CSharp が作られない。Editor.log の error CS を確認」に直した。

### 変更（出力）

- `interactables.dynamics.in_scope_shipped` を追加。`counts.physbones` などと同じ数え方（scene の範囲、EditorOnly を除く）の PhysBone / コライダー / Contact の数です。`dynamics.physbones` などは従来どおり SDK と同じく読み込み中の全シーン（EditorOnly を含む）を数えます（0.1.0 では両者が食い違って見えた）。

## [0.1.0] - 2026-10-25

最初の版です。**Unity の中では一度も実行していません。** オフラインでのコンパイル（Unity 2021.1 の参照 DLL、MCP for Unity 9.7.1 のソースから作ったスタブ）と、VRChat SDK 3.10.5 の DLL に対するフィールド名の照合、ワールドギミックの U# 静的チェックだけを行っています。導入したら README の「初回確認」を行ってください。

### 追加

- MCP for Unity（9.7.1 以上）用の MCP ツール 8 個。
  - `cct_environment`: 接続中のプロジェクト・シーン・VRChat 関連パッケージ・アバターとワールド記述子の確認（読み取り専用）
  - `cct_avatar_audit`: アバターのアップロード前監査。PC / モバイルの性能ランク（SDK の計算器を使えるときは SDK と同じ数値）、SDK がアップロードを止めるエラー、Missing Script、Write Defaults、Expression。`post_build` で NDMF 処理後の一時クローンも監査（読み取り専用）
  - `cct_world_audit`: ワールドシーンのアップロード前点検。SDK・UdonSharp が止める項目（`verdict`）、ライティング、ミラー、Udon、動画プレイヤー、Android/iOS、Editor.log のコンパイルエラー（読み取り専用）
  - `cct_capture_views`: 決まった角度からの PNG 撮影、一覧画像、同じ条件での前後比較（`Library/ClaudeCreatorTools/Captures` にだけ書く）
  - `cct_snapshot`: ファイル単位のスナップショット（`.meta` 込み）、差分、復元、削除。復元・削除は `confirm: true` のときだけ実行し、復元の前に現状を自動で退避
  - `cct_animator_inspect`: AnimatorController のダンプと VRChat 向けの検査、パラメーター・Binding の使用箇所の検索（読み取り専用）
  - `cct_dependency_report`: 依存関係・使用元の逆引き・シーン外参照のレポート（読み取り専用）
  - `cct_world_gimmicks`: ワールドギミックのサンプルの一覧と取り込み。取り込みは `confirm: true` のときだけ
- `Tools > Claude Creator Tools` のメニュー。MCP ツールと同じ処理を、MCP for Unity が無いプロジェクトでも実行できます。ダイアログは出しません。
- ワールドギミックのサンプル（`Samples~/WorldGimmicks`、UdonSharp）: `CctLocalToggle`、`CctGlobalToggle`、`CctMirrorSwitcher`、`CctTeleporter`、`CctPickupResetter`、`CctAudioVolume`、`CctAreaToggle`、`CctPlayerCounter`。

### 既知の制限

- Unity 2022.3 の実機、ClientSim、VRChat での動作は未確認です。
- 利用者の MCP サーバー（9.7.1 + FastMCP 4 改修版、Python 3.14）で、`cct_*` が単独のツールとして出るか、`execute_custom_tool` 経由になるかは未確認です。
- 詳しくは README の「既知の制限」を参照してください。
