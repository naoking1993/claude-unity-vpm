// ============================================================================
// CctGlobalToggle - 全員で共有される ON/OFF スイッチ (後から入った人にも反映)
// ----------------------------------------------------------------------------
// 目的:
//   誰かが Interact すると、インスタンス内の全員の画面で登録した GameObject が
//   ON/OFF されます。後から参加した人 (Late Joiner) にも現在の状態が反映されます。
//   照明、扉、共有の装飾などに使います。
//
// セットアップ:
//   1. スイッチ役の GameObject に Collider を付ける (Is Trigger OFF で OK)。
//      レイヤーは Default か Interactive (8) を推奨。
//   2. その GameObject に CctGlobalToggle を Add Component する。
//   3. Targets / Invert Targets を登録 (CctLocalToggle と同じ考え方)。
//   4. Synced Is On のチェックが「インスタンス開始時の状態」になります。
//   5. Master Only を ON にすると、インスタンスマスターだけが切り替えられます
//      (他の人にはスイッチがインタラクト不可になります)。
//   6. このスクリプトを載せた GameObject は常に Active にしておく
//      (非アクティブだと同期を受け取れません)。また VRC Object Sync と同じ
//      GameObject には置かないこと (Manual 同期と ObjectSync の同居は SDK のビルドエラー)。
//   UI ボタンから: On Click () → この GameObject → UdonBehaviour.SendCustomEvent → _Toggle / _TurnOn / _TurnOff
//
// 同期 (Manual):
//   - 押した人が Networking.SetOwner で持ち主になり、値を書き換えて RequestSerialization。
//     同期変数を書けるのは持ち主だけ、という VRChat のルールに従っています。
//   - 受け取った側は FieldChangeCallback (IsOn の setter) で即座に反映。
//     念のため OnDeserialization でも再適用します (冪等なので二重適用しても問題なし)。
//   - Late Joiner には VRChat が最新の同期値を自動で送るので、追加処理は不要。
//   - 持ち主が退出すると VRChat が自動で新しい持ち主を決めます。新しい持ち主になった
//     クライアントは OnOwnershipTransferred で現在値を送り直し、全員の状態を揃えます。
//   - ほぼ同時に 2 人が押した場合は、最後に持ち主として確定した人の値に揃います。
//   - Master Only はローカル側での制限です。インスタンスマスターは途中で変わることがあり
//     (OnMasterTransferred で追従)、VRChat 公式も「マスターで権限管理しない」ことを
//     推奨しているので、厳密な権限が必要な用途には使わないでください。
//
// テスト手順:
//   ClientSim: Play → スイッチを押す → Targets が切り替わる。Master Only ON でも
//     ClientSim のローカルプレイヤーはマスターなので押せる。
//   Build & Test (クライアント数 2): A で押す → B にも反映。B で押す → A にも反映。
//     A で ON にしてから B を一度閉じて Build & Reload で入り直す → B が ON で始まる (Late Joiner)。
//     持ち主 (最後に押した人) のクライアントを閉じる → 残った方で押せて状態が保たれる。
// ============================================================================
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace Naoking.ClaudeWorldGimmicks
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class CctGlobalToggle : UdonSharpBehaviour
    {
        [Tooltip("ON/OFF を切り替える GameObject。ON の時に表示されます (全員に同期)")]
        public GameObject[] targets;

        [Tooltip("targets と逆の状態にする GameObject")]
        public GameObject[] invertTargets;

        [Tooltip("インスタンス開始時の状態。以降は同期された値が使われます")]
        [UdonSynced, FieldChangeCallback(nameof(IsOn)), SerializeField]
        private bool syncedIsOn = false;

        [Tooltip("ON にするとインスタンスマスターだけが切り替えられます (簡易的な制限です)")]
        public bool masterOnly = false;

        /// <summary>
        /// 同期値。ネットワークから値が届いた時は Udon がこの setter を呼びます (FieldChangeCallback)。
        /// 持ち主が書き換えた時も setter 経由で即座に見た目へ反映します。
        /// </summary>
        public bool IsOn
        {
            get => syncedIsOn;
            set
            {
                syncedIsOn = value;
                Apply();
            }
        }

        private void Start()
        {
            Apply();
            UpdateInteractable();
        }

        public override void Interact()
        {
            _Toggle();
        }

        /// <summary>UI ボタン用: 切り替え。</summary>
        public void _Toggle()
        {
            RequestState(!syncedIsOn);
        }

        /// <summary>UI ボタン用: 全員 ON。</summary>
        public void _TurnOn()
        {
            RequestState(true);
        }

        /// <summary>UI ボタン用: 全員 OFF。</summary>
        public void _TurnOff()
        {
            RequestState(false);
        }

        private void RequestState(bool value)
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;
            if (masterOnly && !local.isMaster) return;

            // 同期変数を書けるのは持ち主だけ。持ち主でなければ先に持ち主になる (ローカルでは即時反映される)。
            if (!Networking.IsOwner(local, gameObject))
            {
                Networking.SetOwner(local, gameObject);
            }

            IsOn = value;          // 自分の画面には即反映
            RequestSerialization(); // 他の人へ送信 (Manual 同期)
        }

        public override void OnDeserialization()
        {
            // 値の変化は FieldChangeCallback で反映済み。冪等なので念のため再適用。
            Apply();
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            // 前の持ち主が退出した等で自分が持ち主になったら、手元の値を送り直して全員を揃える。
            if (Utilities.IsValid(player) && player.isLocal)
            {
                RequestSerialization();
            }
        }

        public override void OnMasterTransferred(VRCPlayerApi newMaster)
        {
            UpdateInteractable();
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (Utilities.IsValid(player) && player.isLocal)
            {
                UpdateInteractable();
            }
        }

        private void UpdateInteractable()
        {
            if (!masterOnly) return;
            VRCPlayerApi local = Networking.LocalPlayer;
            // マスター以外にはスイッチの輪郭表示とインタラクトを出さない
            DisableInteractive = !(Utilities.IsValid(local) && local.isMaster);
        }

        private void Apply()
        {
            SetActiveAll(targets, syncedIsOn);
            SetActiveAll(invertTargets, !syncedIsOn);
        }

        private void SetActiveAll(GameObject[] list, bool active)
        {
            if (list == null) return;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] != null) list[i].SetActive(active);
            }
        }
    }
}
