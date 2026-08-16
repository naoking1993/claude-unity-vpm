#!/bin/sh
# PreToolUse(Bash) hook: 出力が巨大になりがちなコマンドを早期ブロックする。
# 判定できない場合は必ず許可側に倒す（fail-open）。exit 2 のみがブロック。

INPUT=$(cat 2>/dev/null) || exit 0
[ -n "$INPUT" ] || exit 0

# git log -p / --patch はパッチ全文が流れてトークンを浪費する
if printf '%s' "$INPUT" | grep -qE 'git log[^"]*( -p| --patch)'; then
  echo "ブロック: 'git log -p' は出力が巨大です。'git log --oneline -n 20' で概要を見て、必要なコミットだけ 'git show <hash> --stat' を使ってください。" >&2
  exit 2
fi

# バイナリ配布物を cat/head 等で標準出力に流すのを防ぐ
if printf '%s' "$INPUT" | grep -qE '(^|[";&| ])(cat|head|tail|less|more) [^"]*\.(zip|unitypackage|tgz|dll|fbx)'; then
  echo "ブロック: バイナリを標準出力に流すとコンテキストが汚染されます。'unzip -l <file>' や 'ls -la' でメタ情報だけ取得してください。" >&2
  exit 2
fi

# ルートからの find 全走査を防ぐ
if printf '%s' "$INPUT" | grep -qE 'find / (-|\.)'; then
  echo "ブロック: ルートからの find は出力が巨大です。対象ディレクトリを絞るか Glob ツールを使ってください。" >&2
  exit 2
fi

exit 0
