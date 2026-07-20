# claude-unity-vpm

## 収録コンテンツ

### VPM リポジトリ
Claude-Unity 接続キット (MCP for Unity) の VPM 配布用リポジトリです。`index.json` を VCC / ALCOM に追加して利用します。

### グリス塗り替えリマインダー
CPU / GPU のグリス塗り替え時期を管理する Web アプリです。ブラウザだけで動作し、データは localStorage に保存されます。

- 場所: [`cpu-grease-reminder/index.html`](cpu-grease-reminder/index.html)
- GitHub Pages 有効時の URL: `https://naoking1993.github.io/claude-unity-vpm/cpu-grease-reminder/`

機能:
- デバイスごとに塗布日・グリス種類（プリセット5種＋カスタム間隔）を登録し、期限までの残り日数を色分け表示
- 負荷時温度のログを記録し、塗布直後の基準温度から +8°C 以上の上昇で塗り替えを警告
- 塗り替え予定日を `.ics` ファイルで書き出し、Google カレンダー等に登録して通知を受け取れる
- 塗り替え履歴の記録、JSON でのバックアップ書き出し / 読み込み
