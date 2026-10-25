#!/bin/bash
# Run every offline check for Claude Creator Tools. Prepare $CCT_DEV with tests/dev/setup.sh first.
set -uo pipefail
: "${CCT_DEV:?set CCT_DEV (see tests/dev/README.md)}"
HERE=$(cd "$(dirname "$0")" && pwd); REPO=$(cd "$HERE/../.." && pwd)
PKG="$REPO/packages/io.github.naoking1993.claude-creator-tools"
CSC=$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | head -1); API=/usr/lib/mono/4.7.1-api
NET="-r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll -r:$API/Facades/netstandard.dll"
fail=0
step() { echo "== $1"; }
step "compile + MCP registration check"; "$HERE/cct/compile.sh" "$PKG" || fail=1
step "SDK ${SDK_VER:-3.10.5} serialized field check"
for f in "$REPO"/tests/vrc-fields/*.txt; do
  out=$(mono "$CCT_DEV/bin/FieldCheck.exe" check "$f"); echo "$(basename "$f"): ${out##*$'\n'}"
  case "$out" in *"FIELDCHECK OK"*) ;; *) echo "$out" | grep -v '^OK'; fail=1 ;; esac
done
step "pure unit tests"
T=$(mktemp -d)
dotnet "$CSC" -nologo -langversion:9 -nostdlib -noconfig -target:exe -out:$T/PureTests.exe $NET "$PKG/Editor/Core/CctJson.cs" "$PKG/Editor/Core/CctArgs.cs" "$HERE/cct/PureTests.cs" && mono $T/PureTests.exe || fail=1
dotnet "$CSC" -nologo -langversion:9 -nostdlib -noconfig -target:exe -out:$T/RankTests.exe $NET "$PKG/Editor/Tools/CctAvatarAudit.Ranks.cs" "$HERE/cct/RankTests.cs" && mono $T/RankTests.exe | tail -1 || fail=1
rm -rf "$T"
step "UdonSharp samples"; python3 -I "$HERE/udon/check_usharp.py" "$PKG/Samples~/WorldGimmicks/Scripts" | tail -1 || fail=1
step ".meta files"; python3 -I "$REPO/tools/gen_meta.py" --check "$PKG" | tail -1 || fail=1
[ $fail -eq 0 ] && echo "ALL CHECKS OK" || { echo "SOME CHECKS FAILED"; exit 1; }
