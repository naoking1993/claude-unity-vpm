# ギミックの設置レシピ

出典と時点：同梱ギミックは Claude Creator Tools パッケージ（`io.github.naoking1993.claude-creator-tools` 0.1.0）の `Samples~/WorldGimmicks/README.md` と `Scripts/*.cs`（2026-10-24 時点）。SDK の部品は **SDK 3.10.5 と VRChat 公式クリエイタードキュメントの 2026-10-20 時点のスナップショット**（`docs/…` は GitHub `vrchat-community/creator-docs` の `Docs/docs/` 以下）。

同梱ギミックは **Unity 実機・ClientSim・VRChat では未検証**。検証したのは SDK 3.10.5 の実 DLL に対する C# 9 コンパイル、U# が扱えない構文の静的チェック、使っている API が Udon に公開されているかの照合だけ（README「制限事項」）。`cct_*` ツールの引数と出力は `claude-creator-tools` スキルを見る。ここでは目的と手順の順序だけを書く。

## 目次

1. 取り込みの流れ
2. 取り込み後にやること
3. 共通の設定（Interact・トリガー・UI）
4. ギミック別の設定（8種）
5. SDK の部品
6. MCP で設定・配線するときの注意

---

## 1. 取り込みの流れ（`cct_world_gimmicks`）

前提：**ワールドプロジェクト専用**。アバタープロジェクトには UdonSharp が無いので、取り込むとコンパイルエラーで全スクリプトが止まる。先に `cct_environment` などで UdonSharp（Worlds SDK 同梱）があることを確かめる。

1. **一覧と導入状況を見る**：UdonSharp の有無、各サンプルが使うパッケージ（TextMeshPro / uGUI / PlayerData / NetworkCallable）の有無、取り込み済みか、同じスクリプトが別の場所に無いか、取り込み先が使えるか。
2. **ドライラン**：書き込む予定のファイルと、作る予定の Program Asset だけを返させる。計画として本人に示す（何を・どこに・上書きするか）。
3. **取り込み（確定）**：既定の取り込み先は `Assets/ClaudeWorldGimmicks/`。ツールは次の場合に拒否・スキップする：UdonSharp が無い、選んだサンプルの依存パッケージが無い、同じスクリプトが別の場所にある（同じクラスが2つあるとプロジェクト全体がコンパイルエラー）、取り込み先が asmdef / asmref のあるフォルダ・`Editor` フォルダ・`Assets/Plugins`・`Assets/Standard Assets` の中（U# がコンパイルしない）。既存ファイルは既定で上書きしない。サンプル側に `.meta` があればコピーして GUID を保つ（無いファイルは Unity が新しい GUID を振る。同じ GUID を別のアセットが使っていればコピーせず警告）。一部だけ取り込むこともできる。
4. 取り込み後、Unity の再コンパイルが走る。コンパイル中の次の MCP 呼び出しは「エディタが忙しい」旨で返るので、少し待って読み直す。

他の取り込み方：Unity のメニュー `Tools > Claude Creator Tools > ワールドギミック (UdonSharp) > ClaudeWorldGimmicks にインポート (Assets 直下)`（1の確定と同じ）。Package Manager の「Samples > Import」は UdonSharp の有無を確かめず Program Asset も作らないので非推奨。

---

## 2. 取り込み後にやること（README「インポート後にやること」どおり）

1. **コンパイルを待つ。** Unity のスクリプトコンパイルに続いて UdonSharp が U# プログラムを自動でコンパイルする。Console に UdonSharp のエラーが出ていないことを確かめる（MCP では Editor.log の `error CS` と U# のエラーも見る）。
2. **Program Asset を確かめる。** `Assets/ClaudeWorldGimmicks/Scripts/` に各スクリプトと同名の `.asset`（UdonSharp Program Asset）が必要。UdonSharp は「Create > U# Script」で新規作成したときにしか Program Asset を作らない（既存の .cs をコピーしても作られない）。ツールかメニューで取り込めば自動で作る。無い場合は手動で作る：
   - Project ウィンドウで `Scripts` フォルダを右クリック → **Create > VRChat > Udon > Udon C# Program Asset**
   - 作った `.asset` を選択し、Inspector の **Source Script** に対応する `.cs` をドラッグ（名前はスクリプト名と同じにしておくと分かりやすい）
   - 1スクリプトにつき Program Asset は1つだけ（複数あると UdonSharp がエラーにする）
3. **コンポーネントを付ける。** ギミックを置く GameObject に Add Component でスクリプト名（例 `CctLocalToggle`）を追加する。Inspector に表示された時点で UdonSharp が裏の UdonBehaviour を用意する。MCP でコンポーネントを追加した場合は、その GameObject を選択して Inspector を表示するか、Play に入る・シーンを開き直すと UdonSharp が自動でセットアップする（Console の "has not been fully setup, running setup" は正常）。
4. 4章の表に従って、Inspector の項目・Collider・レイヤー・UI イベントを設定する。
5. ClientSim（Play）で確かめ、**VRChat SDK > Show Control Panel > Builder > Build & Test**（Number of Clients = 2）で同期を確かめる。

UdonSharp が自動でコンパイルしないときは **VRChat SDK > Udon Sharp > Refresh All UdonSharp Assets**、または任意の Program Asset の Inspector の **Utilities > Compile All UdonSharp Programs**。

---

## 3. 共通の設定

### 3.1 Interact（クリックで反応）させる物

- 同じ GameObject に **Collider** が要る（Is Trigger は OFF でよい）。`Interact` は Collider と UdonBehaviour の両方が要る（`docs/worlds/udon/graph/event-nodes.md` L15-19）。
- レイヤーは **Default（0）** か、ステッカーを貼らせたくなければ **Interactive（8）**。手前に Interact のレイを遮る Collider が無いこと（[セットアップ](setup-and-upload.md) 3.3）。
- 表示文字は裏の UdonBehaviour の Interaction Text（`interactText`、既定 "Use"）、届く距離は Proximity（`proximity`、既定 2、0〜100）。
- 同梱ギミックでは、スイッチ自身を「非表示にするリスト」に入れても無視される（二度と押せなくなるため）。スイッチの**親**をリストに入れるとスイッチごと消えるので入れない。

### 3.2 トリガー（踏む・エリアに入る）

- Collider の **Is Trigger を ON** にし、スクリプトと同じ GameObject に付ける。
- レイヤーは **MirrorReflection（18）** を推奨（プレイヤーとトリガー判定し、Interact のレイを遮らない）。Mesh Renderer は付けない（MirrorReflection の描画はミラーにだけ映る）。
- テレポートや高速移動では Enter/Exit が取りこぼされることがある（`docs/worlds/udon/players/player-collisions.md` L20-23。`CctAreaToggle` は対策済み）。
- トリガーの層はローカルプレイヤー（PlayerLocal 10）と衝突する層にする。Walkthrough（17）などプレイヤーと衝突しない層では `OnPlayerTriggerEnter` が来ない（衝突マトリクスは [セットアップ](setup-and-upload.md) 3.2、同梱 README のトラブルシューティング）。
- Udon は Start より前に届いたイベントを捨てる。最初は非アクティブな GameObject のスクリプトは、アクティブになって Start が走るまでイベントを受け取らない。

### 3.3 UI（Canvas）から呼ぶ

- Hierarchy の右クリック → **UI > TextMeshPro (VRC)** などで作ると、VRChat 用の設定（VRC Ui Shape 等）が自動で付く（`docs/worlds/components/vrc_uishape.md` L18）。
- 手動なら：Canvas に **VRC Ui Shape**、Render Mode = **World Space**、レイヤーを **UI から Default に変える**（UI レイヤーはメニューを開いている時しか操作できない）、スケール 0.01 程度、各 UI 要素の Navigation = None、シーンに EventSystem、Canvas に GraphicRaycaster。
- ボタンの配線：Button の **On Click ()** の **+** → スクリプトを付けた GameObject をドラッグ → **UdonBehaviour > SendCustomEvent (string)** → メソッド名（例 `_Toggle`）を入力。
- UI のイベントから Udon に値は渡せない（UnityEvent で UdonBehaviour に呼べるのは `SendCustomEvent`・`RunProgram`・`Interact` と `enabled` だけ。`Packages/com.vrchat.worlds/Runtime/VRCSDK/SDK3/UnityEventFilter.cs` L645-657）。スライダーは `_OnSliderChanged` を呼び、スクリプト側でスライダーの値を読む。
- `_` で始まる公開メソッドは「ローカル専用」の印（ネットワークイベントとしては呼ばれない）。

---

## 4. ギミック別の設定

Inspector の項目名の後の `( )` がシリアライズ名。MCP で設定するときはこの名前を使う。

| スクリプト | 用途 | 同期 | 主なイベント / UI から呼ぶメソッド |
| --- | --- | --- | --- |
| `CctLocalToggle` | Interact で物を自分だけ ON/OFF（ミラー、BGM など） | None | `Interact`、`_Toggle` `_TurnOn` `_TurnOff` |
| `CctGlobalToggle` | 全員で共有する ON/OFF、後から来た人にも反映、マスター限定オプション | **Manual** | `Interact`、`_Toggle` `_TurnOn` `_TurnOff` |
| `CctMirrorSwitcher` | 複数ミラー（HQ/LQ/OFF）をローカルで切り替え、初期は全部 OFF | None | `Interact`、`_Next` `_Off` `_Select0`〜`_Select3` |
| `CctTeleporter` | Interact / トリガー進入で自分を Target へ | None | `Interact`、`OnPlayerTriggerEnter`、`_Teleport` |
| `CctPickupResetter` | Pickup を初期位置へ戻す | **NoVariableSync**（イベントのみ） | `Interact`、`_RequestReset` |
| `CctAudioVolume` | UI スライダーで自分だけ音量調整、% 表示、保存（任意） | None（＋PlayerData） | `_OnSliderChanged`、`OnPlayerRestored` |
| `CctAreaToggle` | 自分がエリアにいる間だけ表示（部屋の BGM・照明） | None | `OnPlayerTriggerEnter/Exit`、`OnPlayerRespawn` |
| `CctPlayerCounter` | 現在人数と参加/退出ログ | None | `OnPlayerJoined`、`OnPlayerLeft` |

依存：`CctPickupResetter` は `[NetworkCallable]`（SDK 3.8.1 以降）、`CctAudioVolume` は PlayerData と uGUI の Slider、`CctMirrorSwitcher` / `CctAudioVolume` / `CctPlayerCounter` は TextMeshPro を使う。名前空間は `Naoking.ClaudeWorldGimmicks`。

### 4.1 CctLocalToggle（ローカル ON/OFF）

付ける物：スイッチ役の GameObject（Collider 付き）。

| 項目（シリアライズ名） | 内容 |
| --- | --- |
| Targets（`targets`） | ON のときに表示する GameObject（例：ミラー） |
| Invert Targets（`invertTargets`） | Targets と逆の状態にする GameObject（例：「ミラー ON」の看板） |
| Initial On（`initialOn`） | 入室時の状態（既定 OFF。ミラーは OFF 推奨） |

同期なし。他の人には影響せず、後から来た人は Initial On から始まる。

### 4.2 CctGlobalToggle（全員で共有する ON/OFF）

付ける物：スイッチ役の GameObject（Collider 付き、**常に Active**。非アクティブだと同期を受け取れない）。

| 項目（シリアライズ名） | 内容 |
| --- | --- |
| Targets（`targets`）/ Invert Targets（`invertTargets`） | LocalToggle と同じ |
| Synced Is On（`syncedIsOn`） | インスタンス開始時の状態（以後は同期値。既定 OFF） |
| Master Only（`masterOnly`） | ON でインスタンスマスターだけが操作できる（簡易的な制限。マスターは途中で変わる） |

- **VRC Object Sync と同じ GameObject に置かない**（Manual の UdonBehaviour と ObjectSync の同居は SDK のビルドエラー）。
- 仕組み：押した人が `Networking.SetOwner` でオーナーになり、値を変えて `RequestSerialization()`。受け取り側は FieldChangeCallback で反映し、`OnDeserialization` でも再適用。オーナーが抜けると、新しいオーナーが `OnOwnershipTransferred` で値を送り直す。

### 4.3 CctMirrorSwitcher（ミラー切り替え）

付ける物：切り替えボタン役の GameObject（Interact で使うなら Collider 付き。UI ボタンだけなら不要）。**ミラー本体には付けない。**

| 項目（シリアライズ名） | 内容 |
| --- | --- |
| Mirrors（`mirrors`） | ミラーの GameObject（0 = HQ、1 = LQ…）。`_Next` はこの順に巡回し、最後に OFF |
| Initial Index（`initialIndex`） | 入室時のミラー番号。**-1 = 全部 OFF（既定・推奨）** |
| Label（`label`）/ Mode Names（`modeNames`）/ Off Name（`offName`） | （任意）現在のモード名を TextMeshPro に表示（既定 "OFF"） |

公式の「ミラーは既定 OFF、プレイヤーが ON にできるように」に合わせてある。HQ と LQ は Reflect Layers で作り分ける（[負荷](performance-and-platforms.md) 4章）。

### 4.4 CctTeleporter（テレポート）

付ける物：押すボタン（Collider）か踏む床（Is Trigger の Collider）。テレポート先は別の空の GameObject。

| 項目（シリアライズ名） | 内容 |
| --- | --- |
| Target（`target`） | テレポート先の Transform（位置と向き。Z+ = 青い矢印が正面） |
| Teleport On Enter（`teleportOnEnter`） | ON でトリガーに入ったときもテレポート（自分だけ。既定 OFF） |
| Lerp On Remote（`lerpOnRemote`） | ON：他の人には滑らかな移動に見える／OFF：瞬間移動（既定） |
| Orientation（`orientation`） | 向きの合わせ方（既定 AlignPlayerWithSpawnPoint） |
| Cooldown Seconds（`cooldownSeconds`） | 連続テレポート防止（既定 0.5 秒） |

- 押してワープ：Collider（Is Trigger OFF）。踏んでワープ：Collider（Is Trigger ON）＋Teleport On Enter、レイヤー MirrorReflection 推奨。2つは別の GameObject に分ける。
- `TeleportTo` はローカルプレイヤーにしか効かない。Target を別のテレポーターのトリガーの中に置かない（連鎖ワープ）。Station に座っている間は効かないことがある。

### 4.5 CctPickupResetter（Pickup を戻す）

付ける物：リセットボタン役の GameObject（Collider 付き。**Pickup 自身や Object Sync の付いた物には付けない**）。ワールド開始時から Active にする（Object Sync 無しの物の初期位置を Start で記録するため）。

| 項目（シリアライズ名） | 内容 |
| --- | --- |
| Pickups（`pickups`） | 戻す VRC Pickup（Object Sync 有り/無しを混ぜてよい） |
| Include Held（`includeHeld`） | ON（既定）：持たれている物も手から離して戻す／OFF：持たれている物はそのまま |
| Cooldown Seconds（`cooldownSeconds`） | 連打防止（既定 1 秒） |

- 仕組み：押した人が `SendCustomNetworkEvent(All)` で全員に知らせ、各クライアントは Object Sync 無しの物を自分の画面で戻し、自分がオーナーの Object Sync 付きの物は（持っていれば Drop してから）`VRCObjectSync.Respawn()`。誰にも持たれていない Object Sync 付きの物は、押した人がオーナーになってその場で戻す。
- 手に持たれている同期 Pickup を戻せるのは、持っている人のクライアントにイベントが届いたときだけ。「置いてある」かの判定に使う `VRCPickup.IsHeld` が他人の持ち物でも true になる前提は実機で未確認（README「制限事項」）。

### 4.6 CctAudioVolume（音量スライダー）

付ける物：**ワールド開始時から Active な GameObject**（例：Canvas 自身）。Collider 不要。最初は非表示の設定パネルの中に付けると、入室時の `OnPlayerRestored` を受け取れず保存が働かないことがある（Slider 自体は非表示のパネルにあってよい）。

| 項目（シリアライズ名） | 内容 |
| --- | --- |
| Audio Sources（`audioSources`） | 音量を変える AudioSource（シーン上の音量 = 100%） |
| Slider（`slider`） | 左端 0%、右端 100%（おすすめは Min 0 / Max 1、Value = Max） |
| Percent Label（`percentLabel`） | （任意）「80%」表示の TextMeshPro |
| Persistence Key（`persistenceKey`） | （任意）PlayerData の保存キー。空欄（既定）なら保存しない。ワールド内で一意に（例 `CctAudioVolume.BGM`） |
| Save Delay Seconds（`saveDelaySeconds`） | スライダーを止めてから保存するまでの秒数（既定 1） |

- 配線：Slider の **On Value Changed (Single)** → この GameObject → `UdonBehaviour.SendCustomEvent` → `_OnSliderChanged`。
- 保存は `OnPlayerRestored` の後だけ。PlayerData は書き換えのたびに全データを送るので、動かしている間は保存せず止めてからまとめて保存する。

### 4.7 CctAreaToggle（エリア内だけ表示）

付ける物：エリアを覆う Is Trigger の Collider を付けた空の GameObject（Mesh Renderer なし、レイヤー MirrorReflection 推奨）。

| 項目（シリアライズ名） | 内容 |
| --- | --- |
| Inside Objects（`insideObjects`） | エリア内にいる間だけ表示（BGM の AudioSource を付けた GameObject、照明など） |
| Outside Objects（`outsideObjects`） | エリア外にいる間だけ表示 |
| Check Interval（`checkInterval`） | 自分の位置で内外を確かめ直す間隔（秒、最小 0.1、既定 1。0 で定期確認しない） |
| Exit Margin（`exitMargin`） | トリガーからこの距離（m）以上離れていたら、Exit が来なくても「外」（既定 1） |

- 部屋全体を床まで覆う。L 字の部屋は Box Collider を複数付けてよい。Box / Sphere / Capsule か、**Convex を ON** にした Mesh Collider を使う（凹型の Mesh Collider には `Collider.ClosestPoint` が使えない）。
- BGM の例：AudioSource（Play On Awake ON、Loop ON）を付けた GameObject を Inside Objects に入れ、シーン上では非アクティブにしておく。

### 4.8 CctPlayerCounter（人数・参加ログ）

付ける物：常に Active な GameObject（例：テキスト自身）。Collider 不要。

| 項目（シリアライズ名） | 内容 |
| --- | --- |
| Count Label（`countLabel`） | 人数を表示する TextMeshPro（3D / UI のどちらでも可） |
| Count Prefix（`countPrefix`）/ Count Suffix（`countSuffix`） | 「現在 」「 人」（既定） |
| Log Label（`logLabel`）/ Log Size（`logSize`） | （任意）参加/退出ログと件数（既定 5、0 = ログ無し） |
| Joined Text（`joinedText`）/ Left Text（`leftText`）/ Show Time（`showTime`） | ログの文言と時刻（HH:mm）表示（既定 ON） |

自分が入ったときは既にいる全員分の参加イベントが来るので、ログに既存メンバーの「参加」が並ぶ。ログは各自が見た分だけで、後から来た人に過去のログは届かない。

各ギミックのテスト手順（ClientSim と Build & Test ×2）は README の「テストチェックリスト」にある。

---

## 5. SDK の部品

### 5.1 持てる物：VRCPickup ＋ VRCObjectSync

- `VRCPickup` は **Rigidbody**（RequireComponent）と **Collider** が要る。Collider が無いと Inspector が "This pickup has no collider on it, so it won't be usable." と警告する（`docs/worlds/components/vrc_pickup.md` L15）。
- 他の人と位置を同期するには同じ GameObject に `VRCObjectSync` を足す（同 L17）。同期されるのは位置・回転と kinematic / gravity の状態。kinematic / gravity は Rigidbody を直接書き換えず `SetKinematic` / `SetGravity`（オーナーだけ）を使う。`Respawn()` で初期位置に戻り速度が0になる。`TeleportTo(Transform)`、`FlagDiscontinuity()`（このフレームの移動を補間しない）がある（`docs/worlds/udon/networking/network-components.md` L75-97）。Get 系（GetKinematic 等）は 3.10.5 では Udon に公開されていない（docs の記述と食い違う）。`AllowCollisionOwnershipTransfer`（既定 ON）、`ForceKinematicOnRemote`（3.8.0）。
- **ObjectSync と Manual の UdonBehaviour、ObjectSync と ObjectPool を同じ GameObject に置かない**（ビルドエラー）。
- 主な項目：Proximity（既定 2）、Orientation（Any / Grip / Gun）と Exact Gun / Exact Grip（子の Transform）、Auto Hold、Use Text（既定 "Use"）、Interaction Text、Pickupable、Disallow Theft、Allow Manipulation When Equipped、Throw Velocity Boost、Outline Renderers（3.10.5 追加）。
- Add Component で付けると `Reset()` が層を **Pickup（13）** にし、`version` を 1.1（Auto Hold がチェックボックス）にする（層は 3.10.4 から。層は `VRCSDKBase.dll` の `VRC_Pickup.Reset()`、version は `VRCSDK3.dll` の `VRCPickup.Reset()` で、どちらも逆コンパイルで確認。`releases/release-3.10.4.md` L43）。MCP（スクリプトの AddComponent）で付けたときに `Reset()` が呼ばれるかは未確認（Unity の説明は Inspector からの追加と Reset ボタンの場合だけ）→ MCP で付けたら層と version を読み戻し、違えば明示的に設定する。
- Pickup 層はプレイヤーと衝突しない。床が Pickup と衝突しない層（MirrorReflection・UI・Item 等）だと抜け落ちる（[セットアップ](setup-and-upload.md) 3.2）。Respawn Height Y より下に落ちると Descriptor の設定でリスポーンか破棄。
- 位置を同期しない Pickup は各自の画面で別々に動く。

### 5.2 椅子：VRCStation

- 要るもの：`VRCStation`、Collider（通常 Is Trigger）、Interact で `Networking.LocalPlayer.UseAttachedStation()` を呼ぶ UdonBehaviour（`docs/worlds/components/vrc_station.md` L25-74）。SDK の `VRCChair3.prefab`（`Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VRCChair/`）が例。
- `UseAttachedStation()` は同じ GameObject の Station に座る。`OnStationEntered/Exited(VRCPlayerApi)` は全員で発火するので `player.isLocal` で区別する。
- 主な項目：Player Mobility（既定 Immobilize。Mobile / Immobilize / ImmobilizeForVehicle）、Seated（既定 ON）、Disable Station Exit、Can Use Station From Station（既定 ON）、Station Enter / Exit Player Location（Transform）、Animator Controller。
- U# で型を書くときは `VRC.SDK3.Components.VRCStation` と完全修飾する（`VRC.SDKBase.VRCStation` と曖昧になるため。推定）。

### 5.3 ミラー：VRCMirrorReflection

- SDK の `VRCMirror.prefab`（`Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VRCMirror.prefab`）を使い、既定で非アクティブにしてトグル（`CctLocalToggle` か `CctMirrorSwitcher`）で ON にする。項目と映す層の決め方は [負荷](performance-and-platforms.md) 4章。

### 5.4 動画：VRCUnityVideoPlayer / VRCAVProVideoPlayer

- `VRCUnityVideoPlayer`：`videoURL`、`autoPlay`（true）、`loop`（true）、Render Mode（RenderTexture / MaterialOverride）、Target Texture または Target Material Renderer と Property、Aspect Ratio（既定 FitHorizontally）、Target Audio Sources、Maximum Resolution（720）。
- `VRCAVProVideoPlayer`：`videoURL`、`autoPlay`、`loop`、`maximumResolution`（720）、`useLowLatency`。画面は `VRCAVProVideoScreen`（Renderer 必須、`textureProperty` 既定 `_MainTex`）、音は `VRCAVProVideoSpeaker`（AudioSource 必須、Mode：StereoMix / MonoLeft / MonoRight / Three〜Eight。実際は 5.1 の6ch まで）。
- これらの項目は `[SerializeField] private` なので、SerializedObject では private 名で参照する。
- 同期再生の例は SDK の `UdonSyncPlayer (AVPro).prefab` / `(Unity).prefab`（`Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VideoPlayers/`、ループ無し）。レート制限・許可ドメイン・HTTPS は [負荷](performance-and-platforms.md) 5章。AVPro はエディタで再生されないので Build & Test で確かめる。

### 5.5 UI：VRCUiShape

- Canvas と同じ GameObject に付ける。設定項目は Allow Focus View（既定 ON）だけ。
- 層は UI 以外、World Space、スケール 0.01 程度、EventSystem、GraphicRaycaster、Raycast Target を正しく（透明な要素がボタンを覆わない）、Navigation None、Canvas の Z+ が見る人と反対を向く（`docs/worlds/components/vrc_uishape.md` L10-72）。
- BoxCollider が無ければ実行時に自動で足され RectTransform の大きさに合わせられる。自分で付けたなら大きさを確かめる。親 Canvas の GraphicRaycaster の blockingObjects は None にされる（SDK の `VRC_UiShape` の実装）。
- 入力欄で VRChat のキーボードを出したくないときは `VRCInputFieldKeyboardOverride`。
- 日本語を VRChat の代替フォントで出すには Project Settings > TextMeshPro の Fallback Font Assets を空にする（`docs/worlds/components/textmeshpro/index.md`）。
- デバッグにはオーバーレイ 0（VRC_UiShape）がある（[テストとデバッグ](testing-and-debugging.md) 5章）。

### 5.6 物を出し入れする：VRCObjectPool

- `Pool`（GameObject の配列）。オーナーが `TryToSpawn()`（空きが無ければ null）と `Return(obj)` を呼ぶ（`docs/worlds/udon/networking/network-components.md` L99-104）。オーナー以外が呼ぶとエラーログ "Non-owner attempted to spawn object from…" / "Non-owner attempted to return…" になり、`TryToSpawn()` は null を返し、`Return` は何もしない（SDK の `VRCObjectPool`、逆コンパイル L95-99, L130-133）。
- 初期化時にプールの物は全部非アクティブになり初期位置が記録される。Return で非アクティブ・初期位置に戻る。アクティブ状態は同期され、後から入った人にも反映される。出現時の処理は `OnEnable` に書く（`OnSpawn` は廃止）。
- ObjectPool と ObjectSync を同じ GameObject に置かない（ビルドエラー）。

### 5.7 その他

- **ポータル**：`VRCPortalMarker.prefab` は**シーン階層のルートに置く**（行き先インスタンスの同期のため。`docs/worlds/sdk-prefabs.md` L25-30）。Inspector の項目は World ID（`wrld_…`）・Custom Portal Name・World（None / Hub / Home。None 以外では World ID 無効）。
- **アバターペデスタル**：`blueprintId` を設定。Interact でアバターを変える。Labs/Public では「適度に最適化」したアバターだけを置く。
- **移動速度**：VRCWorld の VRCWorldSettings（[セットアップ](setup-and-upload.md) 2.5）。プレイヤーの移動系の値はローカルプレイヤー専用。

---

## 6. MCP で設定・配線するときの注意

- U# のコンポーネントのプロパティは、4章の `( )` のシリアライズ名で **U# のコンポーネント（プロキシ）側**に設定する。U# は Play・ビルド時にこの値を裏の UdonBehaviour にコピーする。MCP for Unity の `manage_components` で設定できると README は書くが、ユーザーの環境では `manage_components` は未実測（`unity-mcp-operations` の verified-facts）。最初の1件で試して読み戻す。
- Interact の文字と距離は**裏の UdonBehaviour** の `interactText` / `proximity`。
- UI イベントの配線（On Click ()、On Value Changed）はプロパティ設定ではできない。本人に Inspector で配線してもらうか、エディタ C# を実行できるなら `UnityEditor.Events.UnityEventTools.AddStringPersistentListener(button.onClick, UdonSharpEditor.UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy).SendCustomEvent, "_Toggle")` のように**裏の UdonBehaviour** の `SendCustomEvent` を登録する（Slider は `slider.onValueChanged`、引数 `"_OnSliderChanged"`）。この方法の MCP での実行は未検証。
- `execute_code` の Roslyn は Assembly-CSharp を参照しないので、取り込んだ `Cct*` の型名を直接書けない。`AppDomain.CurrentDomain.GetAssemblies()` から型を取り、リフレクションで扱う（コンポーネントの追加なら `UdonSharpEditor.UdonSharpUndo.AddComponent(gameObject, type)`。[UdonSharp](udon-networking.md) 1章）。`using` ディレクティブも書けない（フル修飾で書く）。ユーザーの泡ワールドの記録（[台帳](verified-facts.md)）。別のプロジェクト（ota の BPM 作業、2026-09-11）では execute_code が CodeDom C#6 だった記録もあるので（`vrchat-bpm-controls` の `references/wotagei-ota-bpm.md` L59）、最初にどちらのコンパイラかを確かめる。
- `UdonBehaviour.publicVariables` のゲッターはエディタでは NullReferenceException を投げ得る。値の確認は U# 側のフィールドの読み戻しと Play での実測で行う。
- Play 中（ClientSim）に `UdonBehaviour.Interact()` をリフレクションで呼ぶと、Interact ギミックの機能を確かめられた（泡ワールドの記録）。ただしこれはローカルでの確認で、同期の確認ではない。
- 1件で試す → 本人が目視 → 残りに適用。同じギミックを10か所以上に置くなら Unity 側で1回にまとめる。
- 既存のギミック（購入アセット・AudioLink・動画プレイヤー等）を置き換える前は `cct_snapshot` で退避する。AudioLink 3.1.2 の AudioReactiveLight.prefab にはコンポーネントが2個あり、片方は裏の UdonBehaviour が無い残骸だった（泡ワールド）。設定は裏の UdonBehaviour がある方に行う。
