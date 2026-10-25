// ============================================================================
// CctLocalToggle - 自分の画面だけで表示/非表示を切り替えるスイッチ
// ----------------------------------------------------------------------------
// 目的:
//   Interact (クリック / トリガーを引く) するたびに、登録した GameObject を
//   自分の画面だけで ON/OFF します。ミラー、BGM、ポストエフェクト、案内板など
//   「見たい人だけが付ける」ものに使います。他のプレイヤーには影響しません。
//
// セットアップ:
//   1. スイッチ役の GameObject (例: Cube) を作り、Collider を付ける
//      (Box Collider など。Is Trigger は OFF のままで OK)。
//      レイヤーは Default か Interactive (8, ステッカーを貼られたくない場合) を推奨。
//   2. その GameObject に CctLocalToggle を Add Component する。
//      (UdonSharp の Program Asset が無いとエラーになります。README の手順参照)
//   3. Targets に切り替えたい GameObject (例: ミラー) を登録する。
//      Invert Targets には「Targets と逆の状態にしたい物」を登録できる
//      (例: ミラー OFF の時だけ表示する「ミラー ON」の看板)。
//   4. Initial On で参加時の状態を決める (ミラーは OFF 推奨: VRChat 公式ガイド)。
//   5. スイッチ自身の GameObject を Targets に入れても無視されます
//      (自分を非表示にすると二度と押せなくなるため)。スイッチの親を入れた場合も同じく押せなくなるので注意。
//   UI ボタンから使う場合: Button の On Click () に このスイッチの GameObject を入れ、
//   UdonBehaviour > SendCustomEvent (string) を選び、_Toggle / _TurnOn / _TurnOff を入力。
//
// 同期:
//   同期しません (BehaviourSyncMode.None)。各プレイヤーが自分の画面だけを切り替えます。
//   後から入った人は Initial On の状態から始まります。
//
// テスト手順:
//   ClientSim: Play → スイッチをクリック → Targets が切り替わることを確認。
//   Build & Test (2 クライアント): 片方で押しても、もう片方の表示は変わらないこと。
// ============================================================================
using UdonSharp;
using UnityEngine;

namespace Naoking.ClaudeWorldGimmicks
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CctLocalToggle : UdonSharpBehaviour
    {
        [Tooltip("ON/OFF を切り替える GameObject (例: ミラー)。ON の時に表示されます")]
        public GameObject[] targets;

        [Tooltip("targets と逆の状態にする GameObject (例: ミラー OFF の時だけ出す看板)")]
        public GameObject[] invertTargets;

        [Tooltip("ワールドに入った時の状態。ON にすると targets が表示された状態で始まります (ミラーは OFF 推奨)")]
        public bool initialOn = false;

        private bool _isOn;

        private void Start()
        {
            _isOn = initialOn;
            Apply();
        }

        public override void Interact()
        {
            _Toggle();
        }

        /// <summary>UI ボタン用: 切り替え。'_' で始まるのでネットワーク越しには呼ばれません。</summary>
        public void _Toggle()
        {
            _isOn = !_isOn;
            Apply();
        }

        /// <summary>UI ボタン用: 強制 ON。</summary>
        public void _TurnOn()
        {
            _isOn = true;
            Apply();
        }

        /// <summary>UI ボタン用: 強制 OFF。</summary>
        public void _TurnOff()
        {
            _isOn = false;
            Apply();
        }

        private void Apply()
        {
            SetActiveAll(targets, _isOn);
            SetActiveAll(invertTargets, !_isOn);
        }

        private void SetActiveAll(GameObject[] list, bool active)
        {
            if (list == null) return;
            for (int i = 0; i < list.Length; i++)
            {
                // スイッチ自身は切り替えない (非表示にすると二度と押せなくなる)。
                if (list[i] != null && list[i] != gameObject) list[i].SetActive(active);
            }
        }
    }
}
