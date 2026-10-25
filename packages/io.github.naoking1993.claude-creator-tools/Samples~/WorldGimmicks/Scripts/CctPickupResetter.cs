// ============================================================================
// CctPickupResetter - ピックアップ (持てる物) を元の位置へ戻すボタン
// ----------------------------------------------------------------------------
// 目的:
//   Interact すると、登録した VRC Pickup を初期位置へ戻します。
//   - VRC Object Sync 付き (全員で位置が同期される物): 位置を決められるのは「持ち主」だけ
//     (VRChat の規則) なので、持ち主のクライアントで VRCObjectSync.Respawn() を実行します。
//   - VRC Object Sync 無し (各自の画面でバラバラに動く物): 全員が自分の画面のコピーを、
//     Start 時に記録した初期位置・回転へ戻します。
//
// 設計 (取りこぼしにくい方式):
//   押した人 (A) は次の 2 つを行います。
//   1. SendCustomNetworkEvent(All) で「リセットして」と全員に通知 (A 自身は即時実行)。
//      受け取った各クライアントは、
//        - Object Sync 無しの物: 自分の画面のコピーを戻す (自分が手に持っていれば Drop してから)。
//        - Object Sync 付きで自分が持ち主の物: 手に持っていれば Drop してから Respawn。
//          手に持っている人は必ずその物の持ち主なので、「誰かが持っている物」はこの経路で
//          持っている本人の画面で正しく手から離れます。
//   2. 誰にも持たれていない Object Sync 付きの物は、A 自身が Networking.SetOwner で持ち主になって
//      その場で Respawn します。
//      → 持ち主が退出した直後で持ち主が決まっていない、持ち主のクライアントが重い/応答しない、
//        といった場合でも、置いてあるだけの物は他の人のクライアントに頼らず押した人の画面で戻ります
//        (「持ち主だけが処理する」方式だと、こうした時に 1 個戻らないことがある)。
//   - 2 人が同時に押しても、どちらも「初期位置へ戻す」なので結果は同じです。
//   - A が持ち主を取るのと同時に別の人 C がその物を掴んだ場合は、最終的にどちらかが持ち主になります
//     (A なら初期位置、C なら C の手の中)。どちらでも状態が壊れることはありません。
//   - 手に持たれている物を戻せるのは、持っている人のクライアントにイベントが届いた時です
//     (持っている人が応答しない間は戻りません)。
//
// セットアップ:
//   1. ボタン役の GameObject (ピックアップとは別の物) に Collider を付け、CctPickupResetter を Add Component。
//      ※ VRC Object Sync が付いた GameObject には置かないこと。
//   2. Pickups に戻したい VRC Pickup を登録 (Object Sync 有り/無しを混在させて OK)。
//      VRC Pickup には Rigidbody と Collider が必要 (VRChat の仕様)。
//   3. Include Held: ON (既定) = 持たれている物も手から離して戻す / OFF = 持たれている物は戻さない。
//   4. このボタンの GameObject はワールド開始時から Active にしておく
//      (Object Sync 無しの物の初期位置は Start で記録するため)。
//   UI ボタンから: On Click () → この GameObject → UdonBehaviour.SendCustomEvent → _RequestReset
//
// 同期:
//   NoVariableSync (同期変数なし、ネットワークイベントのみ使用。None だとネットワークイベントが届かない)。
//   ネットワークイベントは後から参加した人には再送されませんが、Object Sync 付きの物の位置は
//   VRChat が同期するので問題ありません。Object Sync 無しの物は、後から来た人の画面では初期位置です。
//
// テスト手順:
//   ClientSim: ピックアップを動かす / 持ったまま → ボタン → 初期位置に戻る (持っていた物は手から離れる)。
//   Build & Test (2 クライアント):
//     A が同期ピックアップを持つ → B がボタン → A の手から離れて全員の画面で初期位置に戻る。
//     A が同期ピックアップを置く → A のクライアントを閉じる → B がボタン → 初期位置に戻る。
//     Object Sync 無しの物: 各クライアントで別々に動かしてからボタン → 両方とも初期位置に戻る。
//     Include Held を OFF → 持たれている物だけ戻らないこと。
// ============================================================================
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace Naoking.ClaudeWorldGimmicks
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class CctPickupResetter : UdonSharpBehaviour
    {
        [Tooltip("初期位置へ戻す VRC Pickup。VRC Object Sync 付き (全員同期) と無し (各自ローカル) を混在させて OK")]
        public VRCPickup[] pickups;

        [Tooltip("ON: 手に持たれている物も手から離して戻す。OFF: 持たれている物はそのまま")]
        public bool includeHeld = true;

        [Tooltip("連打防止の待ち時間 (秒)")]
        public float cooldownSeconds = 1f;

        private VRCObjectSync[] _syncs;
        private Rigidbody[] _bodies;
        private Vector3[] _startPositions;
        private Quaternion[] _startRotations;
        private bool _initialized;
        private float _lastRequestTime = -1000f;

        private void Start()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            int count = pickups == null ? 0 : pickups.Length;
            _syncs = new VRCObjectSync[count];
            _bodies = new Rigidbody[count];
            _startPositions = new Vector3[count];
            _startRotations = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                VRCPickup pickup = pickups[i];
                if (pickup == null) continue;
                _syncs[i] = pickup.GetComponent<VRCObjectSync>();
                _bodies[i] = pickup.GetComponent<Rigidbody>();
                _startPositions[i] = pickup.transform.position;
                _startRotations[i] = pickup.transform.rotation;
            }
        }

        public override void Interact()
        {
            _RequestReset();
        }

        /// <summary>UI ボタン用: 全員にリセットを依頼し、置いてある同期ピックアップは自分で戻す。</summary>
        public void _RequestReset()
        {
            if (Time.time - _lastRequestTime < cooldownSeconds) return;
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;
            _lastRequestTime = Time.time;
            Initialize();

            // 1. 全員へ通知 (自分の画面では今この場で NetResetPickups が実行される)。
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetResetPickups));

            // 2. 誰にも持たれていない同期ピックアップで、まだ自分が持ち主でない物は、持ち主になって戻す。
            //    (自分が持ち主の物は 1 の中で処理済み。持たれている物は持っている人が 1 で処理する)
            int count = pickups == null ? 0 : pickups.Length;
            for (int i = 0; i < count; i++)
            {
                VRCPickup pickup = pickups[i];
                VRCObjectSync sync = _syncs[i];
                if (pickup == null || sync == null) continue;
                if (pickup.IsHeld) continue;
                GameObject target = pickup.gameObject;
                if (Networking.IsOwner(local, target)) continue;
                Networking.SetOwner(local, target); // ローカルでは即座に持ち主になる
                RespawnSynced(i, sync);
            }
        }

        /// <summary>ネットワークイベント (全員で実行)。直接呼ばず _RequestReset を使ってください。</summary>
        [NetworkCallable]
        public void NetResetPickups()
        {
            Initialize();
            int count = pickups == null ? 0 : pickups.Length;
            for (int i = 0; i < count; i++)
            {
                VRCPickup pickup = pickups[i];
                if (pickup == null) continue;

                VRCObjectSync sync = _syncs[i];
                if (sync != null)
                {
                    // 同期ピックアップ: 位置を決められるのは持ち主だけ。自分が持ち主の物だけ処理する。
                    if (!Networking.IsOwner(pickup.gameObject)) continue;
                    if (pickup.IsHeld)
                    {
                        if (!includeHeld) continue;
                        pickup.Drop(); // 持っているのは持ち主 = 自分
                    }
                    RespawnSynced(i, sync);
                }
                else
                {
                    // ローカルピックアップ: 各クライアントが自分の画面のコピーを戻す。
                    if (pickup.IsHeld)
                    {
                        if (!includeHeld) continue;
                        pickup.Drop();
                    }
                    ResetLocal(i, pickup);
                }
            }
        }

        private void RespawnSynced(int index, VRCObjectSync sync)
        {
            sync.Respawn();
            // 手から離した時の投げる勢いや回転が残らないようにする (持ち主なので物理を操作してよい)。
            StopBody(_bodies[index]);
        }

        private void ResetLocal(int index, VRCPickup pickup)
        {
            pickup.transform.SetPositionAndRotation(_startPositions[index], _startRotations[index]);
            StopBody(_bodies[index]);
        }

        private void StopBody(Rigidbody body)
        {
            if (body == null || body.isKinematic) return;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }
}
