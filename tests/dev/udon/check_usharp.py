#!/usr/bin/env python3
"""Compile-check + lint UdonSharp (U#) scripts offline against the real VRChat Worlds SDK 3.10.5.

  python3 check_usharp.py [--strict] [--no-externs] [--no-require-syncmode] [--dump-sigs] [--define X;Y] [--rebuild]
                          <files or dirs...>
  --no-externs          skip stage 3; --no-require-syncmode: no warning for a missing [UdonBehaviourSyncMode];
  --dump-sigs           print every computed Udon extern signature; --rebuild: rebuild build/ (build_chain.py --force).

Stages (all in one Roslyn 4.8 compilation run by tools/UsharpCheck.cs on .NET 8):
  1. C# 9 compile (U# parses with LanguageVersion.CSharp9 on Unity 2022.3) against build/refs.txt:
     real SDK DLLs (VRCSDKBase, VRCSDK3, VRC.Udon.Common, VRC.Dynamics, VRCEconomy, ...), VRC.Udon /
     OdinSerializer / UdonSharp.Runtime / UdonSharp.Lib built from the real SDK sources (build_chain.py),
     Unity 2021.3.33 engine modules, and tiny stubs for UnityEngine.UI + TextMeshPro.
  2. U# feature lint (USL0xx/1xx): constructs UdonSharp 1.x cannot compile, derived from
     Integrations/UdonSharp/Editor/Compiler (BinderSyntaxVisitor, *Symbol.Bind, UdonSharpCompilerV1,
     CompilationContext) - try/throw, lambdas, LINQ, local functions, ?., is/as, initializers, new(),
     discards, generic collections, nested types, interfaces, structs, static fields, ctors, generic
     methods, sync-type/sync-mode rules, [NetworkCallable]/[FieldChangeCallback] rules, event names,
     SendCustomEvent targets, network events sent to a BehaviourSyncMode.None receiver (USL163: never
     delivered at runtime, docs networking/events). Supported features (properties, string interpolation,
     static methods, user enums, overloads, out/ref, params, ??, switch on constants, inheritance) are NOT flagged.
     Variable types (USL125) must have an Udon wrapper module; an extern enum must appear in an exposed signature.
  3. Udon extern exposure (USL120): every extern member access is mapped to its Udon signature exactly
     like CompilerUdonInterface.GetUdonMethodName and looked up in the table extracted from
     VRC.Udon.Wrapper.dll + VRC.Udon.VRCWrapperModules.dll (build/udon_externs.txt). When the declaring type's
     signature is not exposed, the receiver type, its base types and interfaces are tried, exactly like U#'s
     BoundExpression.FindAlternateInvocation / FindAlternateAccessor.
Self-tests: tests/good (must pass) and tests/bad (every file must fail; see the 'Expected' comment in each).
Stub caveat: UnityEngine.UI / TMPro are tiny stubs declaring only members that exist in the real packages with the
same declaring types; a member missing from a stub fails to compile here (conservative), it never passes falsely.
Exit 0 = OK. --strict also fails on warnings (e.g. missing [UdonBehaviourSyncMode]).
"""
import glob, json, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SP = os.environ.get("CCT_DEV") or sys.exit("set CCT_DEV to the folder prepared by tests/dev/setup.sh")
BUILD = SP + "/udon-build"
SDK_UDON = SP + "/ext/vrcsdk_worlds_3.10.5/Runtime/Udon"
ROSLYN = sorted(glob.glob("/usr/lib/dotnet/sdk/*/Roslyn/bincore"))[-1]
CSC = ROSLYN + "/csc.dll"
NETREF = sorted(glob.glob("/usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net8.0"))[-1]
FRAMEWORK_VERSION = os.path.basename(os.path.dirname(os.path.dirname(NETREF)))
TOOL_SRC = HERE + "/tools/UsharpCheck.cs"
TOOL_DLL = BUILD + "/UsharpCheck.dll"
DEFAULT_DEFINES = "UDONSHARP;UDON;VRC_SDK_VRCSDK3;VRC_ENABLE_PLAYER_PERSISTENCE;UNITY_2022_3_OR_NEWER;UNITY_2021_3_OR_NEWER;UNITY_5_3_OR_NEWER"


def ensure_chain(rebuild):
    if rebuild or not os.path.exists(BUILD + "/refs.txt"):
        r = subprocess.run([sys.executable, "-I", HERE + "/build_chain.py", "--force"])
        if r.returncode != 0:
            sys.exit("reference chain build failed")
    if rebuild or not os.path.exists(BUILD + "/udon_events.txt"):
        write_events()


def write_events():
    """Built-in Udon event names (e.g. _onPlayerTriggerEnter -> OnPlayerTriggerEnter) from the real VRC.Udon sources."""
    import re
    names = set()
    for f in (SDK_UDON + "/UdonBehaviour.cs", SDK_UDON + "/UdonManager.cs"):
        for m in re.finditer(r'"_([a-z][A-Za-z0-9]*)"', open(f, encoding="utf-8-sig").read()):
            n = m.group(1)
            names.add(n[0].upper() + n[1:])
    with open(BUILD + "/udon_events.txt", "w", encoding="utf-8") as f:
        f.write("\n".join(sorted(names)) + "\n")


def ensure_tool(rebuild):
    if not rebuild and os.path.exists(TOOL_DLL) and os.path.getmtime(TOOL_DLL) >= os.path.getmtime(TOOL_SRC):
        return
    refs = sorted(glob.glob(NETREF + "/*.dll")) + [ROSLYN + "/Microsoft.CodeAnalysis.dll", ROSLYN + "/Microsoft.CodeAnalysis.CSharp.dll"]
    cmd = ["dotnet", CSC, "-nologo", "-langversion:latest", "-nostdlib", "-noconfig", "-target:exe", "-nowarn:1701,1702,8632",
           f"-out:{TOOL_DLL}"] + [f"-r:{r}" for r in refs] + [TOOL_SRC]
    p = subprocess.run(cmd, capture_output=True, text=True)
    if p.returncode != 0:
        print(p.stdout)
        sys.exit("UsharpCheck tool build failed")
    with open(BUILD + "/UsharpCheck.runtimeconfig.json", "w") as f:
        json.dump({"runtimeOptions": {"tfm": "net8.0", "framework": {"name": "Microsoft.NETCore.App", "version": FRAMEWORK_VERSION},
                                      "rollForward": "Major"}}, f)


def expand(paths):
    files = []
    for p in paths:
        if os.path.isdir(p):
            files += sorted(glob.glob(os.path.join(p, "**", "*.cs"), recursive=True))
        else:
            files.append(p)
    return files


def main(argv):
    flags, paths, defines, i = [], [], DEFAULT_DEFINES, 0
    rebuild = False
    while i < len(argv):
        a = argv[i]
        if a in ("--strict", "--no-externs", "--no-require-syncmode", "--dump-sigs"):
            flags.append(a)
        elif a == "--define":
            i += 1; defines = defines + ";" + argv[i]
        elif a == "--rebuild":
            rebuild = True
        elif a in ("-h", "--help"):
            print(__doc__); return 0
        else:
            paths.append(a)
        i += 1
    files = expand(paths)
    if not files:
        print(__doc__); return 2
    ensure_chain(rebuild)
    ensure_tool(rebuild)
    env = dict(os.environ, USHARP_ROSLYN_DIR=ROSLYN, DOTNET_CLI_TELEMETRY_OPTOUT="1")
    cmd = ["dotnet", "exec", "--runtimeconfig", BUILD + "/UsharpCheck.runtimeconfig.json", TOOL_DLL,
           "--refs", BUILD + "/refs.txt", "--externs", BUILD + "/udon_externs.txt",
           "--synctypes", SDK_UDON + "/UdonNetworkTypes.cs", "--events", BUILD + "/udon_events.txt",
           "--define", defines] + flags + files
    return subprocess.run(cmd, env=env).returncode


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
