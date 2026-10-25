#!/bin/bash
# Prepare the offline verification environment for packages/io.github.naoking1993.claude-creator-tools.
# Downloads reference assemblies into $CCT_DEV (nothing is committed to the repo):
#   refs/sdk  Unity3D.SDK 2021.1.14.1 (UnityEngine.dll + UnityEditor.dll)   - nuget.org
#   refs/um   UnityEngine.Modules 2021.3.33 (engine module DLLs)            - nuget.org
#   refs/nj   Newtonsoft.Json 13.0.1                                        - nuget.org
#   ext/vrcsdk_{base,avatars,worlds}_3.10.5  official VRChat SDK packages   - github.com/vrchat/packages releases
#   refs/MCPForUnity.Editor.dll  compile stub built from the real McpForUnityToolAttribute.cs/Response.cs
#                                in the repo's com.coplaydev.unity-mcp-9.7.1.zip (MIT, CoplayDev)
# Needs: curl, unzip, python3, dotnet-sdk-8.0 (Roslyn csc), mono-complete (runtime + 4.7.1 reference assemblies).
#   CCT_DEV=~/.cache/cct-dev tests/dev/setup.sh
set -euo pipefail
: "${CCT_DEV:?set CCT_DEV to an empty work folder}"
HERE=$(cd "$(dirname "$0")" && pwd); REPO=$(cd "$HERE/../.." && pwd)
SDK_VER=3.10.5
mkdir -p "$CCT_DEV"/{refs,ext,bin}
cd "$CCT_DEV"
nuget() { # id version dest
  [ -d "refs/$3" ] && return 0
  curl -sSfL -o "refs/$3.nupkg" "https://api.nuget.org/v3-flatcontainer/$1/$2/$1.$2.nupkg"
  mkdir -p "refs/$3" && unzip -q -o "refs/$3.nupkg" -d "refs/$3" && rm "refs/$3.nupkg"
}
nuget unity3d.sdk 2021.1.14.1 sdk
nuget unityengine.modules 2021.3.33 um
nuget newtonsoft.json 13.0.1 nj
for p in base avatars worlds; do
  d="ext/vrcsdk_${p}_${SDK_VER}"
  [ -d "$d" ] && continue
  curl -sSfL -o "ext/$p.zip" "https://github.com/vrchat/packages/releases/download/${SDK_VER}/com.vrchat.${p}-${SDK_VER}.zip"
  mkdir -p "$d" && unzip -q -o "ext/$p.zip" -d "$d" && rm "ext/$p.zip"
done
CSC=$(ls /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | head -1); API=/usr/lib/mono/4.7.1-api
NET="-r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll -r:$API/Facades/netstandard.dll"
# MCP for Unity compile stub: the two real public types our adapters use + an McpLog no-op
STUB=$(mktemp -d)
python3 -I - "$REPO/com.coplaydev.unity-mcp-9.7.1.zip" "$STUB" <<'PY'
import sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
for name in ("Editor\\Tools\\McpForUnityToolAttribute.cs", "Editor\\Helpers\\Response.cs"):
    open(sys.argv[2] + "/" + name.split("\\")[-1], "wb").write(z.read(name))
open(sys.argv[2] + "/McpLogStub.cs", "w").write(
    "namespace MCPForUnity.Editor.Helpers { public static class McpLog { public static void Debug(string m){} "
    "public static void Info(string m, bool always = true){} public static void Warn(string m){} public static void Error(string m){} } }\n")
PY
dotnet "$CSC" -nologo -langversion:9 -nostdlib -noconfig -target:library -out:refs/MCPForUnity.Editor.dll $NET -r:refs/nj/lib/net45/Newtonsoft.Json.dll "$STUB"/*.cs
rm -rf "$STUB"
for t in MetaCheck FieldCheck; do
  dotnet "$CSC" -nologo -langversion:9 -nostdlib -noconfig -target:exe -out:bin/$t.exe $NET "$HERE/cct/$t.cs"
done
echo "CCT_DEV ready: $CCT_DEV"
