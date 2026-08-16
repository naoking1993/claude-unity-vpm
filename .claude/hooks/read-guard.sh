#!/bin/sh
# PreToolUse(Read) hook: バイナリ・巨大ファイルの丸読みをブロックしてトークン浪費を防ぐ。
# 判定できない場合は必ず許可側に倒す（fail-open）。exit 2 のみがブロック。

INPUT=$(cat 2>/dev/null) || exit 0
[ -n "$INPUT" ] || exit 0

FILE=$(printf '%s' "$INPUT" | sed -n 's/.*"file_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)
[ -n "$FILE" ] || exit 0

# バイナリ拡張子は無条件ブロック
case "$FILE" in
  *.zip|*.unitypackage|*.tgz|*.tar.gz|*.7z|*.dll|*.so|*.dylib|*.exe|*.bin|*.fbx|*.asset)
    echo "ブロック: $FILE はバイナリです。Read せず、必要なら 'unzip -l' や 'ls -la' でメタ情報だけ取得してください。" >&2
    exit 2
    ;;
esac

# offset/limit 付きの部分読みは許可
case "$INPUT" in
  *'"limit"'*|*'"offset"'*) exit 0 ;;
esac

# 400KB 超のテキスト丸読みはブロック（Grep か offset/limit を促す）
if [ -f "$FILE" ]; then
  SIZE=$(wc -c < "$FILE" 2>/dev/null) || exit 0
  case "$SIZE" in *[!0-9]*) exit 0 ;; esac
  if [ "$SIZE" -gt 400000 ]; then
    echo "ブロック: $FILE は ${SIZE} bytes と大きく、丸読みはトークンを浪費します。Grep で該当箇所を特定するか、Read の offset/limit で必要範囲だけ読んでください。" >&2
    exit 2
  fi
fi

exit 0
