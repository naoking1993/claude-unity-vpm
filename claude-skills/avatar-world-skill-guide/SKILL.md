---
name: avatar-world-skill-guide
description: VRChatのアバター改変・ワールド制作に関する利用可能なスキルの一覧や、どのスキルを使うかを尋ねられたときに使う。複数の用途にまたがり選択に迷う依頼にも使う。用途が明確な単一作業では、該当する専門スキルを直接使う。
---

> Claude登録用の個人スキル・スナップショット（2026-09-29）。元のローカルスキルと共有正本は変更していない。Codex固有のツール名や絶対パスは実行環境に合わせて確認する。別スキルへの相対リンクは、この配布物の同名スキルを必要に応じて読み込む。スキルの読み込みだけではUnity／Blenderへの接続や操作を許可したことにはならない。
>
> 2026-10-25 改訂（Claude 側で追記）: ワールド制作用の `vrchat-world-creation` と、Claude Creator Tools（`cct_*` MCP ツール）の使い方をまとめた `claude-creator-tools` を表に追加し、「ワールド制作専用のスキルはない」という最終段落を書き換えた。ほかの行は変更していない。

# アバター・ワールド制作のスキル一覧

依頼の目的から次の入口を選ぶ。一覧のために全スキルを読み込まず、必要なスキルと、その作業に不可欠な参照先だけを読む。複数の工程がある場合は、工程ごとに選び直す。

| 依頼の目的 | 最初に読むスキル | 境界・使い分け |
| --- | --- | --- |
| Blenderで髪を作る・局所修正する・解析する | [blender-avatar-hair](../blender-avatar-hair/SKILL.md) | 衣装の修正は衣装用へ。髪の手順・データの正本は下記の共有版。 |
| Blenderで衣装・装飾を再現・フィッティングし、継ぎ目・縁・UV・法線・ウェイトを直す | [blender-avatar-outfit-fitting](../blender-avatar-outfit-fitting/SKILL.md) | 髪型制作やUnity側だけの設定変更には使わない。 |
| Unityでアバターの配色・アトラス・肌材質を調整し、元色を保って比較する | [vrchat-avatar-material-tuning](../vrchat-avatar-material-tuning/SKILL.md) | lilToon/Reakin等の部位別適用・色と質感の分離・外観検証。衣装形状や表情制御の修理だけなら各専門スキルへ。 |
| Unityでアバターを軽量化し、ビルド後の外観変化・衣装の貫通や覆い不足を直す | [vrchat-avatar-optimization](../vrchat-avatar-optimization/SKILL.md) | 負荷と外観を同じ処理段階で比較。表情・歩行の競合だけなら競合診断用、Blenderでの衣装制作だけなら衣装用へ。 |
| Unityで手持ちギミックを導入し、握り位置・握り姿勢を合わせる | [vrchat-hand-prop-setup](../vrchat-hand-prop-setup/SKILL.md) | 歩行・表情の競合そのものは競合診断用へ。 |
| Unityでヲタ芸・ダンス速度のBPM操作を作成・修正する | [vrchat-bpm-controls](../vrchat-bpm-controls/SKILL.md) | 曲BPMを調べるだけの依頼や握り位置の修正には使わない。 |
| 表情の選定・表情メニュー・表情固定を作る、または表情・ジェスチャー・歩行・姿勢・アクセサリ切替・ワールド固定の不具合を調べる | [vrchat-expression-debugging](../vrchat-expression-debugging/SKILL.md) | 表情の固定とワールド空間への固定を区別する。アバターのワールド固定はワールドそのものの制作ではない。 |
| 改造したギミックを別アバター・別プロジェクトへ移すためにまとめる | [vrchat-gimmick-packaging](../vrchat-gimmick-packaging/SKILL.md) | 独立プレハブ・unitypackage化が対象。アバター本体のアップロードや単独の握り位置・BPM調整には使わない。 |
| VRChatのワールドを作る・直す（シーン設定、スポーン、レイヤー、ライトベイク、ミラー、Udon同期ギミック、Quest/iOS対応、アップロード前の確認） | [vrchat-world-creation](../vrchat-world-creation/SKILL.md) | アバターをワールド空間に固定するギミックはワールド制作ではない（表情・競合診断用へ）。SDK・Unityの対応版だけの質問は vrchat-unity-version-check。 |
| Claude Creator Tools の `cct_*` ツールで、アバター/ワールドの監査、多方向キャプチャと比較、変更前スナップショットと復元、Animator解析、依存関係レポートを行う | [claude-creator-tools](../claude-creator-tools/SKILL.md) | 何を直すかの判断は各専門スキル。接続確認・破壊的操作の規律は unity-mcp-operations。 |
| MCP for Unityを通じてUnity・Unityプロジェクトを操作する | unity-mcp-operations（Claude側の既存スキル。この配布物には含めない） | 上の専門用途でも、UnityをMCPで操作する工程では併読する。文書の確認だけなら不要。 |

髪の資料は別ZIPの `blender-avatar-hair` スキルに同梱した。必要に応じてそのスキルを登録して読む。元PCの正本は `C:/Users/naoki/ai-shared/hair/SKILL.md` であり、この配布版は日付付きの写し。実行条件・禁止事項・復旧方法は各スキルと参照先で確認し、この一覧に手順を複製しない。

ワールド制作は 2026-10-25 に追加した `vrchat-world-creation` を使う。一次資料は VRChat 公式ドキュメント（2026-10-20 時点）と SDK 3.10.5 で、Unity実機・VRChat実機での手順確認はまだない（v0）。PC のローカルスキルに `world-udon-gimmick`・`world-lighting-bake` がある場合は目的が重なり発火が不安定になるため、登録前にどちらを使うか決めて片方を無効化する。表にない用途は未登録と明示し、無関係なスキルへ割り当てない。
