// ============================================================================
// CctAreaToggle - 自分がエリア (部屋) に入っている間だけ物を表示する
// ----------------------------------------------------------------------------
// 目的:
//   ローカルプレイヤー (自分) がトリガーエリアに入ると Inside Objects を表示し
//   Outside Objects を隠します。出ると逆になります。部屋ごとの BGM、照明、
//   重いオブジェクトの表示切り替え (遠くでは消す) などに使います。他の人には影響しません。
//
// セットアップ:
//   1. 空の GameObject を作り、Box Collider (など) を付けて Is Trigger を ON にする。
//      部屋全体を覆う大きさにし、床まで届くようにする (足元の位置で離脱判定するため)。
//      L 字型などの部屋は、同じ GameObject に Box Collider (Is Trigger) を複数付けて OK
//      (重なっていても正しく数えます)。Mesh Renderer は付けない。
//   2. その GameObject のレイヤーを MirrorReflection (18) にする (推奨)。
//      MirrorReflection はプレイヤーと当たり判定 (トリガー) を取りつつ、VRChat の
//      インタラクト用レイ (UI レーザー / 遠くのピックアップ) を遮りません。
//      Default (0) でも動きますが、巨大なトリガーがインタラクトを遮ることがあります。
//   3. 同じ GameObject に CctAreaToggle を Add Component。
//      Inside Objects / Outside Objects を登録 (自分自身の GameObject は入れないこと)。
//   BGM の例: BGM 用の AudioSource (Play On Awake ON, Loop ON) を付けた子 GameObject を
//   Inside Objects に入れると、部屋に入るたびに最初から再生されます。
//
// 同期:
//   同期しません (BehaviourSyncMode.None)。表示は各プレイヤーが自分の位置で決めます。
//
// 取りこぼし対策:
//   - 重なった複数のトリガー: Enter/Exit の回数を数え、0 になった時だけ「外」にします。
//   - テレポートやリスポーンで Exit イベントが来ない場合がある (公式ドキュメントに記載):
//     中にいる間は Check Interval 秒ごとに、自分の足元と胸の位置がエリアの外接箱から
//     Exit Margin (m) 以上離れていないか確認し、離れていれば強制的に「外」にします。
//     リスポーン時は 0.25 秒後にも確認します。
//   - この GameObject が無効化された時は「外」に戻します (Unity は無効化時に Exit を送らないため)。
//
// テスト手順:
//   ClientSim: Play → エリアに歩いて入る/出る → 表示が切り替わる。
//     エリア内から CctTeleporter などで外へワープ → 1 秒以内に「外」の表示に戻る。
//     エリア内でリスポーン (ClientSim の Respawn ボタン) → 外に出ていれば「外」に戻る。
//   Build & Test (2 クライアント): A がエリアに入っても B の表示は変わらない。
//     B のアバターがエリアに入っても A の表示は変わらない。
// ============================================================================
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace Naoking.ClaudeWorldGimmicks
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CctAreaToggle : UdonSharpBehaviour
    {
        [Tooltip("自分がエリア内にいる間だけ表示する GameObject (部屋の BGM, ライト等)")]
        public GameObject[] insideObjects;

        [Tooltip("自分がエリア外にいる間だけ表示する GameObject")]
        public GameObject[] outsideObjects;

        [Tooltip("エリア内にいる間、まだ中にいるかを確認する間隔 (秒)。0 で確認しない")]
        public float checkInterval = 1f;

        [Tooltip("エリア (コライダーの外接箱) からこの距離 (m) 以上離れていたら、Exit が来なくても「外」にする")]
        public float exitMargin = 1f;

        private Collider[] _zones;
        private int _overlapCount;
        private bool _inside;
        private bool _watching;
        private float _nextTickTime;

        private void Start()
        {
            _zones = GetComponents<Collider>();
            _inside = false;
            ApplyState();
        }

        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            _overlapCount++;
            SetInside(true);
        }

        public override void OnPlayerTriggerExit(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            _overlapCount--;
            if (_overlapCount <= 0)
            {
                _overlapCount = 0;
                SetInside(false);
            }
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            // リスポーンはテレポートなので Exit が来ないことがある。移動が反映された頃に位置で確認する。
            SendCustomEventDelayedSeconds(nameof(_CheckStillInside), 0.25f);
        }

        private void OnDisable()
        {
            // Unity は無効化時に Exit を送らないので、ここで「外」に戻す。
            _overlapCount = 0;
            _watching = false;
            SetInside(false);
        }

        /// <summary>内部用: エリアから離れていたら強制的に「外」にする。</summary>
        public void _CheckStillInside()
        {
            if (!_inside) return;
            if (!IsNearZone())
            {
                _overlapCount = 0;
                SetInside(false);
            }
        }

        /// <summary>内部用: エリア内にいる間だけ回る確認ループ。</summary>
        public void _WatchTick()
        {
            // 無効化→再有効化で古い予約が残っていた場合は、新しいループの予定より早く届くので無視する。
            if (Time.time + 0.05f < _nextTickTime) return;
            if (!_inside || checkInterval <= 0f)
            {
                _watching = false;
                return;
            }
            _CheckStillInside();
            if (_inside)
            {
                ScheduleTick();
            }
            else
            {
                _watching = false;
            }
        }

        private void ScheduleTick()
        {
            _nextTickTime = Time.time + checkInterval;
            SendCustomEventDelayedSeconds(nameof(_WatchTick), checkInterval);
        }

        private void SetInside(bool inside)
        {
            if (_inside == inside) return;
            _inside = inside;
            ApplyState();
            if (_inside && !_watching && checkInterval > 0f)
            {
                _watching = true;
                ScheduleTick();
            }
        }

        private bool IsNearZone()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return false;
            if (_zones == null) return false;
            Vector3 feet = local.GetPosition();
            Vector3 chest = feet + Vector3.up;
            float margin = exitMargin < 0f ? 0f : exitMargin;
            float marginSqr = margin * margin;
            for (int i = 0; i < _zones.Length; i++)
            {
                Collider zone = _zones[i];
                if (zone == null || !zone.enabled) continue;
                Bounds bounds = zone.bounds;
                if (bounds.SqrDistance(feet) <= marginSqr || bounds.SqrDistance(chest) <= marginSqr) return true;
            }
            return false;
        }

        private bool IsLocal(VRCPlayerApi player)
        {
            return Utilities.IsValid(player) && player.isLocal;
        }

        private void ApplyState()
        {
            SetActiveAll(insideObjects, _inside);
            SetActiveAll(outsideObjects, !_inside);
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
