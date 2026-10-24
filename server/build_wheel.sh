#!/usr/bin/env bash
# Rebuild the FastMCP 4 server wheel from upstream + server/patches/<version>/ and compare its SHA256
# with server/SHA256SUMS. Linux/WSL/CI; needs git, GNU patch, uv (network for PyPI build deps).
#
#   server/build_wheel.sh [path-to-existing-unity-mcp-clone] [output-dir]
#
# The build is reproducible: sources come from the pinned upstream tag, patches apply with --fuzz=0,
# the build backend is pinned in the patched pyproject.toml, and SOURCE_DATE_EPOCH fixes the zip times.
set -euo pipefail

VERSION="10.3.0+fastmcp4.1"
TAG="v10.3.0"
UPSTREAM_COMMIT="aa5fc638d623f56178d50329d9ad8c541a57fe66"
SOURCE_DATE_EPOCH=1792800000   # 2026-10-24 00:00:00 UTC (the day the port was built); never "now"
export SOURCE_DATE_EPOCH

HERE=$(cd "$(dirname "$0")" && pwd)
OUT=${2:-$HERE/dist}
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

SRC=${1:-}
if [ -z "$SRC" ]; then
  git clone -q --filter=blob:none --branch "$TAG" https://github.com/CoplayDev/unity-mcp "$WORK/unity-mcp"
  SRC="$WORK/unity-mcp"
fi
ACTUAL=$(git -C "$SRC" rev-parse "$TAG^{commit}")
if [ "$ACTUAL" != "$UPSTREAM_COMMIT" ]; then
  echo "upstream tag $TAG resolves to $ACTUAL, expected $UPSTREAM_COMMIT; refusing to build" >&2
  exit 1
fi

mkdir -p "$WORK/srv"
git -C "$SRC" archive "$TAG" Server | tar -x -C "$WORK/srv" --strip-components=1
for p in "$HERE/patches/$VERSION"/*.patch; do
  patch -p1 -d "$WORK/srv" --forward --fuzz=0 --no-backup-if-mismatch -i "$p" >/dev/null
done
grep -q "^version = \"$VERSION\"" "$WORK/srv/pyproject.toml" || { echo "patched pyproject.toml does not carry version $VERSION" >&2; exit 1; }

( cd "$WORK/srv" && uv build --wheel --out-dir "$WORK/dist" >/dev/null )
WHL=$(ls "$WORK/dist"/*.whl)
NAME=$(basename "$WHL")
SHA=$(sha256sum "$WHL" | cut -d' ' -f1)
echo "$SHA  $NAME"

if [ -f "$HERE/SHA256SUMS" ] && grep -q " $NAME\$" "$HERE/SHA256SUMS"; then
  EXPECTED=$(grep " $NAME\$" "$HERE/SHA256SUMS" | cut -d' ' -f1)
  if [ "$SHA" = "$EXPECTED" ]; then
    echo "reproduced (matches server/SHA256SUMS)"
  else
    echo "DIFFERS from server/SHA256SUMS ($EXPECTED)" >&2
    exit 1
  fi
fi

mkdir -p "$OUT"
if [ -f "$OUT/$NAME" ]; then
  if cmp -s "$WHL" "$OUT/$NAME"; then echo "$OUT/$NAME: identical, not rewritten"; else echo "$OUT/$NAME exists and differs; not overwritten" >&2; exit 1; fi
else
  cp "$WHL" "$OUT/$NAME"
  echo "written $OUT/$NAME"
fi
