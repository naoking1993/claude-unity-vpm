#!/usr/bin/env python3
"""Build the reference chain used by check_usharp.py to compile-check UdonSharp scripts offline.

Outputs (in build/):
  VRC.Udon.Serialization.OdinSerializer.dll  real SDK 3.10.5 source
  VRC.Udon.dll                               real SDK 3.10.5 source, ONE patched line (see PATCHES)
  UdonSharp.Runtime.dll                      real SDK 3.10.5 source (Integrations/UdonSharp/Runtime)
  UdonSharp.Lib.dll                          real SDK 3.10.5 source (Runtime/Libraries)
  UnityEngine.UI.dll, Unity.TextMeshPro.dll  tiny hand-written STUBS (stubs/*.Stub.cs) - packages not available offline
  udon_externs.txt                           Udon extern signature table extracted from VRC.Udon.Wrapper / VRCWrapperModules
  refs.txt                                   the full reference list for compiling user U# scripts

Reference assemblies: mono 4.5 implementation assemblies + Facades (netstandard 2.1, like Unity 2022's
.NET Standard 2.1 profile), Unity 2021.3.33 engine module DLLs (UnityEngine.dll facade excluded), and the
real VRChat SDK 3.10.5 runtime DLLs (VRCSDKBase, VRCSDK3, VRC.Udon.Common, VRC.Dynamics, VRCEconomy, ...).
Usage: python3 build_chain.py [--force]
"""
import glob, os, shutil, subprocess, sys

SP = os.environ.get("CCT_DEV") or sys.exit("set CCT_DEV to the folder prepared by tests/dev/setup.sh")
W = SP + "/ext/vrcsdk_worlds_3.10.5"
B = SP + "/ext/vrcsdk_base_3.10.5"
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = SP + "/udon-build"
CSC = sorted(glob.glob("/usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll"))[-1]
MONO = "/usr/lib/mono/4.5"

# Project-wide defines a VRChat world project (SDK 3.10.5, Unity 2022.3) has when U# compiles:
# UDONSHARP (UdonSharpEditorManager.AddProjectDefine), UDON / VRC_SDK_VRCSDK3 / VRC_ENABLE_PLAYER_PERSISTENCE
# (VRC.SDKBase EnvConfig.SetActiveSDKDefines; persistence is enabled because ClientSim ships in com.vrchat.worlds).
WORLD_DEFINES = ["UDONSHARP", "UDON", "VRC_SDK_VRCSDK3", "VRC_ENABLE_PLAYER_PERSISTENCE",
                 "UNITY_2022_3_OR_NEWER", "UNITY_2021_3_OR_NEWER", "UNITY_5_3_OR_NEWER"]

# Source patches applied to a scratch copy (never to the SDK corpus). Each (file, old, new) must match exactly once.
PATCHES = [
    ("UdonBehaviour.cs",
     "_componentIndexFixed = GetComponentIndex();",
     "_componentIndexFixed = 0; // CCT-HARNESS PATCH: Component.GetComponentIndex() is Unity 2022.1+, our refs are 2021.3"),
]

NOWARN = "-nowarn:0618,0612,0414,0649,0169,0067,0108,0114,0162,0168,0219,0429,0436,1701,1702,8019"


def base_refs():
    refs = [f"{MONO}/{n}.dll" for n in ("mscorlib", "System", "System.Core", "System.Xml", "System.Xml.Linq",
                                        "System.Runtime.Serialization", "System.Data", "System.Numerics")]
    refs += sorted(glob.glob(MONO + "/Facades/*.dll"))
    refs += sorted(p for p in glob.glob(SP + "/refs/um/lib/net45/*.dll") if not p.endswith("/UnityEngine.dll"))
    return refs


def vrc_runtime_dlls():
    found = glob.glob(B + "/**/*.dll", recursive=True) + glob.glob(W + "/**/*.dll", recursive=True)
    keep = []
    for p in found:
        name = os.path.basename(p)
        if "/Editor/" in p or "/Integrations/" in p or "/Plugins/Windows/" in p or "/x86" in p or "/x64/" in p:
            continue
        if "-Editor" in name or name.startswith(("Microsoft.CodeAnalysis", "Algolia", "vrc_", "portmidi")):
            continue
        keep.append(p)
    return sorted(keep)


def cs(d, exclude=()):
    return sorted(p for p in glob.glob(d + "/**/*.cs", recursive=True) if not any(x in p for x in exclude))


def build(name, sources, refs, defines=(), unsafe=False):
    out = f"{OUT}/{name}.dll"
    cmd = ["dotnet", CSC, "-nologo", "-langversion:9", "-nostdlib", "-noconfig", "-target:library", NOWARN,
           f"-out:{out}"] + (["-unsafe"] if unsafe else []) + ([f"-define:{';'.join(defines)}"] if defines else [])
    cmd += [f"-r:{r}" for r in refs] + sources
    p = subprocess.run(cmd, capture_output=True, text=True)
    errs = [l for l in p.stdout.splitlines() if ": error " in l]
    print(f"  {name}: {'OK' if p.returncode == 0 else 'FAILED'} ({len(sources)} files)")
    for l in errs[:20]:
        print("    " + l.replace(SP, "$SP"))
    return out if p.returncode == 0 else None


def patched_copy(src_dir, dst_dir, exclude):
    if os.path.isdir(dst_dir):
        shutil.rmtree(dst_dir)
    files = cs(src_dir, exclude)
    for f in files:
        rel = os.path.relpath(f, src_dir)
        os.makedirs(os.path.dirname(os.path.join(dst_dir, rel)), exist_ok=True)
        shutil.copyfile(f, os.path.join(dst_dir, rel))
    for rel, old, new in PATCHES:
        path = os.path.join(dst_dir, rel)
        text = open(path, encoding="utf-8-sig").read()
        if text.count(old) != 1:
            sys.exit(f"patch for {rel} no longer matches exactly once (SDK changed?) - update PATCHES")
        open(path, "w", encoding="utf-8").write(text.replace(old, new))
    return cs(dst_dir)


def main():
    force = "--force" in sys.argv
    os.makedirs(OUT, exist_ok=True)
    marker = OUT + "/refs.txt"
    if os.path.exists(marker) and not force:
        print("chain already built (use --force to rebuild):", marker)
        return 0
    refs = base_refs() + vrc_runtime_dlls()
    print("building U# reference chain ->", OUT)
    odin = build("VRC.Udon.Serialization.OdinSerializer", cs(W + "/Runtime/Udon/Serialization/OdinSerializer"),
                 refs, ["UNITY_2022_3_OR_NEWER"], unsafe=True)
    if not odin:
        return 1
    udon_src = patched_copy(W + "/Runtime/Udon", OUT + "/src_patched/VRC.Udon", ("/Serialization/OdinSerializer/", "/Editor/"))
    udon = build("VRC.Udon", udon_src, refs + [odin], ["UDON", "VRC_SDK_VRCSDK3"], unsafe=True)
    if not udon:
        return 1
    us = build("UdonSharp.Runtime", cs(W + "/Integrations/UdonSharp/Runtime", exclude=("/Libraries/",)),
               refs + [odin, udon], WORLD_DEFINES)
    if not us:
        return 1
    lib = build("UdonSharp.Lib", cs(W + "/Integrations/UdonSharp/Runtime/Libraries"), refs + [odin, udon, us], WORLD_DEFINES)
    ui = build("UnityEngine.UI", [HERE + "/stubs/UnityEngine.UI.Stub.cs"], refs)
    tmp = build("Unity.TextMeshPro", [HERE + "/stubs/Unity.TextMeshPro.Stub.cs"], refs + [ui]) if ui else None
    if not (ui and tmp):
        return 1
    ext = W + "/Runtime/Udon/External"
    subprocess.run([sys.executable, "-I", HERE + "/tools/extract_externs.py", OUT + "/udon_externs.txt",
                    ext + "/VRC.Udon.Wrapper.dll", ext + "/VRC.Udon.VRCWrapperModules.dll"], check=True)
    all_refs = refs + [odin, udon, us] + ([lib] if lib else []) + [ui, tmp]
    with open(marker, "w", encoding="utf-8") as f:
        f.write("\n".join(all_refs) + "\n")
    print("CHAIN OK" if lib else "CHAIN PARTIAL (UdonSharp.Lib failed; user scripts do not reference it)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
