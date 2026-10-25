// ============================================================================
// CctAudioVolume - UI スライダーで BGM などの音量を自分だけ調整する
// ----------------------------------------------------------------------------
// 目的:
//   登録した AudioSource の音量を、UI の Slider で自分の画面 (耳) だけ変更します。
//   シーンに置いた時の各 AudioSource の音量を 100% とし、スライダーの位置 (左端 0 〜 右端 1) を掛けます。
//   任意で「%」表示 (TextMeshPro) と、VRChat の PlayerData による設定の保存ができます。
//
// セットアップ:
//   1. Canvas を作る (World Space 推奨)。VRChat で UI を操作するには Canvas に
//      VRC Ui Shape コンポーネントが必要。Canvas のレイヤーは Default
//      (UI レイヤーはメニューを開いていないと操作できない: VRChat の仕様)。
//   2. Canvas に Slider を置く。左端 = 0%、右端 = 100% として扱うので Min / Max Value は何でもよい
//      (おすすめは Min 0 / Max 1)。Value を右端 (Max Value) にしておくと 100% で始まります。
//   3. ワールド開始時から Active な GameObject (例: Canvas 自身や、常に表示される空の GameObject) に
//      CctAudioVolume を Add Component し、Audio Sources, Slider, (任意) Percent Label を登録。
//      ※ 最初は非表示の設定パネルの中には付けないこと。Udon は Start 前 (= 非アクティブの間) に届いた
//        イベントを捨てるため、入室時の OnPlayerRestored を受け取れず、保存が働かないことがあります
//        (Slider 自体は非表示のパネルにあっても OK)。
//   4. Slider の On Value Changed (Single) の + を押し、CctAudioVolume を付けた GameObject を入れ、
//      UdonBehaviour > SendCustomEvent (string) を選んで「_OnSliderChanged」と入力。
//      (UI イベントから Udon に値は渡せないので、スクリプト側でスライダーの位置を読みます)
//   5. 保存したい場合は Persistence Key に、ワールド内で一意なキー名 (例: "CctAudioVolume.BGM") を入れる。
//      空欄なら保存しません。複数の CctAudioVolume を置く場合はキーを別々にする。
//
// 同期:
//   同期しません (BehaviourSyncMode.None)。各自の音量です。
//   PlayerData (Persistence) は VRChat が自動でサーバーに保存し、次回入った時に復元されます。
//   - OnPlayerRestored (自分のデータ読み込み完了) より前には読み書きしない (公式ドキュメントの注意)。
//   - スライダーを動かしている間は保存せず、止めてから約 1 秒後にまとめて保存 (PlayerData は
//     書き換えるたびに全データを送信するため)。
//
// テスト手順:
//   ClientSim: Play → スライダーを動かす → 音量と「%」表示が変わる。
//     保存: キーを入れて値を変え、Play を止めて再生 → 値が復元される
//     (ClientSim の保存データはプロジェクト内の JSON。ClientSim のドキュメント参照)。
//   Build & Test (2 クライアント): A で音量を下げても B の音量は変わらない。
//     Build & Test のデータはテストクライアントを閉じると消える (公式ドキュメント)。保存の確認は
//     「Build & Reload」で行う。
// ============================================================================
using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Persistence;
using VRC.SDKBase;

namespace Naoking.ClaudeWorldGimmicks
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CctAudioVolume : UdonSharpBehaviour
    {
        [Tooltip("音量を変える AudioSource。シーンに置いた時の音量を 100% とします")]
        public AudioSource[] audioSources;

        [Tooltip("音量スライダー (左端 = 0%、右端 = 100%)。On Value Changed から _OnSliderChanged を呼ぶ")]
        public Slider slider;

        [Tooltip("(任意) 「80%」のように表示するテキスト (TextMeshPro)")]
        public TMP_Text percentLabel;

        [Tooltip("(任意) 自分の音量を保存する PlayerData のキー。空欄なら保存しない。ワールド内で一意な名前に (例: CctAudioVolume.BGM)")]
        public string persistenceKey = "";

        [Tooltip("スライダーを止めてから保存するまでの秒数")]
        public float saveDelaySeconds = 1f;

        private float[] _baseVolumes;
        private float _level = 1f;
        private bool _initialized;
        private bool _restored;
        private bool _savePending;
        private float _lastChangeTime;

        private void Start()
        {
            Initialize();
            // 非アクティブで始まった等で OnPlayerRestored を受け取れなかった場合の救済:
            // キーが既に存在する = 自分のデータは読み込み済み なので、その値を使う。
            if (!_restored) TryRestoreFromExistingKey();
        }

        private void TryRestoreFromExistingKey()
        {
            if (string.IsNullOrEmpty(persistenceKey)) return;
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;
            if (!PlayerData.HasKey(local, persistenceKey)) return;
            _restored = true;
            ApplySaved(local);
        }

        private void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            int count = audioSources == null ? 0 : audioSources.Length;
            _baseVolumes = new float[count];
            for (int i = 0; i < count; i++)
            {
                _baseVolumes[i] = audioSources[i] != null ? audioSources[i].volume : 0f;
            }
            if (slider != null) _level = Mathf.Clamp01(slider.normalizedValue);
            ApplyLevel();
        }

        /// <summary>Slider の On Value Changed から SendCustomEvent で呼ぶ。</summary>
        public void _OnSliderChanged()
        {
            Initialize();
            if (slider == null) return;
            // normalizedValue = Min〜Max のどこにいるか (0〜1)。Min/Max を 0〜100 などにしていても正しく動く。
            _level = Mathf.Clamp01(slider.normalizedValue);
            ApplyLevel();
            QueueSave();
        }

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || !player.isLocal) return;
            Initialize();
            if (_restored) return; // Start で既存キーから復元済み
            _restored = true;
            ApplySaved(player);
        }

        private void ApplySaved(VRCPlayerApi player)
        {
            if (string.IsNullOrEmpty(persistenceKey)) return;
            float saved;
            if (PlayerData.TryGetFloat(player, persistenceKey, out saved))
            {
                _level = Mathf.Clamp01(saved);
                if (slider != null) slider.SetValueWithoutNotify(Mathf.Lerp(slider.minValue, slider.maxValue, _level));
                ApplyLevel();
            }
        }

        private void ApplyLevel()
        {
            int count = audioSources == null ? 0 : audioSources.Length;
            for (int i = 0; i < count; i++)
            {
                if (audioSources[i] != null) audioSources[i].volume = _baseVolumes[i] * _level;
            }
            if (percentLabel != null) percentLabel.text = Mathf.RoundToInt(_level * 100f) + "%";
        }

        private void QueueSave()
        {
            if (string.IsNullOrEmpty(persistenceKey) || !_restored) return;
            _lastChangeTime = Time.time;
            if (_savePending) return;
            _savePending = true;
            SendCustomEventDelayedSeconds(nameof(_SaveWhenIdle), saveDelaySeconds);
        }

        /// <summary>内部用 (保存の間引き)。</summary>
        public void _SaveWhenIdle()
        {
            float idle = Time.time - _lastChangeTime;
            if (idle < saveDelaySeconds)
            {
                SendCustomEventDelayedSeconds(nameof(_SaveWhenIdle), saveDelaySeconds - idle);
                return;
            }
            _savePending = false;
            PlayerData.SetFloat(persistenceKey, _level);
        }
    }
}
