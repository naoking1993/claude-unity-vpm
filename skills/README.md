# skills/

claude.ai の同期スキル（`~/.claude/skills/synced/.../<skill>/`）の作業用コピー。
このリポジトリは配布物ではなく、セッションのコンテナが消えても差分が残るように置いてある。

反映のしかた: このディレクトリの内容を、claude.ai のスキル編集画面（または
`~/.claude/skills/` 配下の当該スキル）へそのまま上書きする。

- `hair-modeling/` — 髪型メッシュの解析スキル。`scripts/inspect_hair.py` 0.2.0。
  検証は `python skills/hair-modeling/scripts/inspect_hair.py --selftest`（30項目）と
  `python skills/hair-modeling/scripts/turn_bench.py`（turn の解像度非依存性）で走る。
  どちらも Blender 不要。numpy は無くても turn 系は動く（PCA 系の項目だけ None になる）。
