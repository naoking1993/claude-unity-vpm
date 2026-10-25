// ============================================================================
// CctMirrorSwitcher - ミラーの切り替え (高画質 / 低画質 / OFF など) を自分だけで行う
// ----------------------------------------------------------------------------
// 目的:
//   複数のミラー GameObject (例: 0 = 高画質 HQ, 1 = 低画質 LQ) のうち 1 つだけを表示、
//   または全部 OFF にします。自分の画面だけの切り替えです。
//   VRChat 公式ガイド「ミラーは既定で OFF にし、プレイヤーが ON にできるようにする」に
//   合わせ、既定では全部 OFF で始まります。
//
// セットアップ:
//   1. ミラーを用意する (VRChat SDK の VRCMirror プレハブ等)。HQ 用と LQ 用で
//      VRC Mirror Reflection の Reflect Layers や解像度を変えた物を並べて置く。
//      ミラー GameObject はシーン上で非アクティブにしておいてよい (Start で全部 OFF にする)。
//   2. 切り替えボタン役の GameObject に Collider を付け、CctMirrorSwitcher を Add Component。
//      (Interact すると OFF → 0 → 1 → … → OFF の順に巡回)
//   3. Mirrors に順番どおりに登録 (最大 4 個までは _Select0〜_Select3 で直接選べる)。
//      スイッチ自身の GameObject を Mirrors に入れても無視されます (ミラー本体には付けず、別のボタンに付ける)。
//   4. (任意) Label に TextMeshPro のテキストを入れ、Mode Names に表示名 (例: "HQ", "LQ") を入れる。
//   UI ボタンから: 各ボタンの On Click () → この GameObject → UdonBehaviour.SendCustomEvent →
//     _Next / _Off / _Select0 / _Select1 / _Select2 / _Select3 を入力。
//
// 同期:
//   同期しません (BehaviourSyncMode.None)。ミラーの表示は各自の自由なので、ローカルのみ。
//
// テスト手順:
//   ClientSim: Play 直後に全ミラー OFF → ボタンを押すたびに 0 → 1 → OFF と切り替わる。
//     UI ボタンを配線した場合は各ボタンで指定のミラーだけが表示されること。
//   Build & Test (2 クライアント): 片方で ON にしても、もう片方は OFF のまま。
// ============================================================================
using TMPro;
using UdonSharp;
using UnityEngine;

namespace Naoking.ClaudeWorldGimmicks
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CctMirrorSwitcher : UdonSharpBehaviour
    {
        [Tooltip("切り替えるミラーの GameObject。_Next で巡回する順番 (例: 0 = 高画質, 1 = 低画質)")]
        public GameObject[] mirrors;

        [Tooltip("ワールドに入った時に表示するミラーの番号。-1 = 全部 OFF (推奨)")]
        public int initialIndex = -1;

        [Tooltip("(任意) 現在のモードを表示するテキスト (TextMeshPro / TextMeshPro - Text (UI))")]
        public TMP_Text label;

        [Tooltip("(任意) Label に出す名前。Mirrors と同じ順番。足りない分は「Mirror 1」などになります")]
        public string[] modeNames;

        [Tooltip("全部 OFF の時に Label に出す文字")]
        public string offName = "OFF";

        private int _current = -1;

        private void Start()
        {
            Select(initialIndex);
        }

        public override void Interact()
        {
            _Next();
        }

        /// <summary>UI ボタン用: OFF → 0 → 1 → … → OFF と巡回。</summary>
        public void _Next()
        {
            int count = mirrors == null ? 0 : mirrors.Length;
            if (count == 0) return;
            int next = _current + 1;
            if (next >= count) next = -1;
            Select(next);
        }

        /// <summary>UI ボタン用: 全部 OFF。</summary>
        public void _Off()
        {
            Select(-1);
        }

        public void _Select0() { Select(0); }
        public void _Select1() { Select(1); }
        public void _Select2() { Select(2); }
        public void _Select3() { Select(3); }

        /// <summary>現在表示中のミラー番号 (-1 = OFF)。他の U# スクリプトから参照用。</summary>
        public int CurrentIndex => _current;

        private void Select(int index)
        {
            int count = mirrors == null ? 0 : mirrors.Length;
            if (index < -1 || index >= count) index = -1;
            _current = index;
            for (int i = 0; i < count; i++)
            {
                // スイッチ自身は切り替えない (ミラーと同じ GameObject に付けると、OFF にした時に二度と押せなくなるため)。
                if (mirrors[i] != null && mirrors[i] != gameObject) mirrors[i].SetActive(i == index);
            }
            UpdateLabel();
        }

        private void UpdateLabel()
        {
            if (label == null) return;
            if (_current < 0)
            {
                label.text = offName;
            }
            else if (modeNames != null && _current < modeNames.Length && !string.IsNullOrEmpty(modeNames[_current]))
            {
                label.text = modeNames[_current];
            }
            else
            {
                label.text = "Mirror " + (_current + 1);
            }
        }
    }
}
