#!/usr/bin/env python3
"""CI checks for the Avatar Checker package (no Unity needed). Exit non-zero on any failure.

- every listed version is pinned in PUBLISHED and its zip is unchanged; the current version's zip is
  reproduced byte-for-byte from packages/<name>/ (tools/build_checker_zip.py)
- index.json entry == zip package.json; .meta files present with unique GUIDs
- Editor/Core compiles with mono and passes tests/checker/CoreTests.cs
- with --unity-dlls DIR (UnityEngine.dll + UnityEditor.dll), the whole Editor folder compiles"""
import hashlib, json, pathlib, shutil, subprocess, sys, tempfile, zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.dont_write_bytecode = True
sys.path.insert(0, str(ROOT / "tools"))
import build_checker_zip as B  # noqa: E402

BASE = "https://naoking1993.github.io/claude-unity-vpm/"
# Published zips are immutable. Add each new version's hash here when releasing it.
PUBLISHED = {
    "1.0.0": "7d8e9fd711ae64a3beb81f290de1aa016c1b04fd9f9214b4f0ef58b1595bd597",
}
errors = []
def fail(m): errors.append(m); print("FAIL:", m)
def sha(b): return hashlib.sha256(b).hexdigest()

for p in B.check_meta(write=False): fail(p)

idx = json.loads((ROOT / "index.json").read_text(encoding="utf-8"))
versions = idx["packages"].get(B.NAME, {}).get("versions", {})
if not versions: fail(f"{B.NAME} is not in index.json")
src_pj = json.loads((B.SRC / "package.json").read_text(encoding="utf-8"))
if src_pj["version"] not in versions: fail(f"source version {src_pj['version']} is not listed")
for ver in PUBLISHED:
    if ver not in versions: fail(f"{ver}: published version must stay listed")
for ver, e in versions.items():
    if ver not in PUBLISHED: fail(f"{ver}: not pinned in PUBLISHED"); continue
    if ver not in B.RELEASES: fail(f"{ver}: not in build_checker_zip.RELEASES")
    if e.get("url") != f"{BASE}{B.NAME}-{ver}.zip": fail(f"{ver}: url must be {BASE}{B.NAME}-{ver}.zip"); continue
    zp = ROOT / f"{B.NAME}-{ver}.zip"
    if not zp.exists(): fail(f"{ver}: zip missing"); continue
    b = zp.read_bytes()
    if e.get("zipSHA256") != sha(b): fail(f"{ver}: zipSHA256 != file")
    if PUBLISHED[ver] != sha(b): fail(f"{ver}: published zip changed (bump the version instead)")
    z = zipfile.ZipFile(zp)
    pj = json.loads(z.read("package.json").decode("utf-8"))
    entry = {k: v for k, v in e.items() if k not in ("url", "zipSHA256")}
    if entry != pj: fail(f"{ver}: index.json entry != zip package.json")
    if ver == src_pj["version"] and b != B.build(B.RELEASES[ver]):
        fail(f"{ver}: zip is not reproducible from packages/{B.NAME} (rebuild, or bump the version if it was published)")
    for n in z.namelist():
        if "\\" in n or n.startswith("/") or ".." in n.split("/"): fail(f"{ver}: bad entry name {n}")

if not (shutil.which("mcs") and shutil.which("mono")):
    fail("mcs/mono not available: tests skipped")
else:
    with tempfile.TemporaryDirectory() as td:
        exe = pathlib.Path(td, "core.exe")
        core = sorted(str(p) for p in (B.SRC / "Editor" / "Core").glob("*.cs"))
        subprocess.run(["mcs", "-nologo", f"-out:{exe}", *core, str(ROOT / "tests/checker/CoreTests.cs")], check=True)
        r = subprocess.run(["mono", str(exe)], capture_output=True, text=True)
        print(r.stdout[-400:])
        if r.returncode != 0: fail("CoreTests failed:\n" + r.stdout)
        if "--unity-dlls" in sys.argv:
            d = pathlib.Path(sys.argv[sys.argv.index("--unity-dlls") + 1])
            allcs = sorted(str(p) for p in (B.SRC / "Editor").rglob("*.cs"))
            r = subprocess.run(["mcs", "-nologo", "-target:library", f"-out:{td}/editor.dll",
                                f"-r:{d / 'UnityEngine.dll'}", f"-r:{d / 'UnityEditor.dll'}", *allcs],
                               capture_output=True, text=True)
            if r.returncode != 0: fail("Editor code does not compile against Unity:\n" + r.stdout + r.stderr)
            else: print("Editor code compiles against Unity")

print("OK" if not errors else f"{len(errors)} failure(s)")
sys.exit(1 if errors else 0)
