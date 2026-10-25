// ============================================================================
// CctTeleporter - 自分をテレポートさせる (Interact / 踏むとワープ)
// ----------------------------------------------------------------------------
// 目的:
//   Interact した人 (または任意で、トリガーに入った人) を Target の位置と向きへ
//   テレポートします。VRChat の TeleportTo は「ローカルプレイヤー (自分)」にしか効かないため、
//   各プレイヤーが自分自身をテレポートさせる作りです。
//
// セットアップ:
//   1. テレポート先に空の GameObject を置き、向かせたい方向に Z+ (青い矢印) を向ける → Target に登録。
//      Target は床から少し上 (0.05m 程度) に置き、他のテレポーターのトリガー内には置かないこと
//      (ワープ先で即ワープする連鎖を防ぐ)。
//   2. 押してワープ: ボタン役の GameObject に Collider (Is Trigger OFF) を付け、
//      CctTeleporter を Add Component。レイヤーは Default か Interactive (8)。
//   3. 踏んでワープ: 床などに Collider を付けて Is Trigger を ON、Teleport On Enter を ON。
//      トリガーのレイヤーは MirrorReflection (18) 推奨 (プレイヤーと当たり判定し、
//      かつ VRChat のインタラクト用レイを遮らない)。Mesh Renderer は不要 (外す)。
//      押してワープと踏んでワープは別々の GameObject に分けるのが分かりやすいです。
//   4. Lerp On Remote: OFF (既定) = 他の人には瞬間移動に見える。ON = 普通の移動のように
//      滑らかに見え、アバターのアニメーション/IK もリセットされない (近距離・頻繁なワープ向け)。
//
// 同期:
//   同期しません (BehaviourSyncMode.None)。プレイヤー位置は VRChat が自動で同期します。
//   他の人をテレポートさせることはできません (TeleportTo の仕様)。
//
// テスト手順:
//   ClientSim: Play → ボタンをクリック / トリガーを踏む → Target の位置・向きに移動する。
//   Build & Test (2 クライアント): A がワープ → B から見て A が移動している。
//     B が A のトリガーに入っても、テレポートするのは B 自身だけであること。
// 注意:
//   VRC Station に座っている間は TeleportTo が効かない場合があります (VRChat の仕様)。
// ============================================================================
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace Naoking.ClaudeWorldGimmicks
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CctTeleporter : UdonSharpBehaviour
    {
        [Tooltip("テレポート先。位置と向き (Z+ = 青い矢印が正面) を使います")]
        public Transform target;

        [Tooltip("ON: この GameObject の Trigger コライダーに自分が入った時にもテレポートする")]
        public bool teleportOnEnter = false;

        [Tooltip("ON: 他の人からは滑らかな移動に見える (近距離・頻繁なワープ向け)。OFF: 瞬間移動に見える (通常はこちら)")]
        public bool lerpOnRemote = false;

        [Tooltip("向きの合わせ方。AlignPlayerWithSpawnPoint = プレイヤーの向きを Target に合わせる (推奨)")]
        public VRC_SceneDescriptor.SpawnOrientation orientation = VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint;

        [Tooltip("連続テレポートを防ぐ待ち時間 (秒)")]
        public float cooldownSeconds = 0.5f;

        private float _lastTeleportTime = -1000f;
        private bool _pending;

        public override void Interact()
        {
            _Teleport();
        }

        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (!teleportOnEnter) return;
            // トリガーには他のプレイヤーも入ってくる。自分 (ローカルプレイヤー) の時だけ処理する。
            if (!Utilities.IsValid(player) || !player.isLocal) return;
            if (_pending) return;
            _pending = true;
            // 物理コールバックの最中を避け、1 フレーム後にテレポートする。
            SendCustomEventDelayedFrames(nameof(_TeleportDeferred), 1);
        }

        /// <summary>内部用 (トリガー時の遅延テレポート)。</summary>
        public void _TeleportDeferred()
        {
            _pending = false;
            _Teleport();
        }

        /// <summary>UI ボタン用: 自分を Target へテレポート。</summary>
        public void _Teleport()
        {
            if (target == null) return;
            if (Time.time - _lastTeleportTime < cooldownSeconds) return;
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;
            _lastTeleportTime = Time.time;
            local.TeleportTo(target.position, target.rotation, orientation, lerpOnRemote);
        }
    }
}
