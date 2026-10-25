#!/usr/bin/env python3
"""CI checks for the VPM listing (no Unity needed). Exit non-zero on any failure."""
import datetime, hashlib, itertools, json, re, subprocess, sys, tempfile, zipfile, pathlib, collections, shutil, os

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASE = "https://naoking1993.github.io/claude-unity-vpm/"
PKG = "com.coplaydev.unity-mcp"
UPSTREAM_VER, UPSTREAM_SHA = "9.7.1", "a3eee339e8af3a15cbd676a6f6dd78984eb4ce451b38196ea48ecc726cd81831"
# Published zips are immutable: once a version is listed, its zip and hash never change and it stays listed.
# Add the new version's hash here when releasing a new patched version.
PUBLISHED = {
    "9.7.1": UPSTREAM_SHA,
    "9.7.100": "b3dbfadb67fca4b7ef0227c0fbc289595b315267993fd2dc8c4b9dc56758b6eb",
    "9.7.101": "1af604c8e93e403fe5261ac0d35735a987aef2a0ec32046d23a85f949d18abd7",
}
# Entry timestamps: the official VCC writes each zip entry's date_time to the extracted file, and
# Unity's script compilation treats a .cs file as unchanged while its modification time is unchanged.
# So an entry whose content differs between two listed versions must also differ in date_time,
# whichever version the user updates from. 9.7.100 predates this rule and is grandfathered.
GRANDFATHERED_STAMP = {"9.7.100"}
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
zips = {}  # version -> ZipFile, for the cross-version timestamp check

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
    zips[ver] = z
    pj = json.loads(z.read("package.json").decode("utf-8"))
    if pj["name"] != PKG or pj["version"] != ver: fail(f"{ver}: zip package.json name/version != listing")
    if ver == UPSTREAM_VER:
        if sha(b) != UPSTREAM_SHA: fail("9.7.1 zip must stay the pristine upstream mirror")
        continue
    # patched build: identical to upstream except TerminalLauncher.cs and package.json
    if z.namelist() != up.namelist(): fail(f"{ver}: entry list differs from upstream")
    changed = [n for n in up.namelist() if z.read(n) != up.read(n)]
    if sorted(changed) != sorted([TL, "package.json"]): fail(f"{ver}: unexpected changed entries {changed}")
    if ver not in GRANDFATHERED_STAMP:
        for n in up.namelist():
            t_new, t_up = z.getinfo(n).date_time, up.getinfo(n).date_time
            if n in changed and not t_new > t_up:
                fail(f"{ver}: changed entry {n} must have a newer date_time than upstream ({t_new} <= {t_up})")
            if n not in changed and t_new != t_up:
                fail(f"{ver}: unchanged entry {n} must keep the upstream date_time ({t_new} != {t_up})")
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

# Timestamps: never in the future, and "same path + same date_time => same content" across every pair of
# listed versions (Unity compares against whatever version was compiled last, not just the previous one).
# DOS times have no zone and .NET reads them as local time, so a stamp must already be past in the
# westernmost zone (UTC-12) to be "never in the future" anywhere.
now = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None) - datetime.timedelta(hours=12)
grandfathered_pairs = {frozenset({UPSTREAM_VER, g}) for g in GRANDFATHERED_STAMP}
for ver, z in zips.items():
    for zi in z.infolist():
        if datetime.datetime(*zi.date_time) > now: fail(f"{ver}: {zi.filename} date_time {zi.date_time} is in the future")
for (v1, z1), (v2, z2) in itertools.combinations(sorted(zips.items()), 2):
    if frozenset({v1, v2}) in grandfathered_pairs: continue
    for n in set(z1.namelist()) & set(z2.namelist()):
        if z1.getinfo(n).date_time == z2.getinfo(n).date_time and z1.read(n) != z2.read(n):
            fail(f"{v1} vs {v2}: {n} has the same date_time but different content (Unity would not recompile it)")

# ---- packages built from source under packages/<name>/ (tools/build_vpm_package.py) ----
sys.path.insert(0, str(ROOT / "tools"))
import build_vpm_package as bvp  # noqa: E402
# Pin every published zip here, exactly like PUBLISHED above.
SRC_PUBLISHED = {
    "io.github.naoking1993.claude-creator-tools": {
        "0.1.0": "8bacf727b9a3057f283b85b6d26a96233c0e60a0587b1f45e685dccb27736ea1",
    },
}
LISTING_KEYS_FROM_PJ = ("name", "displayName", "version", "unity", "description", "author", "license",
                        "dependencies", "vpmDependencies", "samples")
mcp_guids = {l.split(b"guid: ")[1].strip() for n in up.namelist() if n.endswith(".meta")
             for l in up.read(n).splitlines() if l.startswith(b"guid: ")}
utc_minus_12 = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None) - datetime.timedelta(hours=12)
for name, releases in bvp.PACKAGES.items():
    listed = idx["packages"].get(name, {}).get("versions", {})
    pins = SRC_PUBLISHED.get(name, {})
    src_pj = json.loads((ROOT / "packages" / name / "package.json").read_text(encoding="utf-8"))
    for ver in pins:
        if ver not in listed: fail(f"{name} {ver}: published version must stay listed")
    for ver in listed:
        if ver not in pins: fail(f"{name} {ver}: not pinned in SRC_PUBLISHED")
        if ver not in releases: fail(f"{name} {ver}: no stamp in build_vpm_package.PACKAGES")
    stamps = list(releases.values())
    if len(set(stamps)) != len(stamps): fail(f"{name}: every release needs its own stamp (Unity recompiles by mtime)")
    for ver, e in listed.items():
        tag = f"{name} {ver}"
        if not re.fullmatch(r"\d+\.\d+\.\d+", ver): fail(f"{tag}: must be plain MAJOR.MINOR.PATCH")
        if e.get("version") != ver: fail(f"{tag}: key/version mismatch")
        if e.get("url") != f"{BASE}{name}-{ver}.zip": fail(f"{tag}: url must be {BASE}{name}-{ver}.zip")
        zp = ROOT / f"{name}-{ver}.zip"
        if not zp.exists(): fail(f"{tag}: zip missing"); continue
        b = zp.read_bytes()
        if e.get("zipSHA256") != sha(b): fail(f"{tag}: zipSHA256 != file")
        if ver in pins and pins[ver] != sha(b): fail(f"{tag}: published zip changed (bump the version instead)")
        z = zipfile.ZipFile(zp)
        names = z.namelist()
        pj = json.loads(z.read("package.json").decode("utf-8"))
        if pj.get("name") != name or pj.get("version") != ver: fail(f"{tag}: zip package.json name/version != listing")
        for k in LISTING_KEYS_FROM_PJ:
            if e.get(k) != pj.get(k): fail(f"{tag}: listing '{k}' != package.json")
        extra = set(e) - set(LISTING_KEYS_FROM_PJ) - {"url", "zipSHA256"}
        if extra: fail(f"{tag}: unexpected listing keys {sorted(extra)}")
        if ver == src_pj["version"] and bvp.build(name, ver, releases[ver]) != b:
            fail(f"{tag}: zip is not reproducible from packages/{name} (rebuild with tools/build_vpm_package.py)")
        stamp_set = {zi.date_time for zi in z.infolist()}
        if stamp_set != {tuple(releases.get(ver, ()))}: fail(f"{tag}: entries must all carry the release stamp")
        for zi in z.infolist():
            if datetime.datetime(*zi.date_time) > utc_minus_12: fail(f"{tag}: {zi.filename} date_time is in the future")
            if "\\" in zi.filename or zi.filename.startswith("/") or ".." in zi.filename.split("/"):
                fail(f"{tag}: bad entry name {zi.filename!r}")
        # .meta coverage: every asset (file or folder, except '~' folders themselves) has one, no orphans, unique GUIDs
        files = {n for n in names if not n.endswith(".meta")}
        folders = {"/".join(n.split("/")[:i]) for n in files for i in range(1, len(n.split("/")))}
        assets = {a for a in files | folders if not a.split("/")[-1].endswith("~")}
        metas = {n[:-5] for n in names if n.endswith(".meta")}
        for a in sorted(assets - metas): fail(f"{tag}: missing {a}.meta")
        for a in sorted(metas - assets): fail(f"{tag}: orphan {a}.meta")
        seen = {}
        for m in sorted(metas):
            g = next((l.split(b"guid: ")[1].strip() for l in z.read(m + ".meta").splitlines() if l.startswith(b"guid: ")), None)
            if not g or not re.fullmatch(rb"[0-9a-f]{32}", g): fail(f"{tag}: bad guid in {m}.meta"); continue
            if g in seen: fail(f"{tag}: duplicate guid {g.decode()} ({m}, {seen[g]})")
            if g in mcp_guids: fail(f"{tag}: guid {g.decode()} collides with com.coplaydev.unity-mcp")
            seen[g] = m
        for n in names:
            # JSON (package.json, .asmdef) must not carry a BOM; C# sources may (the U# samples use one on purpose so
            # Japanese comments open correctly in Windows editors).
            if n.endswith((".json", ".asmdef")) and z.read(n).startswith(b"\xef\xbb\xbf"):
                fail(f"{tag}: {n} has a UTF-8 BOM")

print("OK" if not errors else f"{len(errors)} failure(s)")
sys.exit(1 if errors else 0)
