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
//      部屋全体を覆う大きさにし、床まで届くようにする (足元の位置でも判定するため)。
//      L 字型などの部屋は、同じ GameObject に Box Collider (Is Trigger) を複数付けて OK
//      (重なっていても正しく数えます)。Mesh Renderer は付けない。
//      Is Trigger が OFF のコライダーは判定に使いません。
//   2. その GameObject のレイヤーを MirrorReflection (18) にする (推奨)。
//      MirrorReflection はプレイヤーと当たり判定 (トリガー) を取りつつ、VRChat の
//      インタラクト用レイ (UI レーザー / 遠くのピックアップ) を遮りません。
//      Default (0) でも動きますが、巨大なトリガーがインタラクトを遮ることがあります。
//   3. 同じ GameObject に CctAreaToggle を Add Component。
//      Inside Objects / Outside Objects を登録 (自分自身の GameObject は入れても無視されます)。
//   BGM の例: BGM 用の AudioSource (Play On Awake ON, Loop ON) を付けた GameObject を
//   Inside Objects に入れると、部屋に入るたびに最初から再生されます。その GameObject はシーン上で
//   非アクティブにしておく (Start で「外」の状態になるまでの最初の 1 フレームに鳴らないように)。
//
// 同期:
//   同期しません (BehaviourSyncMode.None)。表示は各プレイヤーが自分の位置で決めます。
//
// 取りこぼし対策:
//   - 重なった複数のトリガー: Enter/Exit の回数を数え、0 になった時だけ「外」にします。
//     Enter を取りこぼした後は回数が実際より少ないので、0 になった時に体の中心線がまだ別のトリガーの
//     中にあれば「中」のままにします (L 字の部屋の途中で一瞬消えて BGM が頭から鳴り直すのを防ぐ)。
//   - テレポートやリスポーン、高速移動では Enter/Exit が来ないことがある (公式ドキュメントに記載)。
//     また Udon は Start 前に届いたイベントを捨てるため、「エリアの中でスポーンした」
//     「エリアの中にいる時にこの GameObject が有効化された」場合も Enter が届きません。
//     そこで Check Interval 秒ごとに自分の位置を確かめ直します:
//       外の扱いなのに、体の中心線 (足元付近と腰の高さ) がトリガーの中 → 「中」にする。
//       中の扱いなのに、足元・腰・頭の高さのどれもトリガーから Exit Margin (m) 以上離れている → 「外」にする。
//     起動 0.5 秒後とリスポーン 0.25 秒後にも同じ確認をします。
//   - この GameObject (またはこのコンポーネント) が無効化された時は「外」に戻します (Unity は無効化時に
//     Exit を送らないため)。無効の間の Enter/Exit は無視し、再び有効になった 0.25 秒後に位置で確かめ直します。
//   - コライダーは Box / Sphere / Capsule か、Convex を ON にした Mesh Collider を使う
//     (位置の確認に使う Collider.ClosestPoint が凹型の Mesh Collider には使えないため)。
//
// テスト手順:
//   ClientSim: Play → エリアに歩いて入る/出る → 表示が切り替わる。
//     スポーン地点をエリア内に置いて Play → 1 秒以内に「中」の表示になる。
//     エリア内から CctTeleporter などで外へワープ → 1 秒程度で「外」の表示に戻る。
//     外から CctTeleporter でエリア内へワープ → 1 秒程度で「中」の表示になる。
//     エリア内でリスポーン (ClientSim の Respawn ボタン) → 外に出ていれば「外」に戻る。
//     L 字 (トリガー 2 つ) のエリア内でスポーン → もう片方のトリガーへ歩いて移る → 途中で消えない。
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

        [Tooltip("自分の位置でエリアの内外を確かめ直す間隔 (秒、最小 0.1)。Enter/Exit の取りこぼしを直します。0 で定期確認しない (起動時とリスポーン時の確認だけ)")]
        public float checkInterval = 1f;

        [Tooltip("トリガーからこの距離 (m) 以上離れていたら、Exit が来なくても「外」にする")]
        public float exitMargin = 1f;

        private const float MinInterval = 0.1f;
        private const float InsideTolerance = 0.01f;

        private Collider[] _zones;
        private int _overlapCount;
        private bool _inside;
        private bool _enabled;
        private bool _watching;
        private float _nextTickTime;

        private void OnEnable()
        {
            // 初回は Udon が Start の直前に呼ぶ。2 回目以降は再有効化のたび。
            _enabled = true;
            CollectZones();
            StartWatch();
            // 再有効化の場合: 無効の間に出入りした分を位置で確かめ直す (初回は Start の確認と重なるが無害)。
            SendCustomEventDelayedSeconds(nameof(_Recheck), 0.25f);
        }

        private void Start()
        {
            CollectZones();
            ApplyState();
            // Start 前に届いた Enter は捨てられているので、少し待ってから位置で確かめる。
            SendCustomEventDelayedSeconds(nameof(_Recheck), 0.5f);
        }

        private void OnDisable()
        {
            // Unity は無効化時に Exit を送らないので、ここで「外」に戻す。
            _enabled = false;
            _watching = false;
            _overlapCount = 0;
            SetInside(false);
        }

        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            // Udon はコンポーネントが無効でもトリガーイベントを届けるので、無効の間は無視する。
            if (!_enabled || !IsLocal(player)) return;
            _overlapCount++;
            SetInside(true);
        }

        public override void OnPlayerTriggerExit(VRCPlayerApi player)
        {
            if (!_enabled || !IsLocal(player)) return;
            _overlapCount--;
            if (_overlapCount > 0) return;
            _overlapCount = 0;
            // Enter を取りこぼしていた (エリア内でスポーン等) と回数が実際より少ない。重なった別のトリガーの中に
            // 体の中心線がまだあるなら「中」のまま。本当に全部から出た時は、カプセルごと外なので中心線も外にある。
            if (AxisInsideZones(player)) return;
            SetInside(false);
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (!IsLocal(player)) return;
            // リスポーンはテレポートなので Enter/Exit が来ないことがある。移動が反映された頃に位置で確かめる。
            SendCustomEventDelayedSeconds(nameof(_Recheck), 0.25f);
        }

        /// <summary>内部用: 自分の位置でエリアの内外を確かめ直す (遅延イベントから呼ばれる)。</summary>
        public void _Recheck()
        {
            Evaluate();
        }

        /// <summary>内部用: 有効な間だけ回る確認ループ。</summary>
        public void _WatchTick()
        {
            // 遅延イベントは無効化中も届くので、ループが止められていれば何もしない。
            if (!_watching || !_enabled) return;
            // 無効化→再有効化で古い予約が残っていた場合は、新しいループの予定より早く届くので無視する
            // (重複したループはこれで 1 つに収束する)。
            if (Time.time + 0.05f < _nextTickTime) return;
            Evaluate();
            ScheduleTick();
        }

        private void StartWatch()
        {
            if (checkInterval <= 0f) return;
            _watching = true;
            ScheduleTick();
        }

        private void ScheduleTick()
        {
            float interval = checkInterval < MinInterval ? MinInterval : checkInterval;
            _nextTickTime = Time.time + interval;
            SendCustomEventDelayedSeconds(nameof(_WatchTick), interval);
        }

        private void Evaluate()
        {
            if (!_enabled) return;
            if (_zones == null || _zones.Length == 0) return;
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;

            if (!_inside)
            {
                // 軸上の点がトリガーの中 = カプセルはトリガーと重なっている。Enter を取りこぼしていた。
                if (AxisInsideZones(local))
                {
                    SetInside(true);
                }
            }
            else
            {
                Vector3 feet = local.GetPosition();
                float height = PlayerHeight(local, feet);
                Vector3 mid = feet + Vector3.up * (height * 0.5f);
                Vector3 top = feet + Vector3.up * height;
                float margin = exitMargin < 0f ? 0f : exitMargin;
                if (DistanceToZones(feet) > margin && DistanceToZones(mid) > margin && DistanceToZones(top) > margin)
                {
                    // 明らかにエリアの外にいる。Exit を取りこぼしていた。
                    _overlapCount = 0;
                    SetInside(false);
                }
            }
        }

        /// <summary>
        /// 体の中心線上の点 (プレイヤーのカプセルの軸の、足元付近と腰の高さ) のどちらかがトリガーの中にあるか。
        /// </summary>
        private bool AxisInsideZones(VRCPlayerApi player)
        {
            if (_zones == null || _zones.Length == 0 || !Utilities.IsValid(player)) return false;
            Vector3 feet = player.GetPosition();
            float height = PlayerHeight(player, feet);
            return DistanceToZones(feet + Vector3.up * (height * 0.15f)) <= InsideTolerance
                || DistanceToZones(feet + Vector3.up * (height * 0.5f)) <= InsideTolerance;
        }

        /// <summary>足元から頭までの高さ (アバターの大きさに合わせる。0.3〜5m に制限)。</summary>
        private float PlayerHeight(VRCPlayerApi player, Vector3 feet)
        {
            float height = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position.y - feet.y;
            if (height < 0.3f) height = 0.3f;
            if (height > 5f) height = 5f;
            return height;
        }

        /// <summary>有効なトリガーコライダーまでの最短距離 (中にいれば 0)。トリガーが無ければ大きな値。</summary>
        private float DistanceToZones(Vector3 point)
        {
            float best = float.MaxValue;
            for (int i = 0; i < _zones.Length; i++)
            {
                Collider zone = _zones[i];
                if (zone == null || !zone.enabled) continue; // 無効なコライダーの ClosestPoint は当てにならない
                float d = Vector3.Distance(zone.ClosestPoint(point), point);
                if (d < best) best = d;
            }
            return best;
        }

        private void CollectZones()
        {
            if (_zones != null) return;
            Collider[] all = GetComponents<Collider>();
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].isTrigger) count++;
            }
            _zones = new Collider[count];
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].isTrigger)
                {
                    _zones[n] = all[i];
                    n++;
                }
            }
        }

        private void SetInside(bool inside)
        {
            if (_inside == inside) return;
            _inside = inside;
            ApplyState();
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
                // 自分自身を切り替えると判定ごと止まるので無視する。
                if (list[i] != null && list[i] != gameObject) list[i].SetActive(active);
            }
        }
    }
}
