#!/usr/bin/env python3
"""CI checks for the VPM listing (no Unity needed). Exit non-zero on any failure."""
import hashlib, json, re, subprocess, sys, tempfile, zipfile, pathlib, collections, shutil, os

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASE = "https://naoking1993.github.io/claude-unity-vpm/"
PKG = "com.coplaydev.unity-mcp"
UPSTREAM_VER, UPSTREAM_SHA = "9.7.1", "a3eee339e8af3a15cbd676a6f6dd78984eb4ce451b38196ea48ecc726cd81831"
# Published zips are immutable: once a version is listed, its zip and hash never change and it stays listed.
# Add the new version's hash here when releasing a new patched version.
PUBLISHED = {
    "9.7.1": UPSTREAM_SHA,
    "9.7.100": "b3dbfadb67fca4b7ef0227c0fbc289595b315267993fd2dc8c4b9dc56758b6eb",
}
TL = "Editor\\Services\\Server\\TerminalLauncher.cs"
ALLOWED_PJ_DIFF = {"version", "displayName", "description"}
errors = []
def fail(m): errors.append(m); print("FAIL:", m)
def sha(b): return hashlib.sha256(b).hexdigest()

idx = json.loads((ROOT / "index.json").read_text(encoding="utf-8"))
versions = idx["packages"][PKG]["versions"]
up_path = ROOT / f"{PKG}-{UPSTREAM_VER}.zip"
if sha(up_path.read_bytes()) != UPSTREAM_SHA: fail("baseline 9.7.1 zip is not the pristine upstream mirror")
up = zipfile.ZipFile(up_path)
for ver in PUBLISHED:
    if ver not in versions: fail(f"{ver}: published version must stay listed")
for ver in versions:
    if ver not in PUBLISHED: fail(f"{ver}: not pinned in PUBLISHED (record its zipSHA256 there)")

for ver, e in versions.items():
    if e["version"] != ver: fail(f"{ver}: key/version mismatch")
    if not re.fullmatch(r"\d+\.\d+\.\d+", ver): fail(f"{ver}: must be plain MAJOR.MINOR.PATCH (VCC prerelease/build-metadata rules)")
    if any(s in ver.lower() for s in ("-beta", "-alpha", "-rc", "-pre")): fail(f"{ver}: prerelease marker")
    if not e["url"].startswith(BASE): fail(f"{ver}: url not on listing host"); continue
    zp = ROOT / e["url"][len(BASE):]
    if not zp.exists(): fail(f"{ver}: zip missing {zp.name}"); continue
    b = zp.read_bytes()
    if e["zipSHA256"] != sha(b): fail(f"{ver}: zipSHA256 != file")
    if e["zipSHA256"] != e["zipSHA256"].lower(): fail(f"{ver}: zipSHA256 must be lowercase")
    if ver in PUBLISHED and sha(b) != PUBLISHED[ver]: fail(f"{ver}: published zip changed (bump the version instead)")
    z = zipfile.ZipFile(zp)
    pj = json.loads(z.read("package.json").decode("utf-8"))
    if pj["name"] != PKG or pj["version"] != ver: fail(f"{ver}: zip package.json name/version != listing")
    if ver == UPSTREAM_VER:
        if sha(b) != UPSTREAM_SHA: fail("9.7.1 zip must stay the pristine upstream mirror")
        continue
    # patched build: identical to upstream except TerminalLauncher.cs and package.json
    if z.namelist() != up.namelist(): fail(f"{ver}: entry list differs from upstream")
    changed = [n for n in up.namelist() if z.read(n) != up.read(n)]
    if sorted(changed) != sorted([TL, "package.json"]): fail(f"{ver}: unexpected changed entries {changed}")
    upj = json.loads(up.read("package.json").decode("utf-8"))
    diffkeys = {k for k in set(upj) | set(pj) if upj.get(k) != pj.get(k)}
    if not diffkeys <= ALLOWED_PJ_DIFF: fail(f"{ver}: package.json changed keys {diffkeys}")
    for k in ("dependencies", "vpmDependencies"):
        if e.get(k, {}) != pj.get(k, {}): fail(f"{ver}: listing {k} != package.json {k}")
    tl = z.read(TL)
    if tl.startswith(b"\xef\xbb\xbf"): fail(f"{ver}: TerminalLauncher.cs has BOM")
    # source must equal upstream + patches (exact, no fuzz)
    with tempfile.TemporaryDirectory() as td:
        f = pathlib.Path(td, *TL.split("\\")); f.parent.mkdir(parents=True); f.write_bytes(up.read(TL))
        for p in sorted((ROOT / "patches" / ver).glob("*.patch")):
            subprocess.run(["patch", "-s", "-p1", "--forward", "--fuzz=0", "-d", td, "-i", str(p)], check=True)
        if f.read_bytes() != tl: fail(f"{ver}: TerminalLauncher.cs != upstream + patches/{ver}")
    # behaviour: compile the shipped file for the Windows branch and run it on Japanese paths
    if shutil.which("mcs") and shutil.which("mono"):
        with tempfile.TemporaryDirectory() as td:
            t = pathlib.Path(td)
            (t / "TerminalLauncher.cs").write_bytes(tl)
            (t / "ITerminalLauncher.cs").write_bytes(z.read("Editor\\Services\\Server\\ITerminalLauncher.cs"))
            exe = t / "h.exe"
            subprocess.run(["mcs", "-nologo", "-define:UNITY_EDITOR_WIN", f"-out:{exe}",
                            str(t / "TerminalLauncher.cs"), str(t / "ITerminalLauncher.cs"),
                            str(ROOT / "tests/cs/Stubs.cs"), str(ROOT / "tests/cs/Harness.cs")], check=True)
            for name in ("テスト1", "霧島", "テスト"):  # 'テスト' ends in 0x88: DBCS trail-byte case
                proj = t / name; proj.mkdir()
                pid = str(proj / "Library/MCPForUnity/RunState/mcp_http_8080.pid")
                cmd = ('C:\\Users\\naoki\\.local\\bin\\uvx.exe --from "mcpforunityserver@file:///C:/w/x-9.7.1+fastmcp4.2-py3-none-any.whl" '
                       f'mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --pidfile {pid} --unity-instance-token 0123456789abcdef0123456789abcdef')
                out = subprocess.run(["mono", str(exe), str(proj), cmd], check=True, capture_output=True,
                                     env={**os.environ, "LANG": "C.UTF-8"}).stdout.decode().splitlines()
                got = bytes.fromhex(out[0])
                want = b"@echo off\r\nchcp 65001>nul\r\ncls\r\n" + cmd.encode("utf-8") + b"\r\n"
                if got != want: fail(f"{ver}: generated .cmd bytes wrong for {name}: {got[:60]!r}")
                if not out[1].startswith('cmd.exe /c start "MCP Server" cmd.exe /k "'): fail(f"{ver}: psi changed")
    else:
        fail("mcs/mono not available: behavioural test skipped")

print("OK" if not errors else f"{len(errors)} failure(s)")
sys.exit(1 if errors else 0)
