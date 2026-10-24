// ============================================================================
// CctPickupResetter - ピックアップ (持てる物) を元の位置へ戻すボタン
// ----------------------------------------------------------------------------
// 目的:
//   Interact すると、登録した VRC Pickup を初期位置へ戻します。
//   - VRC Object Sync 付き (全員で位置が同期される物): その物の「持ち主」のクライアントだけが
//     Respawn() を実行して戻します (同期オブジェクトを動かせるのは持ち主だけ、という VRChat の規則)。
//   - VRC Object Sync 無し (各自の画面でバラバラに動く物): 全員が自分の画面のコピーを、
//     Start 時に記録した初期位置・回転へ戻します。
//   手に持たれている物は、持っている本人のクライアントで手から離して (Drop) から戻します。
//
// 設計 (競合に強い方式):
//   押した人は SendCustomNetworkEvent(All) で「リセットして」と全員に通知するだけで、
//   持ち主の横取り (SetOwner) はしません。各クライアントは受け取った時点で自分が持ち主の
//   同期ピックアップだけを処理します。
//   - 持ち主の移動をしないので、他の人が掴んだ瞬間と重なっても所有権の奪い合いが起きない。
//   - ピックアップを持っている人は必ずその持ち主なので、手から離す処理も本人の画面で正しく行われる。
//   - 2 人が同時に押しても、結果はどちらも「初期位置」なので同じになる。
//   - 例外: イベントが届く直前に持ち主が退出して所有権が移った場合など、まれに 1 個戻らない
//     ことがあります。その時はもう一度押してください (状態を壊すことはありません)。
//
// セットアップ:
//   1. ボタン役の GameObject (ピックアップとは別の物) に Collider を付け、CctPickupResetter を Add Component。
//      ※ VRC Object Sync が付いた GameObject には置かないこと。
//   2. Pickups に戻したい VRC Pickup を登録 (Object Sync 有り/無しを混在させて OK)。
//      VRC Pickup には Rigidbody と Collider が必要 (VRChat の仕様)。
//   3. Include Held: ON (既定) = 持たれている物も手から離して戻す / OFF = 持たれている物は戻さない。
//   4. このボタンの GameObject はワールド開始時から Active にしておく
//      (初期位置は Start で記録するため)。
//   UI ボタンから: On Click () → この GameObject → UdonBehaviour.SendCustomEvent → _RequestReset
//
// 同期:
//   NoVariableSync (同期変数なし、ネットワークイベントのみ使用)。
//   ネットワークイベントは後から参加した人には再送されませんが、Object Sync 付きの物の位置は
//   VRChat が同期するので問題ありません。Object Sync 無しの物は、後から来た人の画面では初期位置です。
//
// テスト手順:
//   ClientSim: ピックアップを動かす / 持ったまま → ボタン → 初期位置に戻る (持っていた物は手から離れる)。
//   Build & Test (2 クライアント):
//     A が同期ピックアップを持つ → B がボタン → A の手から離れて全員の画面で初期位置に戻る。
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

        /// <summary>UI ボタン用: 全員にリセットを依頼する (自分の画面では即時実行される)。</summary>
        public void _RequestReset()
        {
            if (Time.time - _lastRequestTime < cooldownSeconds) return;
            _lastRequestTime = Time.time;
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetResetPickups));
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
                    // 同期ピックアップ: 位置を決められるのは持ち主だけ。持ち主のクライアントだけが処理する。
                    if (!Networking.IsOwner(pickup.gameObject)) continue;
                    if (pickup.IsHeld)
                    {
                        if (!includeHeld) continue;
                        pickup.Drop(); // 持っているのは持ち主 = 自分
                    }
                    sync.Respawn();
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

        private void ResetLocal(int index, VRCPickup pickup)
        {
            pickup.transform.SetPositionAndRotation(_startPositions[index], _startRotations[index]);
            Rigidbody body = _bodies[index];
            if (body != null && !body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }
    }
}
