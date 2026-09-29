# skills/

Claude Code のスキル（`~/.claude/skills/` に置くもの）の作業用コピー。
セッションのコンテナは使い捨てなので、スキルに加えた変更はここに commit しないと残らない。

## 中身

| スキル | 内容 |
|---|---|
| `hair-modeling/` | 髪型メッシュのリバースエンジニアリングと生成レシピ化（Blender） |

## 反映のしかた

```sh
cp -r skills/hair-modeling ~/.claude/skills/
```

Claude 側で同期されたスキル（`~/.claude/skills/synced/.../hair-modeling`）を使っている場合は、
そちらを差し替えるか、同期元（claude.ai のスキル）にこの内容を反映する。

## 確認

`hair-modeling` のスクリプトは Blender 非依存の自己テストを持つ。
反映後にこれが通ることを確認する:

```sh
python3 skills/hair-modeling/scripts/inspect_hair.py --selftest
# → SELFTEST PASSED (0 failures, 0 skipped)
# numpy が無い環境では PCA 依存の 11 項目が SKIP になるが、完走する
```
