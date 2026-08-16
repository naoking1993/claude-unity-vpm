# claude-unity-vpm

[CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp)（MCP for Unity）を
VCC / ALCOM から導入できるようにする**非公式 VPM ミラー**です。
© CoplayDev, MIT License。配布物は公式リリース由来です。

## 導入方法

VCC / ALCOM のリポジトリ追加に次の URL を登録してください:

```
https://naoking1993.github.io/claude-unity-vpm/index.json
```

## Claude Code トークン効率化キット

このリポジトリの `.claude/` には、Claude Code のトークン消費を抑えつつ精度を上げる
仕組み（痩せた CLAUDE.md + オンデマンド Skills + 浪費ブロック hooks + `/handoff`）が
入っています。他プロジェクトへもコピーして使えます。

詳細: [docs/token-efficiency-guide.md](docs/token-efficiency-guide.md)
