# World Gimmicks (UdonSharp) — Claude Creator Tools サンプル

VRChat **ワールド**用の、よく使うギミック 8 種の UdonSharp (U#) スクリプト集です。
各スクリプトの冒頭に、目的・セットアップ・同期の仕組み・テスト手順を日本語で書いてあります。

- 対象: Unity 2022.3 (22f1) + VRChat Worlds SDK 3.10.5 (UdonSharp と ClientSim は SDK に同梱)
  - `CctPickupResetter` は `[NetworkCallable]` (SDK 3.8.1 以降)、`CctAudioVolume` は PlayerData (Persistence) を使います。
    `CctMirrorSwitcher` / `CctAudioVolume` / `CctPlayerCounter` は TextMeshPro、`CctAudioVolume` は uGUI (Slider) も使います。
- 名前空間: `Naoking.ClaudeWorldGimmicks` / クラス名はすべて `Cct` で始まります
- **ワールドプロジェクト専用です。** アバタープロジェクトには UdonSharp が無いため、取り込むとコンパイルエラーになります。
  下の「インポート方法」の A / B を使えば、UdonSharp の有無を確認してから取り込みます。

> **Unity 実機では未検証です。** VRChat Worlds SDK 3.10.5 の実際の DLL と UdonSharp のソースに対して
> コンパイル・静的チェック (U# 非対応構文、Udon に公開された API かどうか) を通したものです。
> 公開前に必ず ClientSim と Build & Test (2 クライアント) で動作を確認してください。

---

## 収録ギミック

| スクリプト | 用途 | 同期モード | 主なイベント / 公開メソッド |
|---|---|---|---|
| `CctLocalToggle` | Interact で物を自分だけ ON/OFF (ミラー、BGM など)。逆状態リスト付き | None (ローカル) | `Interact`, `_Toggle` `_TurnOn` `_TurnOff` |
| `CctGlobalToggle` | 全員で共有する ON/OFF。後から来た人にも反映。マスター限定オプション | **Manual** | `Interact`, `_Toggle` `_TurnOn` `_TurnOff`, FieldChangeCallback |
| `CctMirrorSwitcher` | 複数ミラー (HQ / LQ / OFF) をローカルで切り替え。初期状態は全部 OFF | None (ローカル) | `Interact`, `_Next` `_Off` `_Select0`〜`_Select3` |
| `CctTeleporter` | Interact / トリガー進入で自分を Target へテレポート | None | `Interact`, `OnPlayerTriggerEnter`, `_Teleport` |
| `CctPickupResetter` | ピックアップを初期位置へ戻す (置いてある同期ピックアップは押した人が持ち主になって戻し、手に持たれている物は持っている人の画面で戻す) | **NoVariableSync** (ネットワークイベントのみ) | `Interact`, `_RequestReset` |
| `CctAudioVolume` | UI スライダーで AudioSource の音量を自分だけ調整、% 表示、保存 (任意) | None (+ PlayerData) | `_OnSliderChanged`, `OnPlayerRestored` |
| `CctAreaToggle` | 自分がエリアにいる間だけ表示 (部屋の BGM / 照明)。Enter/Exit の取りこぼしを位置の確認で補正 | None (ローカル) | `OnPlayerTriggerEnter/Exit`, `OnPlayerRespawn` |
| `CctPlayerCounter` | 現在人数と参加/退出ログを表示 | None (ローカル) | `OnPlayerJoined`, `OnPlayerLeft` |

ファイル: `Scripts/<スクリプト名>.cs` (1 ファイル 1 クラス、UTF-8 BOM 付き)

---

## インポート方法

### A. Claude (MCP) から — 推奨

Claude Creator Tools の MCP ツール `cct_world_gimmicks` を使います。

1. `cct_world_gimmicks` を `action: "list"` (省略時の既定) で実行 → UdonSharp の有無、各サンプルが使うパッケージの有無、
   インポート済みかどうか、別の場所に同じスクリプトが無いか、取り込み先が使えるかを確認。
   `importable` に「今取り込めるサンプル名」が入るので、一部が取り込めない時はそれを `only` に渡します。
2. `action: "import"` (confirm なし) で実行 → ドライラン。書き込み予定 (`plan`) と作成予定の Program Asset (`program_assets_planned`) だけ返します。
3. `action: "import", confirm: true` で実行 → `Assets/ClaudeWorldGimmicks/` に取り込み。
   - UdonSharp が無いプロジェクトでは `NO_UDONSHARP` で拒否します。
   - 選んだサンプルが使うパッケージ (TextMeshPro / uGUI / PlayerData / NetworkCallable) が無い場合は `MISSING_DEPENDENCY` で拒否します
     (そのサンプルを `only` から外せば残りは取り込めます)。
   - 既存ファイルは上書きしません (`overwrite: true` で上書き。既存の .meta は常に保持)。
   - `.meta` もコピーするので GUID が安定します (同じ GUID を別のアセットが使っている場合はコピーせず警告)。
   - 同じスクリプトがプロジェクトの別の場所 (例: Package Manager の Samples で取り込んだ `Assets/Samples/...`) にある場合、
     そのサンプルは取り込みません (`SAMPLE_EXISTS_ELSEWHERE`。同じクラスが 2 つあるとプロジェクト全体がコンパイルエラーになるため)。
     そのコピーに Program Asset が無ければ、コピーの隣に作ります (Assets/ の中にある場合のみ。ドライランの `program_assets_planned` に出ます)。
   - `dest` で取り込み先 (Assets/ 以下) を変更可。次の場所は拒否します (U# がコンパイルしないため):
     .asmdef / .asmref があるフォルダの中 (`DEST_HAS_ASMDEF`。U# 用 Assembly Definition が別途必要になる)、
     `Editor` フォルダの中、`Assets/Plugins` / `Assets/Standard Assets` の中 (`DEST_SPECIAL_FOLDER`)。
   - `only: ["CctTeleporter"]` のように一部だけ取り込むこともできます (大文字小文字は区別しない。README は常に取り込みます)。
   - 取り込み後、各スクリプトの **UdonSharp Program Asset (`<スクリプト名>.asset`) を自動作成** します (`create_program_assets: false` で無効)。
   - 結果の `files_written` / `files_skipped` / `program_assets_created` に、実際に書いたもの・飛ばしたものが全部入ります。
     書き込み後は Unity のスクリプト再コンパイルが走ります。コンパイル中に次の MCP 呼び出しをすると `EDITOR_BUSY` が返るので、少し待ってから再実行します。

### B. Unity のメニューから

`Tools > Claude Creator Tools > ワールドギミック (UdonSharp) > ClaudeWorldGimmicks にインポート (Assets 直下)`
(内容は A の `import, confirm: true` と同じ。`一覧と導入状況をログ出力` で状態を Console に出せます)

### C. Package Manager の Samples から (非推奨)

Package Manager の「Samples > Import」でも取り込めますが、**UdonSharp の有無を確認しません**。
また Program Asset は作られないので、下の「インポート後にやること」2 の手順で手動で作ってください。
この方法で取り込んだ後に A / B を使うと、同じスクリプトは重複を避けて取り込まず、代わりに足りない Program Asset をそのコピーの隣に作ります。

---

## インポート後にやること (必須)

1. **コンパイルを待つ。** Unity がスクリプトをコンパイルし、続いて UdonSharp が U# プログラムを自動でコンパイルします。
   Console に UdonSharp のエラーが出ていないことを確認します。
2. **Program Asset を確認する。** `Assets/ClaudeWorldGimmicks/Scripts/` に各スクリプトと同名の `.asset` (UdonSharp Program Asset) が必要です。
   UdonSharp は「Create > U# Script」で新規作成した時にしか Program Asset を作りません (既存の .cs をコピーしても作られません)。
   A / B の方法なら自動で作ります。無い場合は手動で作ります:
   - Project ウィンドウで `Scripts` フォルダを右クリック → **Create > VRChat > Udon > Udon C# Program Asset**
   - 作った `.asset` を選択し、Inspector の **Source Script** に対応する `.cs` をドラッグ (名前はスクリプト名と同じにしておくと分かりやすい)
   - 1 スクリプトにつき Program Asset は 1 つだけ (複数あると UdonSharp がエラーにします)
3. **コンポーネントを付ける。** ギミックを置く GameObject を選び、**Add Component** でスクリプト名 (例: `CctLocalToggle`) を追加します。
   Inspector に表示された時点で UdonSharp が裏側の UdonBehaviour を自動で用意します。
   (Claude が MCP でコンポーネントを追加した場合は、その GameObject を選択して Inspector を表示するか、
   Play に入る / シーンを開き直すと UdonSharp が自動でセットアップします)
4. 下の「ギミック別セットアップ」に従って、インスペクター項目・Collider・レイヤー・UI イベントを設定します。
5. ClientSim (Play) で確認 → **VRChat SDK > Show Control Panel > Builder > Build & Test** (Number of Clients = 2) で同期を確認します。

UdonSharp が自動でコンパイルしない時は **VRChat SDK > Udon Sharp > Refresh All UdonSharp Assets**、
または任意の Program Asset の Inspector で **Utilities > Compile All UdonSharp Programs** を押します。

---

## 共通の設定

### Interact (クリックで反応) させる物
- 同じ GameObject に **Collider** が必要です (Is Trigger は OFF で OK)。
- レイヤーは **Default (0)** か **Interactive (8)** (ステッカーを貼られたくない場合)。
- Interact の文字は UdonBehaviour の Inspector の「Interaction Text」、届く距離は「Proximity」で変更できます。
- スイッチ自身の GameObject を「非表示にするリスト」に入れても無視されます (二度と押せなくなるため)。
  スイッチの**親**をリストに入れるとスイッチごと消えるので入れないでください。

### トリガー (踏む / エリアに入る)
- Collider の **Is Trigger を ON**。スクリプトと同じ GameObject に付けます。
- レイヤーは **MirrorReflection (18)** を推奨 (プレイヤーとトリガー判定し、かつ VRChat のインタラクト用レイ
  (UI レーザー・遠くのピックアップ) を遮りません)。Mesh Renderer は付けない (MirrorReflection の描画はミラーにだけ映るため)。
- 公式ドキュメントにある通り、テレポートや高速移動では Enter/Exit が取りこぼされることがあります
  (`CctAreaToggle` は対策済み)。
- Udon は **Start より前に届いたイベントを捨てます** (VRC.Udon の UdonBehaviour.RunEvent)。最初は非アクティブな
  GameObject の上のスクリプトは、アクティブになって Start が走るまでイベント (Enter, OnPlayerRestored など) を受け取りません。

### UI (Canvas) から呼ぶ
- Hierarchy 右クリック → **UI > TextMeshPro (VRC)** などで作ると、VRChat 用の設定 (VRC Ui Shape 等) が自動で付きます。
  手動で作る場合: Canvas に **VRC Ui Shape** を追加、Render Mode = **World Space**、レイヤーを **UI から Default に変更**
  (UI レイヤーは VRChat のメニューを開いている時しか操作できない)、スケール 0.01 程度、各 UI 要素の Navigation = None。
- ボタンの配線: Button の **On Click ()** の **+** → スクリプトを付けた GameObject をドラッグ →
  **UdonBehaviour > SendCustomEvent (string)** → メソッド名 (例: `_Toggle`) を入力。
  UI イベントから Udon に値は渡せません。スライダーは `_OnSliderChanged` を呼び、スクリプト側でスライダーの位置 (`normalizedValue`) を読みます。
- `_` で始まる公開メソッドは「ローカル専用」の印です (ネットワークイベントとしては呼ばれません)。

### Claude (MCP) から設定する場合
- 下の各表の `( )` 内がシリアライズ名です。MCP for Unity の `manage_components` などで **U# スクリプトのコンポーネント
  (例: `CctLocalToggle`) のプロパティ**としてこの名前で設定します。U# は Play / ビルド時にこの値を裏の UdonBehaviour へコピーします。
- コンポーネントを追加した直後は U# の初期化が済んでいません。その GameObject を選択して Inspector を表示するか、
  Play に入る / シーンを開き直すと U# が自動でセットアップします (Console に "has not been fully setup, running setup" が出るのは正常)。
- Interact の文字と距離は裏の `UdonBehaviour` コンポーネントの `interactText` / `proximity` です。
- UI イベントの配線 (On Click () / On Value Changed) はプロパティ設定ではできません。ユーザーに Inspector で配線してもらうか、
  エディタ C# を実行できる場合は `UnityEditor.Events.UnityEventTools.AddStringPersistentListener(button.onClick,
  UdonSharpEditor.UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy).SendCustomEvent, "_Toggle")` のように
  **裏の UdonBehaviour** の `SendCustomEvent` を登録します (Slider は `slider.onValueChanged`、引数は `"_OnSliderChanged"`)。

---

## ギミック別セットアップ

### 1. CctLocalToggle (ローカル ON/OFF)
スクリプトを付ける物: スイッチ役の GameObject (Collider 付き)。

| 項目 (シリアライズ名) | 内容 |
|---|---|
| Targets (`targets`) | ON の時に表示する GameObject (例: ミラー) |
| Invert Targets (`invertTargets`) | Targets と逆の状態にする GameObject (例: 「ミラー ON」看板) |
| Initial On (`initialOn`) | 参加時の状態 (ミラーは OFF 推奨。既定 OFF) |

- 必要: Collider (Interact 用)。UI から: `_Toggle` / `_TurnOn` / `_TurnOff`。
- 同期なし。他の人には影響せず、後から来た人は Initial On から始まります。リストに入れたスイッチ自身は無視します。

### 2. CctGlobalToggle (全員で共有する ON/OFF)
スクリプトを付ける物: スイッチ役の GameObject (Collider 付き、常に Active)。

| 項目 (シリアライズ名) | 内容 |
|---|---|
| Targets (`targets`) / Invert Targets (`invertTargets`) | CctLocalToggle と同じ |
| Synced Is On (`syncedIsOn`) | インスタンス開始時の状態 (以降は同期値。既定 OFF) |
| Master Only (`masterOnly`) | ON でインスタンスマスターだけが操作可 (他の人にはインタラクトが出ない) |

- 必要: Collider (Interact 用)。このスクリプトの GameObject は常に Active に (非アクティブだと同期を受け取れない)。
- **VRC Object Sync と同じ GameObject に置かない** (Manual 同期の UdonBehaviour と ObjectSync の同居は SDK のビルドエラー)。
- 仕組み: 押した人が `Networking.SetOwner` で持ち主になり、値を変えて `RequestSerialization()`。
  受け取り側は FieldChangeCallback (IsOn の setter) で反映、`OnDeserialization` でも念のため再適用。
  Late Joiner には VRChat が最新値を自動で送ります。持ち主が抜けると VRChat が新しい持ち主を決め、
  新しい持ち主は `OnOwnershipTransferred` で値を送り直して全員を揃えます。
- Master Only は簡易的な制限です (マスターは途中で変わり、公式もマスターでの権限管理を推奨していません)。

### 3. CctMirrorSwitcher (ミラー切り替え)
スクリプトを付ける物: 切り替えボタン役の GameObject (Interact で使うなら Collider 付き。UI ボタンだけで使うなら Collider 不要)。

| 項目 (シリアライズ名) | 内容 |
|---|---|
| Mirrors (`mirrors`) | ミラーの GameObject (0 = HQ, 1 = LQ … の順)。`_Next` はこの順に巡回し最後に OFF |
| Initial Index (`initialIndex`) | 参加時のミラー番号。**-1 = 全部 OFF (既定・推奨)** |
| Label (`label`) / Mode Names (`modeNames`) / Off Name (`offName`) | (任意) 現在のモード名を TextMeshPro に表示 (既定 "OFF") |

- 公式ガイド「ミラーは既定で OFF、プレイヤーが ON にできるように」に合わせています。
- UI から: `_Next` / `_Off` / `_Select0`〜`_Select3`。同期なし。スイッチ自身の GameObject を Mirrors に入れても無視します (ミラー本体には付けない)。

### 4. CctTeleporter (テレポート)
スクリプトを付ける物: 押すボタン (Collider) か、踏む床 (Is Trigger の Collider)。テレポート先は別の空 GameObject。

| 項目 (シリアライズ名) | 内容 |
|---|---|
| Target (`target`) | テレポート先の Transform (位置 + 向き。Z+ = 青い矢印が正面) |
| Teleport On Enter (`teleportOnEnter`) | ON でトリガーに入った時もテレポート (自分だけ。既定 OFF) |
| Lerp On Remote (`lerpOnRemote`) | ON: 他の人には滑らかな移動に見える (近距離・頻繁なワープ向け) / OFF: 瞬間移動 (既定・通常) |
| Orientation (`orientation`) | 向きの合わせ方 (既定 AlignPlayerWithSpawnPoint。推奨) |
| Cooldown Seconds (`cooldownSeconds`) | 連続テレポート防止 (既定 0.5 秒) |

- 押してワープ: Collider (Is Trigger OFF)。踏んでワープ: Collider (Is Trigger ON) + Teleport On Enter、レイヤー MirrorReflection 推奨。
  2 つは別々の GameObject に分けるのがおすすめです。
- `TeleportTo` はローカルプレイヤーにしか効きません (他人は動かせない)。トリガーはローカルプレイヤーの時だけ反応します。
- Target を別のテレポーターのトリガー内に置かない (連鎖ワープ防止)。Station に座っている間は効かないことがあります。

### 5. CctPickupResetter (ピックアップを戻す)
スクリプトを付ける物: リセットボタン役の GameObject (Collider 付き。ピックアップ自身や Object Sync の付いた物には付けない)。

| 項目 (シリアライズ名) | 内容 |
|---|---|
| Pickups (`pickups`) | 戻す VRC Pickup コンポーネント (Object Sync 有り/無しを混在可) |
| Include Held (`includeHeld`) | ON (既定): 持たれている物も手から離して戻す / OFF: 持たれている物はそのまま |
| Cooldown Seconds (`cooldownSeconds`) | 連打防止 (既定 1 秒) |

- ボタンはピックアップとは別の GameObject に置き、ワールド開始時から Active に (Object Sync 無しの物の初期位置を Start で記録)。
- 仕組み: 押した人 (A) は次の 2 つを行います。
  1. `SendCustomNetworkEvent(All)` で全員に通知 (A 自身は即時実行)。受け取った各クライアントは
     - Object Sync 無しの物: 自分の画面のコピーを初期位置・回転へ戻す (自分が持っていれば `Drop()` してから)。
     - Object Sync 付きで**自分が持ち主**の物: 持っていれば `Drop()` してから `VRCObjectSync.Respawn()`。
       手に持っている人 = 持ち主なので、「誰かが持っている物」は持っている本人の画面で正しく手から離れます。
  2. **誰にも持たれていない** Object Sync 付きの物は、A 自身が `Networking.SetOwner` で持ち主になってその場で `Respawn()`。
     持ち主が退出した直後 (新しい持ち主が決まる前) や、持ち主のクライアントが重い/応答しない時でも、
     置いてある物は他の人のクライアントに頼らず、押した人の画面で戻ります。
  - 戻した後は Rigidbody の速度・回転速度もゼロにします (手から離した時の勢いが残らないように)。
  - 2 人が同時に押しても結果は同じ (どちらも初期位置)。A が持ち主を取るのと同時に別の人が掴んだ場合は、
    最終的に持ち主になった方の状態 (初期位置か、その人の手の中) に揃います。
- VRC Pickup には Rigidbody と Collider が必要です (VRChat の仕様)。

### 6. CctAudioVolume (音量スライダー)
スクリプトを付ける物: ワールド開始時から Active な GameObject (例: Canvas 自身)。Collider 不要。

| 項目 (シリアライズ名) | 内容 |
|---|---|
| Audio Sources (`audioSources`) | 音量を変える AudioSource (シーン上の音量 = 100%) |
| Slider (`slider`) | 左端 = 0%、右端 = 100% (Min / Max Value は何でもよい。おすすめは Min 0 / Max 1、Value = Max で 100% 開始) |
| Percent Label (`percentLabel`) | (任意) 「80%」表示の TextMeshPro |
| Persistence Key (`persistenceKey`) | (任意) PlayerData の保存キー。空欄 (既定) なら保存しない。ワールド内で一意に (例: `CctAudioVolume.BGM`) |
| Save Delay Seconds (`saveDelaySeconds`) | スライダーを止めてから保存するまでの秒数 (既定 1) |

- **CctAudioVolume はワールド開始時から Active な GameObject に付けてください** (例: Canvas 自身)。最初は非表示の
  設定パネルの中に付けると、入室時の `OnPlayerRestored` を受け取れず保存が働かないことがあります
  (救済として、Start の時点で保存キーが既にあればその値を読み込みます)。Slider 自体は非表示のパネルにあっても OK。
- 配線: Slider の **On Value Changed (Single)** → この GameObject → `UdonBehaviour.SendCustomEvent` → `_OnSliderChanged`。
- 保存 (PlayerData): 自分のデータの読み込み完了 (`OnPlayerRestored`) より前は読み書きしません (公式の注意)。
  PlayerData は書き換えのたびに全データを送るため、動かしている間は保存せず止めてからまとめて保存します。
- 同期なし (各自の音量)。

### 7. CctAreaToggle (エリア内だけ表示)
スクリプトを付ける物: エリアを覆う Is Trigger の Collider を付けた空の GameObject (Mesh Renderer なし)。

| 項目 (シリアライズ名) | 内容 |
|---|---|
| Inside Objects (`insideObjects`) | エリア内にいる間だけ表示 (BGM の AudioSource を付けた GameObject、照明など) |
| Outside Objects (`outsideObjects`) | エリア外にいる間だけ表示 |
| Check Interval (`checkInterval`) | 自分の位置でエリアの内外を確かめ直す間隔 (秒、最小 0.1、既定 1)。0 で定期確認しない (起動 0.5 秒後・リスポーン時・再有効化時の確認だけ) |
| Exit Margin (`exitMargin`) | トリガーのコライダーからこの距離 (m) 以上離れていたら、Exit が来なくても「外」 (既定 1) |

- 同じ GameObject に Collider (Is Trigger ON) を付け、部屋全体を床まで覆う。L 字の部屋は Box Collider を複数付けて OK。
  Is Trigger が OFF のコライダーは判定に使いません。Box / Sphere / Capsule か、**Convex を ON** にした Mesh Collider を使います
  (位置の確認に使う `Collider.ClosestPoint` が凹型の Mesh Collider には使えないため)。
- レイヤーは MirrorReflection (18) 推奨。自分自身の GameObject はリストに入れても無視されます。
- 取りこぼし対策:
  - 重なったトリガーは Enter/Exit の回数を数えて判定。Enter を取りこぼした後で回数が 0 になっても、体の中心線がまだ別の
    トリガーの中なら「中」のまま (L 字の部屋の途中で一瞬消えて BGM が頭から鳴り直すことがない)。
  - Check Interval ごと (と起動 0.5 秒後・リスポーン 0.25 秒後) に自分の位置を確かめ直します:
    「外」扱いなのに体の中心線 (足元付近・腰の高さ) がトリガーの中 → 「中」に。
    「中」扱いなのに足元・腰・頭の高さのどれもトリガーから Exit Margin 以上離れている → 「外」に。
    これで、テレポート/リスポーンで Enter/Exit が来ない場合、**エリアの中でスポーンした場合**、
    **エリアの中にいる時にこの GameObject が有効化された場合** (Udon は Start 前の Enter を捨てる) も、最大 Check Interval 秒で正しい表示になります。
  - GameObject やこのコンポーネントが無効化された時は「外」に戻します (Unity は無効化時に Exit を送らないため)。
    無効の間の Enter/Exit は無視し、再び有効になった 0.25 秒後に位置で確かめ直します。
- BGM の例: AudioSource (Play On Awake ON, Loop ON) を付けた GameObject を Inside Objects に入れる (入るたびに頭から再生)。
  シーン上では非アクティブにしておく (Start で「外」の状態が適用されるが、最初の 1 フレーム鳴るのを防ぐため)。

### 8. CctPlayerCounter (人数・参加ログ)
スクリプトを付ける物: 任意の常に Active な GameObject (例: テキスト自身)。Collider 不要。

| 項目 (シリアライズ名) | 内容 |
|---|---|
| Count Label (`countLabel`) | 人数を表示する TextMeshPro (3D の TextMeshPro / UI の TextMeshPro - Text (UI) どちらも可) |
| Count Prefix (`countPrefix`) / Count Suffix (`countSuffix`) | 「現在 」「 人」 (既定) |
| Log Label (`logLabel`) / Log Size (`logSize`) | (任意) 参加/退出ログと件数 (既定 5。0 = ログ無し) |
| Joined Text (`joinedText`) / Left Text (`leftText`) / Show Time (`showTime`) | ログの文言 (既定「 が参加しました」「 が退出しました」) と時刻 (HH:mm) 表示 (既定 ON) |

- `OnPlayerLeft` の中では退出者がまだプレイヤー一覧に含まれる場合があります (ClientSim はイベント送信後に一覧から削除)。
  人数は一覧から「有効 かつ 退出者以外」を数えるので、含まれていてもいなくても正しく出ます。
- 自分が参加した時は、既にいる全員分の参加イベントが来るため、ログに既存メンバーの「参加」が並びます。
- 人数は毎回プレイヤー一覧から数え直すので、途中まで非表示だった場合も表示された時 (Start / OnEnable) に正しい人数になります。

---

## ネットワークの注意 (まとめ)

- 同期変数を書き換えられるのは **その GameObject の持ち主だけ**。書く前に `Networking.SetOwner`、書いたら `RequestSerialization()` (Manual)。
- **後から来た人** には同期変数の最新値が届きます (`OnDeserialization` / FieldChangeCallback)。**ネットワークイベントは再送されません**。
  状態 (ON/OFF 等) は同期変数で、一瞬の出来事 (リセット等) はイベントで扱います。
- 持ち主が退出すると VRChat が自動で新しい持ち主を割り当てます (同期値は残ります)。
- `Networking.LocalPlayer` はエディタ等で無効なことがあるので、使う前に `Utilities.IsValid()` で確認しています。
- VRC Object Sync と Manual 同期の UdonBehaviour は同じ GameObject に置けません (SDK のビルドエラー)。
- ネットワークイベントで呼ぶメソッドは `[NetworkCallable]` を付けています (SDK 3.8.1 以降の推奨)。`_` で始まる公開メソッドはネットワーク越しに呼ばれません。

---

## テストチェックリスト

ClientSim はローカルプレイヤーだけを再現します (リモートプレイヤーの同期は再現しない)。
同期は **Build & Test (Number of Clients = 2)** で確認してください。ClientSim では Esc キーのメニューから
リスポーンやリモートプレイヤーの追加ができます。

### CctLocalToggle
- [ ] ClientSim: クリックで Targets / Invert Targets が逆に切り替わる。Initial On の状態で始まる
- [ ] Build & Test ×2: 片方で押しても、もう片方の表示は変わらない

### CctGlobalToggle
- [ ] ClientSim: クリックで切り替わる (ClientSim のプレイヤーはマスターなので Master Only でも押せる)
- [ ] Build & Test ×2: A で押す → B にも反映 / B で押す → A にも反映
- [ ] Late Joiner: A で状態を変えた後に B を Build & Reload で入り直す → B が A と同じ状態で始まる
- [ ] 最後に押した人 (持ち主) のクライアントを閉じる → 残った方で状態が保たれ、押せる
- [ ] Master Only ON: マスターでない方にはインタラクトが出ない

### CctMirrorSwitcher
- [ ] ClientSim: 開始時は全ミラー OFF。押すたびに 0 → 1 → … → OFF。UI ボタンで指定のミラーだけ表示
- [ ] Build & Test ×2: 片方で ON にしても、もう片方は OFF のまま

### CctTeleporter
- [ ] ClientSim: クリック / トリガーで Target の位置・向きへ移動する
- [ ] Build & Test ×2: A がワープ → B から見て A が移動。B が A 用のトリガーに入ってもワープするのは B だけ
- [ ] Lerp On Remote ON/OFF で、相手から見た移動の見え方が変わる

### CctPickupResetter
- [ ] ClientSim: 動かした / 持っているピックアップがボタンで初期位置に戻る (持っていた物は手から離れる)
- [ ] Build & Test ×2: A が Object Sync 付きの物を持つ → B がボタン → A の手から離れ、両方の画面で初期位置に戻る
- [ ] Build & Test ×2: A が Object Sync 付きの物を動かして置く → A のクライアントを閉じる → B がボタン → 初期位置に戻る
- [ ] Object Sync 無しの物: 各自の画面で動かしてからボタン → 両方とも初期位置に戻る
- [ ] Include Held OFF: 持たれている物だけ戻らない

### CctAudioVolume
- [ ] ClientSim: スライダーで音量と「%」表示が変わる
- [ ] 保存: Persistence Key を入れて値を変える → Play を止めて再生 → 値が復元される (ClientSim の PlayerData ウィンドウでも確認可)
- [ ] Slider の Max Value を 100 にしても 0〜100% が正しく動く
- [ ] Build & Test ×2: A で下げても B の音量は変わらない (Build & Test の保存データはクライアントを閉じると消える。保存確認は Build & Reload で)

### CctAreaToggle
- [ ] ClientSim: 歩いて出入りすると Inside / Outside が切り替わる
- [ ] エリア内から CctTeleporter で外へワープ → 1 秒程度で「外」に戻る
- [ ] 外から CctTeleporter でエリア内へワープ → 1 秒程度で「中」になる
- [ ] スポーン地点 (VRC Scene Descriptor の Spawns) をエリア内に置いて Play → 1 秒程度で「中」になる
- [ ] エリア内で Esc メニューからリスポーン → 外なら「外」に戻る
- [ ] L 字など複数トリガー: 重なり部分を通っても途中で消えない
- [ ] L 字のエリア内でスポーンしてから、もう片方のトリガーへ歩いて移る → 途中で消えない (BGM が頭から鳴り直さない)
- [ ] Build & Test ×2: 相手がエリアに入っても自分の表示は変わらない

### CctPlayerCounter
- [ ] ClientSim: 開始時「現在 1 人」。Esc メニューでリモートプレイヤーを追加/削除すると人数とログが増減 (退出時に正しく 1 減る)
- [ ] Build & Test ×2: 両方「現在 2 人」→ 片方を閉じると残った方が「現在 1 人」と「○○ が退出しました」

---

## 制限事項

- **Unity 実機では未検証** です。検証したのは次の 3 点のみです:
  1. VRChat Worlds SDK 3.10.5 の実 DLL (VRCSDKBase / VRCSDK3 / VRC.Udon.Common / VRC.Dynamics / VRCEconomy 等) と、
     SDK 同梱ソースからビルドした VRC.Udon / UdonSharp.Runtime / UdonSharp.Lib に対する C# 9 コンパイル
     (Unity エンジンは 2021.3 の参照 DLL、uGUI と TextMeshPro は最小スタブ)
  2. UdonSharp コンパイラのソースから抜き出した「U# が扱えない構文・宣言」の静的チェック
  3. 使用しているすべての Unity / VRChat API が Udon に公開されているか (SDK の Udon ラッパー DLL の関数表との照合)
- ClientSim / VRChat クライアント上での動作、インスペクター表示、UdonSharp による実際のコンパイル結果は未確認です。
- インポーター (`cct_world_gimmicks` / メニュー) も Unity 実機では未実行です (C# コンパイルと MCP 登録規則のチェックのみ)。
  Program Asset の自動作成は UdonSharp 自身の「Create > U# Script」と同じ手順 (CreateInstance → sourceCsScript 設定 → CreateAsset) を
  なぞっていますが、うまくいかない時は「インポート後にやること」2 の手動手順で作ってください。
- `CctGlobalToggle` の Master Only は簡易的な制限です (厳密な権限管理には使わないでください)。
- `CctPickupResetter`:
  - 手に持たれている同期ピックアップを戻せるのは、持っている人のクライアントにイベントが届いた時だけです
    (持っている人が応答しない間は戻りません。所有権を横取りして無理に手から離すことはしません)。
  - 「置いてある」かどうかは押した人の画面の `VRCPickup.IsHeld` で判断します。他人が持っている物でも IsHeld が true に
    なる前提です (VRChat の Network Object Info に Held が同期情報として表示されるため) が、実機では未確認です。
  - ピックアップ側に所有権の移動を拒否する Udon (`OnOwnershipRequest` で false) がある場合、置いてある物は戻りません。
- `CctPlayerCounter` のログは各自が見た分だけです (後から来た人に過去のログは届きません)。
- `CctAreaToggle` の位置確認は Check Interval ごとなので、取りこぼしの補正には最大その秒数かかります。
  Exit を取りこぼしたままトリガーの近く (Exit Margin 以内) に立っている間は「中」のままです。
- `CctAudioVolume` を最初は非表示の GameObject に付け、しかも保存データがまだ無い場合は、その入室中は保存されません
  (OnPlayerRestored を受け取れないため。常に Active な GameObject に付ければ起きません)。

## トラブルシューティング

| 症状 | 対処 |
|---|---|
| "Unable to find valid U# program asset associated with script ..." / "U# behaviour is not pointing to a valid U# program asset" | Program Asset が無い。「インポート後にやること」2 の手順で作る |
| "Script X is referenced by 2 UdonSharpProgramAssets" | 1 つのスクリプトに Program Asset が複数ある。片方の .asset を削除する |
| Add Component に出てこない | C# のコンパイルエラーがある (Console を確認)。下の重複クラスも参照 |
| "The namespace 'Naoking.ClaudeWorldGimmicks' already contains a definition for 'Cct...'" (CS0101) | 同じサンプルを 2 か所に取り込んでいる (例: Package Manager の Samples と Assets/ClaudeWorldGimmicks)。片方のフォルダを削除する |
| "Script ... does not belong to a U# assembly" | .asmdef のあるフォルダに入れている。Assets/ClaudeWorldGimmicks など asmdef の外へ移す |
| 押しても反応しない | Collider が無い / レイヤーが UI など / スイッチ自身を非表示にした |
| UI ボタンが反応しない | Canvas に VRC Ui Shape が無い / Canvas のレイヤーが UI のまま / SendCustomEvent のメソッド名の打ち間違い |
| 同期しない (CctGlobalToggle) | GameObject が非アクティブ / VRC Object Sync と同居している / Build & Test で 2 クライアントにしていない |
| 音量が保存されない (CctAudioVolume) | Persistence Key が空 / CctAudioVolume を最初は非表示のパネルに付けている (常に Active な GameObject に移す) |
| エリアに入っても表示が変わらない (CctAreaToggle) | Collider の Is Trigger が OFF / レイヤーが Walkthrough (17) などプレイヤーと当たらないレイヤー / Inside Objects 未設定 |
| アバタープロジェクトでコンパイルエラー | このサンプルはワールド専用。取り込んだフォルダ (Assets/ClaudeWorldGimmicks) を削除する |
