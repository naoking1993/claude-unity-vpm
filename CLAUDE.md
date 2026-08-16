# claude-unity-vpm

CoplayDev/unity-mcp（MCP for Unity）を VCC/ALCOM から導入できるようにする非公式 VPM ミラー。
GitHub Pages で https://naoking1993.github.io/claude-unity-vpm/ に配信。

## 構成

- `index.json` — VPM リポジトリ本体（パッケージ一覧・バージョン・zipSHA256）
- `com.coplaydev.unity-mcp-*.zip` — 配布物（バイナリ。Read 禁止、`unzip -l` でメタ情報のみ）
- `.claude/` — トークン効率化キット（hooks / skills / commands）

## ルール

- パッケージの追加・更新は **vpm-maintenance** スキルの手順に従う
- トークン節約と精度維持の運用は **token-discipline** スキルに従う
- zip・バイナリは読まない。大きいファイルは Grep か Read の offset/limit で必要箇所のみ
- index.json 編集後は必ず JSON 構文検証してからコミット
- セッション中に CLAUDE.md / .claude/settings.json を書き換えない（キャッシュ破壊）
