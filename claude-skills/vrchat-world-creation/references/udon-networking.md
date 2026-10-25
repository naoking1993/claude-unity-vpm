# UdonSharp とネットワーク同期

出典と時点：**SDK 3.10.5 に同梱の UdonSharp（`Packages/com.vrchat.worlds/Integrations/UdonSharp/`）と VRChat 公式クリエイタードキュメントの 2026-10-20 時点のスナップショット**による。`docs/…`・`releases/…` は GitHub `vrchat-community/creator-docs` の `Docs/` 以下。`U#/…` は `Packages/com.vrchat.worlds/Integrations/UdonSharp/` の略。単体版 UdonSharp 1.1.9 と古い GitHub wiki は 3.10.5 と食い違うので根拠にしない。

確度：「確認済み」はソース・docs で本文を確かめたもの、「推定」はそこからの推論、「未確認」は確かめられなかったもの。Unity 実機・VRChat 実機での動作確認はしていない。

## 目次

1. UdonSharp の基本
2. 使える C# と使えない C#
3. 同期方式
4. 同期変数
5. オーナーと移譲
6. シリアライズのイベント
7. 後から入った人
8. ネットワークイベント
9. 帯域とサイズの上限
10. 永続化（PlayerData / PlayerObject）
11. ワールド更新時の互換性
12. よくある不具合と調べ方

---

## 1. UdonSharp の基本

- `UdonSharpBehaviour` を継承し、**クラス名とファイル名を一致**させる（`docs/worlds/udon/udonsharp/migration.md` L66-68）。
- 各 `.cs` に対応する **UdonSharp Program Asset（`.asset`）がちょうど1つ**必要。2つあると "Script X is referenced by N UdonSharpProgramAssets…" のエラー（`U#/Editor/Compiler/UdonSharpCompilerV1.cs` L370-378）。
- Program Asset は「Create > U# Script」（Project ウィンドウ）か、UdonBehaviour の Inspector の New Program → Create Script で新規作成したときにだけ自動で作られる（`docs/worlds/udon/udonsharp/index.md` L17-36）。既存の `.cs` をコピーしても作られない。無いままコンポーネントを付けると "Unable to find valid U# program asset associated with script…"（`U#/Editor/UdonSharpEditorUtility.cs` L888-895）。手動では「Create > VRChat > Udon > Udon C# Program Asset」で作り、Source Script に `.cs` を入れる。
- U# がコンパイルするのは、asmdef に属さない Assets 内のスクリプトと、**U# Assembly Definition が付いた asmdef** に属するスクリプトだけ（`U#/Editor/Compiler/CompilationContext.cs` L230-326）。asmdef の中に U# Assembly Definition の無い U# スクリプトがあると "does not belong to a U# assembly" になり、**プロジェクト全体の U# コンパイルが止まる**。ユーザーの泡ワールドでは Console に出ず Editor.log にだけ出た（LTCGI の U# Assembly Definition 欠落、[台帳](verified-facts.md)）。U# Assembly Definition は asmdef を選んで「Create > U# Assembly Definition」で作る。
- コンパイル後のプログラムは `Assets/SerializedUdonPrograms/<Program Asset の GUID>.asset` にできる（`Packages/com.vrchat.worlds/Editor/Udon/ProgramSources/UdonProgram/UdonProgramAsset.cs` L35-62）。
- U# のコンパイルが自動で走らないときは `VRChat SDK > Udon Sharp > Refresh All UdonSharp Assets`、または Program Asset の Inspector の Compile All UdonSharp Programs。
- エディタからコンポーネントとして付けると、U# が裏の `UdonBehaviour` を用意する。MCP で付けた直後は初期化されていないので、その GameObject を選択して Inspector を表示するか、Play に入る・シーンを開き直す（同梱サンプルの README。Console の "has not been fully setup, running setup" は正常）。公開 API `UdonSharpEditor.UdonSharpUndo.AddComponent<T>(gameObject)` / `gameObject.AddUdonSharpComponent<T>()` は内部で裏の UdonBehaviour との紐付けまで行う（`U#/Editor/Editors/UdonSharpUndo.cs` L40-67、`UdonSharpComponentExtensions.cs` L16-35）。型名を書けない `execute_code` からは、リフレクションで取った `Type` を渡す非ジェネリック版 `UdonSharpUndo.AddComponent(GameObject, Type)` が使える（MCP からの実行は未検証）。`UdonSharpEditorUtility.ConvertToUdonBehaviours` は中身が `NotImplementedException` で使えない。
- Udon は通常の C# の 200〜1000 倍遅い。毎フレームの大きなループを避け、`Networking.LocalPlayer` や `transform` は `Start` でキャッシュする（`docs/worlds/udon/udonsharp/performance-tips.md` L7-13）。
- 3.10.5 から、U# でも Assembly Version Defines が使える。asmdef の Version Defines に SDK の版の条件と記号を書いたとき（例：3.10 を条件に `SDK_310_OR_NEWER`）だけ、その記号で `#if` できる。記号は最初から定義されてはいない（`releases/release-3.10.5.md` L29-30）。asmdef に入れた U# には U# Assembly Definition も要る（下の注意）。

---

## 2. 使える C# と使えない C#

U# のコンパイラは C# 9 で構文を読み、処理を実装した構文だけを受け付ける（`U#/Editor/Compiler/Binder/BinderSyntaxVisitor.cs`）。それ以外は "UdonSharp does not currently support node type…" になる。公開（expose）されていない API は "Method is not exposed to Udon" になる。公開状況はエディタの `VRChat SDK > Udon Sharp > Class Exposure Tree` で見られる（`docs/worlds/udon/udonsharp/class-exposure-tree.md`）。

| 機能 | 可否 | 根拠・補足 |
| --- | --- | --- |
| if/else、for、while、do、foreach（配列・string・Transform の子）、switch 文、三項演算子、`??`、文字列補間、`nameof` | ○ | 確認済み |
| ユーザー定義メソッド（引数・戻り値・`out`/`ref`・既定値・`params`）、メソッドのオーバーロード | ○ | テストで確認済み。docs の FAQ「オーバーロード不可」は古い。ただし `[NetworkCallable]` のメソッドはオーバーロード不可 |
| プロパティ（get/set） | ○ | テストで確認済み（docs の FAQ「不可」は古い） |
| 継承・virtual・abstract・override、partial class | ○ | abstract の U# クラスは Program Asset を持てない |
| 再帰 | 条件付き | 再帰するメソッドに `[RecursiveMethod]` |
| ユーザー定義 enum | ○ | 名前空間の直下に置く（クラス内のネスト型は不可）。同期も可 |
| 配列、ジャグ配列 `T[][]` | ○ | |
| try/catch/finally、throw | × | 確認済み |
| ラムダ、匿名メソッド、ローカル関数、LINQ | × | ローカル関数は確認済み、他は推定 |
| interface の宣言・実装、ジェネリッククラス、U# クラス上のジェネリックメソッド | × | 確認済み。非 U# の static クラスの static ジェネリックメソッド・拡張メソッドは可 |
| U# クラスのコンストラクタ | × | 初期化は `Start` かフィールド初期化子（初期化子はコンパイル時に評価される） |
| static フィールド（`const` 以外。`static readonly` も不可） | × | 確認済み |
| ネスト型 | × | 確認済み |
| 多次元配列 `T[,]` | × | ジャグ配列を使う |
| `List<T>`・`Dictionary<K,V>` | × | `VRC.SDK3.Data.DataList` / `DataDictionary` を使う |
| オブジェクト初期化子 `new X { a = 1 }` | × | 配列初期化子は可 |
| `is` / `as`、`?.`、パターンマッチ、switch 式、`using` 文、`yield`、`async` | × | 推定（処理が無い）。使わない |
| ユーザー定義型への `typeof` | × | U# の型判定は `GetUdonTypeID<T>()` |
| `Instantiate` | GameObject だけ | `VRCInstantiate` は廃止 |
| 他の U# のフィールドの読み書き | ○（遅い） | `[FieldChangeCallback]` 付きのフィールドへ外から代入するとコンパイルエラー |
| U# を `Behaviour` 型で持って `enabled` を切り替える | × | フィールドは自作の U# 型で宣言する |

### 2.1 イベント名と SendCustomEvent

- `SendCustomEvent("名前")`、UI のイベント、遅延イベントで呼ぶメソッドは **public かつ引数0個**にする。引数のあるメソッドは内部名が `__0_名前` のように変わるので文字列では呼べない（`U#/Editor/Compiler/CompilationContext.cs` L460-466）。
- 名前が `_` で始まる public メソッドは、ネットワーク越しには呼ばれない。ローカルの SendCustomEvent からは呼べる。同梱ギミックはこれを「ローカル専用」の印に使っている（`docs/worlds/udon/networking/events.md` L288-296）。この判定は `[NetworkCallable]` の無い旧来のイベントに対するもので、`[NetworkCallable]` を付けたメソッドは `_` で始まってもネットワーク越しに呼ばれる（ClientSim の実装 `ClientSimNetworkCalling.cs` L49-56 で確認。クライアント本体は推定）。呼ばれたくないメソッドに `[NetworkCallable]` を付けない。
- 組み込みイベント（`Interact`、`OnPlayerJoined` など）は名前と引数型の一致で判定される。同名で引数型だけ違うメソッドはコンパイルエラー。
- 外から呼ばないメソッドは private にする（public が少ないほどイベント検索が速い。`performance-tips.md` L16-17）。

### 2.2 主なイベントの発火条件（`docs/worlds/udon/graph/event-nodes.md`）

| イベント | 発火 |
| --- | --- |
| `Interact` | **ローカルプレイヤー**がインタラクトしたとき。**Collider と UdonBehaviour の両方**が要る |
| `OnPickup` / `OnDrop` / `OnPickupUseDown` / `OnPickupUseUp` | ローカルプレイヤーの操作でだけ。Use 系は Desktop では Auto Hold が要る |
| `OnPlayerJoined` | 自分が入ったときは既にいる全員（自分を含む）の分、その後は入った人の分 |
| `OnPlayerLeft` | 誰かが抜けたとき。マスターが抜けると、その前に新しいマスターが選ばれる |
| `OnPlayerTriggerEnter/Stay/Exit` | **どのプレイヤー**のカプセルでも発火（Is Trigger の Collider が要る）。自分だけの処理は `player.isLocal` で絞る |
| `OnPlayerCollisionEnter/Stay/Exit` | 動いている物体に限る。静止物に歩いてぶつかっても発火しない（`docs/worlds/udon/players/player-collisions.md` L34-36） |
| `OnStationEntered/Exited` | ローカルとリモートの両方で発火 |
| `OnPlayerRestored` | そのプレイヤーの永続データの読み込みが終わったとき |
| `OnOwnershipTransferred` | オーナーが変わったとき、全員で発火 |

Udon は Start より前に届いたイベントを捨てる（`RunEvent` は `canRunBeforeStart: false`。`Packages/com.vrchat.worlds/Runtime/Udon/UdonBehaviour.cs` L1331-1332）。最初は非アクティブな GameObject のスクリプトは、アクティブになって Start が走るまでイベントを受け取らない（同梱サンプルの README）。Trigger の Enter/Exit はテレポートや高速移動で取りこぼされることがある（`docs/worlds/udon/players/player-collisions.md` L20-23）。

---

## 3. 同期方式（`[UdonBehaviourSyncMode(BehaviourSyncMode.X)]`）

| 方式 | 意味 |
| --- | --- |
| `Any`（属性なしの既定） | 何も強制しない。Inspector で選ぶ |
| `None` | 同期変数を禁止。**SendCustomNetworkEvent も使えない** |
| `NoVariableSync` | 同期変数は禁止、ネットワークイベントは使える。同じ GameObject の Manual / Continuous に合わせる |
| `Continuous` | 自動で頻繁に送る。取りこぼし得る。補間（Linear / Smooth）可、**配列は不可**、1回約 **200 バイト** |
| `Manual` | オーナーが `RequestSerialization()` したときに送る。確実に届く。**配列可**、補間不可、1回約 **280,496 バイト** |

出典：`docs/worlds/udon/udonsharp/attributes.md` L61-67、`docs/worlds/udon/networking/network-details.md` L14-37、`U#/Runtime/UdonSharpAttributes.cs` L43-83。

- 頻繁に変わり途中の値がどうでもよいもの（位置など）は Continuous、変化は少ないが途中の値が大事なもの（スコア、盤面、扉の開閉）は Manual。
- 1つの GameObject の同期する UdonBehaviour は同じ方式に揃えられ、Manual と Continuous が混ざると両方 Manual 扱い（`network-details.md` L90-93）。
- **VRCObjectSync と Manual の UdonBehaviour を同じ GameObject に置かない**（SDK のビルドエラー。[セットアップ](setup-and-upload.md) 4.3 の C7）。Manual のスクリプトは別の GameObject に置く。

---

## 4. 同期変数（`[UdonSynced]`）

- 同期できる型：`bool, char, byte, sbyte, short, ushort, int, uint, long, ulong, float, double, string`、`Color, Color32, Vector2, Vector3, Vector4, Quaternion`、`VRCUrl`、それぞれの配列、ユーザー定義 enum（`Packages/com.vrchat.worlds/Runtime/Udon/UdonNetworkTypes.cs` L13-54）。**GameObject・Component などのシーン参照は同期できない**（`docs/worlds/udon/networking/variables.md` L37）。
- `[UdonSynced]` は補間なし。`[UdonSynced(UdonSyncMode.Linear)]` / `Smooth` は Continuous でだけ使える。
- **同期する配列は必ず初期化する**（`new int[0]` など）。未初期化の配列が1つでもあると、その UdonBehaviour 全体が同期されない（`network-details.md` L95-98）。
- 同期変数を書き換えられるのは**その GameObject のオーナーだけ**（`variables.md` L33-35）。
- データコンテナ（DataList / DataDictionary）は直接同期できない。JSON 文字列にして `[UdonSynced] string` で送る（`docs/worlds/udon/data-containers/data-dictionaries.md` L193-227）。

---

## 5. オーナーと移譲

- 最初に入った人が全ネットワークオブジェクトの初期オーナー。オーナーが抜けると自動で別の人に移る。オーナーを一度も変えていない物はマスターがオーナー。誰が次のマスターになるかを当てにしない。Android ではアプリを長くバックグラウンドに置くとマスターが変わることがある（`docs/worlds/udon/networking/ownership.md` L7-10, L60-75）。
- **権限の判定にマスターを使わない**。`IsInstanceOwner` か独自の仕組みを使う（`ownership.md` L62-81、`network-components.md` L14-16）。`IsInstanceOwner` は Invite / Friends 系のインスタンス作成者だけ true で、Group・Public・Build & Test では常に false。
- 移す：`Networking.SetOwner(Networking.LocalPlayer, gameObject)`。SDK 同梱サンプル `GlobalToggleObject` は SetOwner の直後に値を変えて `RequestSerialization()` している（`U#/Samples~/Utilities/Synced/GlobalToggleObject.cs`）。この書き方に従う。
- `OnOwnershipRequest(requestingPlayer, requestedOwner)` は移譲の前に呼ばれ、true で承認・false で拒否。**要求した側と現オーナーの両方で実行される**ので、両者の判定がずれると壊れる（`ownership.md` L34-43、両方で実行される点は `network-components.md` L59-60）。
- オーナーを移さずに済ませる：`SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(OpenDoor))` でオーナーに頼み、オーナーが値を変えて送る（`docs/worlds/udon/networking/late-joiners.md` L43-51）。
- 移譲は頻繁にしない（遅延と同期ずれの原因。`performance.md`）。

---

## 6. シリアライズのイベント（`docs/worlds/udon/networking/network-components.md` L32-73）

- `RequestSerialization()`：**Manual で、オーナーが呼んだときだけ**効く。何回呼んでもよく、Udon が十分な時間をおいてから `OnPreSerialization` → 送信 → `OnPostSerialization(result)` の順に処理する（連続で呼んでも最後の値がまとめて送られると推定。`network-details.md` L35）。
- `OnPreSerialization`：送信の直前。同期変数に最終的な値を詰める。
- `OnDeserialization` / `OnDeserialization(DeserializationResult)`：受信して値を展開した後。**どの変数が変わったかは分からない**。前回の値と比べるか、`[FieldChangeCallback]` を使う。`isFromStorage` が true なら永続データからの復元。
- `OnPostSerialization(SerializationResult)`：`success` と `byteCount`（実際の送信量）。
- オーナー自身のクライアントでは `OnDeserialization` は呼ばれないと推定。値を変えた時点で自分の表示も直接更新する（SDK 同梱サンプルの書き方）。
- `[FieldChangeCallback(nameof(Prop))]`：同期や `SetProgramVariable` で値が来ると、フィールドに直接入れる代わりにプロパティの setter が呼ばれる。同じクラス内でフィールドに直接代入しても setter は呼ばれないので、自分で変えるときもプロパティ経由で代入する。

最小の形（Manual 同期のトグル。根拠は SDK 同梱 `GlobalToggleObject.cs`。外部でのコンパイル確認のみ、Unity 実機は未検証）：

```csharp
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class SyncedToggle : UdonSharpBehaviour      // ファイル名 SyncedToggle.cs
{
    [SerializeField] private GameObject target;
    [UdonSynced, FieldChangeCallback(nameof(IsOn))] private bool _isOn;

    public bool IsOn { get => _isOn; set { _isOn = value; target.SetActive(value); } }

    private void Start() { _isOn = target.activeSelf; }  // 初期値はシーンの状態（SDK サンプルと同じ）

    public override void Interact()
    {
        if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
        IsOn = !IsOn;            // 自分で変えるときもプロパティ経由
        RequestSerialization();  // Manual：オーナーが送信を頼む
    }
}
```

同梱の `CctGlobalToggle` はこれに Late Joiner・オーナー退出時の送り直し・マスター限定を足したもの（[レシピ](gimmick-recipes.md)）。

---

## 7. 後から入った人（`docs/worlds/udon/networking/late-joiners.md`）

- 入ると最新の同期変数を受け取り、`OnDeserialization` が呼ばれる。オーナーも正しく設定される。**ネットワークイベントは再送されない**（L7-9）。
- 新しい人の入室時に `RequestSerialization` を呼び直す必要はない（`variables.md` L36）。
- **状態（ON/OFF、スコア、進行）は同期変数で持ち、一瞬の出来事（リセット、効果音）はイベントで扱う**。状態をイベントで管理すると、後から入った人だけずれる。

---

## 8. ネットワークイベント（`docs/worlds/udon/networking/events.md`）

- 送り方：`SendCustomNetworkEvent(NetworkEventTarget.X, nameof(Method))`。3.8.1 以降は引数を最大8個付けられる（`SendCustomNetworkEvent(target, name, p0, …)`、`releases/release-3.8.1.md` L20-24）。`NetworkCalling.SendCustomNetworkEvent(this, …)` も同じ動作。
- 送り先 `NetworkEventTarget`：`All`、`Owner`、`Others`、`Self`（Others と Self は 3.8.1 追加）。自分にも届くイベントは送信時にその場で実行される（L98）。特定の人だけに送るには playerId を引数に入れて All で送り、受け側で `Networking.LocalPlayer.playerId` と比べる（L117）。
- 受け側の条件（L30-43）：UdonBehaviour の同期方式が **None 以外**。メソッドは public、名前が `_` で始まらない（`[NetworkCallable]` なら `_` でも可）。**引数付きなら `[NetworkCallable]` が必須**（`VRC.SDK3.UdonNetworkCalling`）。static / virtual / override 不可、オーバーロード不可、戻り値 void、引数は同期できる型・8個まで、`ref`/`out`/`params`/既定値は不可。
- レート制限（L212-236）：`[NetworkCallable]` の既定 **5回/秒**、指定できる最大 **100回/秒**（`[NetworkCallable(maxEventsPerSecond: 2)]` のように書く）。送信側とサーバーの両方で適用され、超えた分は**上限なしで積まれる**。送り続けるとワールド全体の Udon ネットワークに悪影響。自分へのイベントには掛からない。全体でも約100イベント/秒。値はできるだけ低くする。
- サイズ（L171-190）：1イベントの引数合計 **16KB まで**。送信全体は約 18KB/s で頭打ち（実際は 8〜10KB/s 程度）。引数が 1024 バイトを超えると内部で分割され、分割数だけレート制限に数えられる。string は UTF-8 で数える。
- 送った側は相手の実行を待たない。送信順はレート制限に掛からない限り保たれる（複数人が同時に送った場合は保証なし）（L100-102）。
- `NetworkCalling.CallingPlayer` で送信者、`NetworkCalling.InNetworkCall` で「ネットワーク呼び出しの中か」が分かる（L277-284）。
- 引数なし・`[NetworkCallable]` 無しの旧来のイベントは GameObject 単位で、同じ GameObject 上の全 UdonBehaviour の同名メソッドが呼ばれる。`[NetworkCallable]` はコンポーネント単位で、並び順に依存するので、ネットワークで使う UdonBehaviour を Destroy しない（L298-306）。
- 新旧のワールドバージョンが同じインスタンスに混ざると、サーバーがイベントを黙って捨てることがある（L273-275）。

---

## 9. 帯域とサイズの上限（`docs/worlds/udon/networking/network-details.md`）

| 項目 | 値 |
| --- | --- |
| Udon の送信量 | 約 **11KB/秒** |
| Manual の1回のシリアライズ | 約 **280,496 バイト** |
| Continuous の1回のシリアライズ | 約 **200 バイト** |
| ネットワークイベントの引数 | 1イベント 16KB まで、全体約 18KB/s（実際 8〜10KB/s） |

- 超えると `Networking.IsClogged` が true になる。Continuous はイベントが送れずログにエラー、Manual は保留して後で再送。どちらも UdonBehaviour の処理は続くが、データは送受信されない（L18-21）。
- `bool` も最低1バイト＋オーバーヘッド。実際の送信量は `OnPostSerialization` の `byteCount` で見る。型ごとのサイズは L47-87（string は1文字2バイト、Vector3 は12バイト、Quaternion は16バイトなど）。
- 画面に映っている同期オブジェクトが優先して送られる（L102-106）。
- 予測できる動き（決まった経路の移動など）は、位置を送らず初期値と開始時刻だけ送る設計にする（L23）。
- オブジェクトごとの送信量は World Debug View の 6（Net Objects）で見られる（[テストとデバッグ](testing-and-debugging.md) 5章）。

---

## 10. 永続化（PlayerData / PlayerObject）の要点

`docs/worlds/udon/persistence/index.md`、`player-data.md`、`player-object.md`（3.7.4 で追加）。

- **PlayerData**：プレイヤーごとのキーと値。`PlayerData.SetInt("key", v)` などの Set は**ローカルプレイヤー自身にだけ**書ける。Get / TryGet は誰のデータでも読める。キーは削除できない。存在しない・型が違うキーはその型の既定値が返る（区別するなら TryGet）。
- **PlayerObject**：GameObject に `VRCPlayerObject` を付けると、入ったプレイヤーごとに複製される。同じ GameObject に `VRCEnablePersistence` を付けると、その上の UdonBehaviour の同期変数が保存される。オーナーは固定で他人に移せない。**シーン側からテンプレートを参照しない**（VRChat が自動で無効化する）。参照は `Networking.FindComponentInPlayerObjects(player, テンプレート上のコンポーネント)` で取る。
- **上限**：1ワールド・1プレイヤーあたり PlayerData **100KB** と PlayerObject **100KB**（圧縮後）。超える保存はエラーログを出して保存されない（L41-47）。
- **読み書きは `OnPlayerRestored` の後**にする。前に書くと、後から届いたサーバーのデータで上書きされる恐れがある。**ローカルプレイヤーの `OnPlayerLeft` の中では保存できない**。ワールド間でデータは共有できず、セーブスロットの仕組みも組み込みでは無い（Udon で自作はできる。`persistence/index.md` L49-50）。
- どれか1つのキーを変えるとその人の PlayerData 全体が送られる。頻繁に変わる・大きなデータは PlayerObject に分ける。プレハブを配る人はキーに固有の接頭辞を付ける（L52-64）。
- 保存先：アップロード済みはサーバー、Build & Test はローカル（クライアントを閉じると消える）、ClientSim はプロジェクト内の JSON（L26-39）。Clients 2 以上の Build & Test ではクライアントごとに別保存（L36）。
- 3.10.0 で使用量の API（`GetPlayerDataStorageUsage/Limit`、`OnPlayerDataStorageWarning/Exceeded` など）が追加された。
- `VRC_ENABLE_PLAYER_PERSISTENCE`：SDK の `EnvConfig` が、ClientSim の型があればこの定義シンボルを足すので、ワールドプロジェクトでは定義される（`Packages/com.vrchat.base/Editor/VRCSDK/Dependencies/VRChat/EnvConfig.cs` L1310-1322。クライアント側は未確認）。`OnPlayerDataUpdated` の `override` がコンパイルエラーになる場合は、`override` を外しても U# は名前と引数型でイベントと認識する（推定）。

---

## 11. ワールド更新時の互換性（`docs/worlds/udon/networking/compatibility.md` L8-14）

同期するオブジェクトの**コンポーネントの型・数・順序**、または**同期変数の型・数・順序**が前のバージョンと変わると、前のバージョンで開かれたインスタンスとは互換性が無くなり、入ろうとした人は退出させられる。公開中のワールドで同期まわりを変えるときは、この影響を本人に伝える。

---

## 12. よくある不具合と調べ方

| 症状 | 主な原因 | 対策 | 確度 |
| --- | --- | --- | --- |
| 値を変えたのに他の人に反映されない | オーナーでないクライアントで書き換えている／Manual なのに `RequestSerialization` を呼んでいない／同期方式が None | `SetOwner` してから書き換え、Manual なら `RequestSerialization()`。方式を確かめる | 確認済み |
| 自分の画面だけ変わり、後で元に戻る | オーナーでない側の書き換えが次の受信で上書きされた | 同上 | 推定 |
| 後から入った人だけずれる | 状態をイベントで管理している | 同期変数＋`OnDeserialization` / FieldChangeCallback | 確認済み |
| 同期変数を変えてすぐ送ったイベントの方が先に届く | イベントは同期変数より速く届くことが多い | 値をイベントの引数で送るか、受け側は同期変数の変化で処理 | 推定 |
| 配列を同期しているのに何も同期されない | 同期配列が未初期化（null） | 宣言時に初期化 | 確認済み |
| SendCustomNetworkEvent が届かない | private、`_` 始まり、受け側が None、引数付きなのに `[NetworkCallable]` 無し、`SendCustomEvent` で引数付きメソッドを呼んだ | 2.1・8章の条件 | 確認済み |
| ObjectSync の物が同期しない | VRCObjectSync が無い、オーナーの移譲が正しくない | 追加し、`Networking.GetOwner(gameObject).displayName` をログに出して確かめる（`docs/worlds/udon/networking/debugging.md`） | docs |
| Manual のスクリプトと ObjectSync が干渉する | 同じ GameObject に置いた | 別の GameObject へ（ビルドも止まる） | 確認済み |
| OnPlayerTriggerEnter で全員がテレポートしようとする | 全プレイヤー分発火している | `if (!player.isLocal) return;` | 確認済み |
| TeleportTo が効かない | ローカルプレイヤー以外に使った、Station に座っている、`OnDeserialization` の中で呼んだ | ローカルプレイヤーだけに使う。OnDeserialization からは `SendCustomEventDelayedFrames` で1フレーム遅らせる（`docs/worlds/udon/players/player-positions.md` L77-94） | 確認済み |
| 保存した値が戻らない | `OnPlayerRestored` の前に読み書き、最初は非アクティブな GameObject で `OnPlayerRestored` を受け取れない、Build & Test でクライアントを閉じた | 2.2・10章 | 確認済み |
| 公開中のワールドを更新したら旧インスタンスに入れない | 同期の構成を変えた | 11章 | 確認済み |

### 12.1 確かめ方

- **ClientSim**：ローカルのプレイヤーだけを再現する。リモートプレイヤーへの同期は再現しない（Esc メニューで追加したリモートプレイヤーにはデシリアライズが起きない。`docs/worlds/clientsim/index.md` L38-44）。`All` / `Owner` / `Self` のイベントはローカルで実行され、**`Others` は何もしない**。None の UdonBehaviour へのイベント、`_` 始まりの旧来イベント、`[NetworkCallable]` 無しの引数付きイベントはエラーになる（`Packages/com.vrchat.worlds/Integrations/ClientSim/Runtime/Networking/ClientSimNetworkCalling.cs`）。`OnPostSerialization` / `OnDeserialization` の値も実機と違う。→ ClientSim で同期の合否を決めない。
- **Build & Test（Clients 2 以上）**：A で操作 → B に反映、B で操作 → A に反映、A で状態を変えた後に B を入り直す（後から入った人）、オーナーのクライアントを閉じる（オーナー退出）、の順に確かめる。
- **ログ**：送受信の要所に `Debug.Log` を入れ、output_log の Udon の例外（"halted"）を探す（[テストとデバッグ](testing-and-debugging.md) 6章）。
- **デバッグビュー**：6（Net Objects）でオーナーと送信量、オーバーレイ 8（Network Object Info）でオブジェクトの同期状態を見る（ワールド作者か World Debugging 有効時）。

### 12.2 スクリプトを渡す前のチェック

- [ ] `UdonSharpBehaviour` を継承、クラス名＝ファイル名、Program Asset が1つ
- [ ] asmdef の中に置くなら U# Assembly Definition がある（無ければ asmdef の外に置く）
- [ ] 2章の「×」の構文を使っていない
- [ ] 同期変数：Manual なら配列を初期化、Continuous なら配列なし・200 バイト以内
- [ ] 値を変える前に `SetOwner`、Manual なら `RequestSerialization()`
- [ ] ネットワークイベントの受け側は public・`_` で始まらない。引数付きなら `[NetworkCallable]`、オーバーロードなし、レートは低め
- [ ] UI から呼ぶメソッドは public・引数0個
- [ ] `OnPlayerTriggerEnter` などは `player.isLocal` で絞り、`TeleportTo` はローカルプレイヤーだけ
- [ ] 永続化は `OnPlayerRestored` の後
- [ ] 保存したプレイヤー参照は使う前に `Utilities.IsValid(player)`（`docs/worlds/udon/players/index.md` L22-50）
