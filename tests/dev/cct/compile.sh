#!/bin/bash
# Compile-check Claude Creator Tools against Unity 2021.1 reference DLLs (+ MCP for Unity stub).
# Usage: compile.sh [package_dir]   -> exit 0 if both assemblies compile.
#        ONLY=ClassName compile.sh   -> compile Core + CctEnvironment + CctMenu + only that tool's files
#        (Tools/ClassName*.cs, Menu/ClassNameMenu.cs, Mcp/ClassNameTool.cs) so parallel edits elsewhere don't interfere.
set -u
SP=${CCT_DEV:?set CCT_DEV to the folder prepared by tests/dev/setup.sh}
HERE=$(cd "$(dirname "$0")" && pwd)
P=${1:-$(cd "$HERE/../../.." && pwd)/packages/io.github.naoking1993.claude-creator-tools}
OUT=$(mktemp -d)
CSC=$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | head -1)
API=/usr/lib/mono/4.7.1-api
REFS="-r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll -r:$API/System.Xml.dll -r:$API/Facades/netstandard.dll -r:$SP/refs/sdk/lib/UnityEngine.dll -r:$SP/refs/sdk/lib/UnityEditor.dll"
COMMON="-nologo -langversion:9 -nostdlib -noconfig -target:library -define:UNITY_EDITOR -nowarn:1701,1702"
if [ -n "${ONLY:-}" ]; then
  mapfile -t CORE < <( (find "$P/Editor/Core" -name '*.cs'; ls "$P/Editor/Tools/CctEnvironment.cs" "$P/Editor/Menu/CctMenu.cs"; find "$P/Editor/Tools" -name "$ONLY.cs" -o -name "$ONLY.*.cs"; find "$P/Editor/Menu" -name "${ONLY}Menu.cs") | sort -u)
  mapfile -t MCP < <( (ls "$P/Editor/Mcp/CctMcpBridge.cs" "$P/Editor/Mcp/CctEnvironmentTool.cs"; find "$P/Editor/Mcp" -name "${ONLY}Tool.cs") | sort -u)
else
  mapfile -t CORE < <(find "$P/Editor" -name '*.cs' -not -path "$P/Editor/Mcp/*" | sort)
  mapfile -t MCP < <(find "$P/Editor/Mcp" -name '*.cs' | sort)
fi
echo "== core: ${#CORE[@]} files"
dotnet "$CSC" $COMMON $REFS -out:$OUT/Naoking.ClaudeCreatorTools.Editor.dll "${CORE[@]}" 2>&1 | sed "s#$P/##g"
rc1=${PIPESTATUS[0]}
rc2=1
if [ $rc1 -eq 0 ]; then
  echo "== mcp: ${#MCP[@]} files"
  dotnet "$CSC" $COMMON -define:CCT_UNITY_MCP $REFS -r:$OUT/Naoking.ClaudeCreatorTools.Editor.dll -r:$SP/refs/MCPForUnity.Editor.dll -r:$SP/refs/nj/lib/net45/Newtonsoft.Json.dll -out:$OUT/Naoking.ClaudeCreatorTools.Mcp.dll "${MCP[@]}" 2>&1 | sed "s#$P/##g"
  rc2=${PIPESTATUS[0]}
fi
if [ $rc1 -eq 0 ] && [ $rc2 -eq 0 ]; then
  [ -z "${ONLY:-}" ] && mkdir -p $SP/bin && cp $OUT/*.dll $SP/bin/ 2>/dev/null
  echo "COMPILE OK"
  mono $SP/bin/MetaCheck.exe $OUT/Naoking.ClaudeCreatorTools.Mcp.dll $SP/refs $SP/refs/sdk/lib $SP/refs/nj/lib/net45
  exit $?
fi
echo "COMPILE FAILED (core=$rc1 mcp=$rc2)"
exit 1
