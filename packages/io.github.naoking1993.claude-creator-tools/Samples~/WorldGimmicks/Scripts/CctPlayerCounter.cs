// ============================================================================
// CctPlayerCounter - 現在の人数と、参加/退出ログを表示する
// ----------------------------------------------------------------------------
// 目的:
//   インスタンスにいる人数を TextMeshPro のテキストに表示し、任意で直近 N 件の
//   「○○ が参加しました / 退出しました」ログを表示します。
//
// セットアップ:
//   1. 表示用のテキストを置く。
//      - Canvas 上: Hierarchy 右クリック → UI → TextMeshPro (VRC) などで TextMeshPro - Text (UI)
//      - ワールド空間: 3D Object → Text - TextMeshPro
//      (TextMeshPro を初めて使う場合は「Import TMP Essentials」を実行)
//   2. 任意の GameObject (例: テキスト自身) に CctPlayerCounter を Add Component。
//   3. Count Label に人数用テキストを登録。ログも出すなら Log Label と Log Size (例: 5) を設定。
//      Log Size = 0 でログ無し。
//   4. Count Prefix / Suffix、参加/退出の文言、時刻表示の有無を好みで変更。
//
// 同期:
//   同期しません (BehaviourSyncMode.None)。人数とログは各クライアントが
//   OnPlayerJoined / OnPlayerLeft から自分で計算します。
//   - OnPlayerLeft の中では、退出した本人がまだプレイヤー一覧に含まれている場合がある
//     (ClientSim の実装はイベント送信後に一覧から削除する)。そのため人数は
//     GetPlayers() の結果から「有効 (Utilities.IsValid) かつ 退出者の playerId 以外」を数えます。
//     含まれていない実装でも同じ結果になります。
//   - 自分が参加した時は、既にいる全員分の OnPlayerJoined が届きます (公式ドキュメント)。
//     そのためログには既存メンバーの「参加」も並びます。
//   - ログは各自が見たものだけです (後から来た人には過去のログはありません)。
//
// テスト手順:
//   ClientSim: Play → 「現在 1 人」。ClientSim の設定ウィンドウでリモートプレイヤーを
//     追加/削除 → 人数とログが増減する (退出時に人数が正しく 1 減ること)。
//   Build & Test (クライアント数 2): 両方で「現在 2 人」。片方を閉じる → 残った方が
//     「現在 1 人」と「○○ が退出しました」になる。
// ============================================================================
using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace Naoking.ClaudeWorldGimmicks
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CctPlayerCounter : UdonSharpBehaviour
    {
        [Tooltip("人数を表示するテキスト (TextMeshPro / TextMeshPro - Text (UI))")]
        public TMP_Text countLabel;

        [Tooltip("人数の前に付ける文字")]
        public string countPrefix = "現在 ";

        [Tooltip("人数の後に付ける文字")]
        public string countSuffix = " 人";

        [Tooltip("(任意) 参加/退出ログを表示するテキスト")]
        public TMP_Text logLabel;

        [Tooltip("ログに残す件数 (新しい順)。0 でログ無し")]
        public int logSize = 5;

        [Tooltip("参加時の文言 (名前の後ろに付く)")]
        public string joinedText = " が参加しました";

        [Tooltip("退出時の文言 (名前の後ろに付く)")]
        public string leftText = " が退出しました";

        [Tooltip("ログの先頭に時刻 (HH:mm) を付ける")]
        public bool showTime = true;

        private VRCPlayerApi[] _players;
        private string[] _log;
        private int _logCount;

        private void Start()
        {
            EnsureLog();
            RefreshCount(-1);
            RefreshLog();
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            AddLog(NameOf(player) + joinedText);
            RefreshCount(-1);
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            AddLog(NameOf(player) + leftText);
            // 退出者がまだ一覧に含まれていても数えないよう、その playerId を除外する。
            RefreshCount(Utilities.IsValid(player) ? player.playerId : -1);
        }

        private void RefreshCount(int excludePlayerId)
        {
            int total = VRCPlayerApi.GetPlayerCount();
            int capacity = _players == null ? 0 : _players.Length;
            if (capacity < total)
            {
                if (capacity < 16) capacity = 16;
                while (capacity < total) capacity *= 2;
                _players = new VRCPlayerApi[capacity];
            }
            // 前回の結果が残らないよう空にしてから取得する。
            for (int i = 0; i < _players.Length; i++) _players[i] = null;
            VRCPlayerApi.GetPlayers(_players);

            int count = 0;
            for (int i = 0; i < _players.Length; i++)
            {
                VRCPlayerApi p = _players[i];
                if (!Utilities.IsValid(p)) continue;
                if (p.playerId == excludePlayerId) continue;
                count++;
            }
            if (countLabel != null) countLabel.text = countPrefix + count + countSuffix;
        }

        private string NameOf(VRCPlayerApi player)
        {
            return Utilities.IsValid(player) ? player.displayName : "(不明なプレイヤー)";
        }

        private void EnsureLog()
        {
            int size = logSize < 0 ? 0 : logSize;
            if (_log == null || _log.Length != size)
            {
                _log = new string[size];
                _logCount = 0;
            }
        }

        private void AddLog(string line)
        {
            EnsureLog();
            if (_log.Length == 0) return;
            if (showTime) line = "[" + System.DateTime.Now.ToString("HH:mm") + "] " + line;
            for (int i = _log.Length - 1; i > 0; i--) _log[i] = _log[i - 1];
            _log[0] = line;
            if (_logCount < _log.Length) _logCount++;
            RefreshLog();
        }

        private void RefreshLog()
        {
            if (logLabel == null) return;
            string text = "";
            for (int i = 0; i < _logCount; i++)
            {
                if (i > 0) text += "\n";
                text += _log[i];
            }
            logLabel.text = text;
        }
    }
}
