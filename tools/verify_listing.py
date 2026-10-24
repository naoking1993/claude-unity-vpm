#!/usr/bin/env python3
"""CI checks for the VPM listing (no Unity needed). Exit non-zero on any failure.
Run on Linux/WSL/CI; Windows Python rewrites '\\' in entry names and reports false failures."""
import datetime, hashlib, itertools, json, re, subprocess, sys, tempfile, zipfile, pathlib, collections, shutil, os
sys.dont_write_bytecode = True  # keep tools/__pycache__ out of the working tree
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from mirror_upstream_zip import compress_archive_order

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASE = "https://naoking1993.github.io/claude-unity-vpm/"
PKG = "com.coplaydev.unity-mcp"
# Unmodified upstream mirrors the patched builds are made from. 9.7.1 is listed; 10.3.0 is a build base only
# (not listed in index.json) and must match tools/upstream/v10.3.0.manifest (git blob hashes of v10.3.0:MCPForUnity).
BASELINES = {
    "9.7.1": "a3eee339e8af3a15cbd676a6f6dd78984eb4ce451b38196ea48ecc726cd81831",
    "10.3.0": "83c5c41e118efda0b5096a23d1dd3bb5e7910f5fb4d0badc8e1ccf0e51509891",
}
REF_VER = "9.7.1"
REF_TIME = (1985, 10, 26, 17, 15, 0)  # date_time of every entry of the 9.7.1 zip
# Baselines built by mirror_upstream_zip.py: version -> (tag, stamp of entries that differ from the 9.7.1 zip).
# The stamp is deliberately one day before the 10.3.100 release stamp (2026-10-23, day before the 2026-10-24
# release): changed entries of a patched release must be strictly newer than the base's, and a release stamp of
# 2026-10-24 would be in the future at UTC-12 on build day. Enforced below (RELEASE_STAMPS), fixed forever.
MIRRORS = {"10.3.0": ("v10.3.0", (2026, 10, 22, 0, 0, 0))}
RELEASE_STAMPS = {"9.7.101": (2026, 9, 28, 0, 0, 0), "10.3.100": (2026, 10, 23, 0, 0, 0)}  # = build_patched_zip RELEASES
# Published zips are immutable: once a version is listed, its zip and hash never change and it stays listed.
# Add the new version's hash here when releasing a new patched version.
PUBLISHED = {
    "9.7.1": BASELINES["9.7.1"],
    "9.7.100": "b3dbfadb67fca4b7ef0227c0fbc289595b315267993fd2dc8c4b9dc56758b6eb",
    "9.7.101": "1af604c8e93e403fe5261ac0d35735a987aef2a0ec32046d23a85f949d18abd7",
    "10.3.100": "938eadc3c264d3fa3e67fd849fb39ca9489c4ad66fcfb8f78ac6bbf0c77068f7",
}
TL = "Editor\\Services\\Server\\TerminalLauncher.cs"
PATCHED = {  # version: (base version, exactly the entries that differ from the base)
    "9.7.100": ("9.7.1", {TL, "package.json"}),
    "9.7.101": ("9.7.1", {TL, "package.json"}),
    "10.3.100": ("10.3.0", {"package.json"}),
}
# Entry timestamps: the official VCC writes each zip entry's date_time to the extracted file, and
# Unity's script compilation treats a .cs file as unchanged while its modification time is unchanged.
# So an entry whose content differs between two listed versions must also differ in date_time,
# whichever version the user updates from. 9.7.100 predates this rule and is grandfathered.
GRANDFATHERED_STAMP = {"9.7.100"}
ALLOWED_PJ_DIFF = {"version", "displayName", "description"}
# ZipInfo fields every entry shares with the 9.7.1 zip (CPython's writestr rewrites external_attr 0 as 0600<<16)
ZI_FIELDS = ("compress_type", "create_system", "create_version", "extract_version", "flag_bits", "extra", "comment")
ZI_REF = (8, 0, 20, 20, 0, b"", b"")
errors = []
def fail(m): errors.append(m); print("FAIL:", m)
def sha(b): return hashlib.sha256(b).hexdigest()
def blob_sha1(b): return hashlib.sha1(b"blob %d\0" % len(b) + b).hexdigest()
def fwd(n): return n.replace("\\", "/")

idx = json.loads((ROOT / "index.json").read_text(encoding="utf-8"))
versions = idx["packages"][PKG]["versions"]
zips = {}  # version -> ZipFile (every baseline and listed zip), for the cross-version timestamp check

# Baselines: present, pinned, 9.7.1-style layout (backslash names, no directory entries, Compress-Archive order)
for ver, h in BASELINES.items():
    p = ROOT / f"{PKG}-{ver}.zip"
    if not p.exists(): fail(f"baseline {ver}: zip missing {p.name}"); continue
    if sha(p.read_bytes()) != h: fail(f"baseline {ver}: zip is not the pinned upstream mirror"); continue
    z = zips[ver] = zipfile.ZipFile(p)
    if z.comment: fail(f"baseline {ver}: zip has a comment")
    names = z.namelist()
    if any("/" in n or n.endswith("\\") for n in names): fail(f"baseline {ver}: entry names must be '\\'-separated files")
    if list(map(fwd, names)) != compress_archive_order(set(map(fwd, names))):
        fail(f"baseline {ver}: entry order is not the Compress-Archive order")
    for zi in z.infolist():
        if tuple(getattr(zi, f) for f in ZI_FIELDS) != ZI_REF or zi.external_attr not in (0, 0o600 << 16):
            fail(f"baseline {ver}: {zi.filename} ZipInfo fields differ from the 9.7.1 layout"); break
ref = zips.get(REF_VER)
if ref and any(zi.date_time != REF_TIME for zi in ref.infolist()): fail(f"{REF_VER}: every entry must have date_time {REF_TIME}")
for ver, (tag, stamp) in MIRRORS.items():
    if not REF_TIME < stamp: fail(f"baseline {ver}: stamp {stamp} must be later than the 9.7.1 time {REF_TIME}")
    for pv, (base, _) in PATCHED.items():  # the constants themselves, not just the entries they produce
        if base == ver and pv in RELEASE_STAMPS and not stamp < RELEASE_STAMPS[pv]:
            fail(f"baseline {ver}: stamp {stamp} must be earlier than the {pv} release stamp {RELEASE_STAMPS[pv]}")
    z = zips.get(ver)
    if z is None or ref is None: continue
    mp = ROOT / "tools" / "upstream" / f"{tag}.manifest"
    if not mp.exists(): fail(f"baseline {ver}: manifest missing {mp}"); continue
    manifest = dict(l.split("\t") for l in mp.read_text(encoding="utf-8").splitlines())
    if set(map(fwd, z.namelist())) != set(manifest): fail(f"baseline {ver}: entry set != {mp.name} path set")
    for n in z.namelist():
        data = z.read(n)
        if n in ref.namelist() and data == ref.read(n): want = REF_TIME
        else: want = stamp
        if blob_sha1(data) != manifest.get(fwd(n)): fail(f"baseline {ver}: {n} content != git blob in {mp.name}")
        if z.getinfo(n).date_time != want: fail(f"baseline {ver}: {n} date_time {z.getinfo(n).date_time} != {want}")

# Listing: every published version listed, every listed version pinned (baseline 10.3.0 is not listed)
for ver in PUBLISHED:
    if ver not in versions: fail(f"{ver}: published version must stay listed")
for ver in versions:
    if ver not in PUBLISHED: fail(f"{ver}: not pinned in PUBLISHED (record its zipSHA256 there)")
    if ver not in BASELINES and ver not in PATCHED: fail(f"{ver}: neither a baseline nor in PATCHED")

for ver, e in versions.items():
    if e["version"] != ver: fail(f"{ver}: key/version mismatch")
    if not re.fullmatch(r"\d+\.\d+\.\d+", ver): fail(f"{ver}: must be plain MAJOR.MINOR.PATCH (VCC prerelease/build-metadata rules)")
    if any(s in ver.lower() for s in ("-beta", "-alpha", "-rc", "-pre")): fail(f"{ver}: prerelease marker")
    if not e["url"].startswith(BASE): fail(f"{ver}: url not on listing host"); continue
    zp = ROOT / e["url"][len(BASE):]
    if zp.name != f"{PKG}-{ver}.zip": fail(f"{ver}: url file name must be {PKG}-{ver}.zip")
    if not zp.exists(): fail(f"{ver}: zip missing {zp.name}"); continue
    b = zp.read_bytes()
    if e["zipSHA256"] != sha(b): fail(f"{ver}: zipSHA256 != file")
    if e["zipSHA256"] != e["zipSHA256"].lower(): fail(f"{ver}: zipSHA256 must be lowercase")
    if ver in PUBLISHED and sha(b) != PUBLISHED[ver]: fail(f"{ver}: published zip changed (bump the version instead)")
    z = zipfile.ZipFile(zp)
    zips[ver] = z
    pj = json.loads(z.read("package.json").decode("utf-8"))
    if pj["name"] != PKG or pj["version"] != ver: fail(f"{ver}: zip package.json name/version != listing")
    for k in ("dependencies", "vpmDependencies"):
        if e.get(k, {}) != pj.get(k, {}): fail(f"{ver}: listing {k} != package.json {k}")
    if ver in BASELINES:
        if sha(b) != BASELINES[ver]: fail(f"{ver}: zip must stay the pristine upstream mirror")
        continue
    if ver not in PATCHED: continue
    base_ver, expected = PATCHED[ver]
    up = zips.get(base_ver)
    if up is None: fail(f"{ver}: base {base_ver} unavailable"); continue
    # patched build: identical to its base except the expected entries
    if z.namelist() != up.namelist(): fail(f"{ver}: entry list differs from base {base_ver}")
    changed = [n for n in up.namelist() if z.read(n) != up.read(n)]
    if set(changed) != expected or len(changed) != len(expected): fail(f"{ver}: unexpected changed entries {changed}")
    if ver not in GRANDFATHERED_STAMP:
        for n in up.namelist():
            t_new, t_up = z.getinfo(n).date_time, up.getinfo(n).date_time
            if n in changed and not t_new > t_up:
                fail(f"{ver}: changed entry {n} must have a newer date_time than base ({t_new} <= {t_up})")
            if n in changed and ver in RELEASE_STAMPS and t_new != RELEASE_STAMPS[ver]:
                fail(f"{ver}: changed entry {n} date_time {t_new} != pinned release stamp {RELEASE_STAMPS[ver]}")
            if n not in changed and t_new != t_up:
                fail(f"{ver}: unchanged entry {n} must keep the base date_time ({t_new} != {t_up})")
    upj = json.loads(up.read("package.json").decode("utf-8"))
    diffkeys = {k for k in set(upj) | set(pj) if upj.get(k) != pj.get(k)}
    if not diffkeys <= ALLOWED_PJ_DIFF: fail(f"{ver}: package.json changed keys {diffkeys}")
    if not pj["description"].startswith(upj["description"]): fail(f"{ver}: package.json description must extend the base description")
    if TL not in expected: continue
    tl = z.read(TL)
    if tl.startswith(b"\xef\xbb\xbf"): fail(f"{ver}: TerminalLauncher.cs has BOM")
    # source must equal base + patches (exact, no fuzz)
    with tempfile.TemporaryDirectory() as td:
        f = pathlib.Path(td, *TL.split("\\")); f.parent.mkdir(parents=True); f.write_bytes(up.read(TL))
        patches = sorted((ROOT / "patches" / ver).glob("*.patch"))
        if not patches: fail(f"{ver}: no patches/{ver}/*.patch")
        for p in patches:
            subprocess.run(["patch", "-s", "-p1", "--forward", "--fuzz=0", "-d", td, "-i", str(p)], check=True)
        if f.read_bytes() != tl: fail(f"{ver}: TerminalLauncher.cs != base + patches/{ver}")
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
# zips, the unlisted 10.3.0 baseline included (Unity compares against whatever version was compiled last, not
# just the previous one). DOS times have no zone and .NET reads them as local time, so a stamp must already be
# past in the westernmost zone (UTC-12) to be "never in the future" anywhere.
now = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None) - datetime.timedelta(hours=12)
grandfathered_pairs = {frozenset({PATCHED[g][0], g}) for g in GRANDFATHERED_STAMP}
for ver, z in zips.items():
    for zi in z.infolist():
        if datetime.datetime(*zi.date_time) > now: fail(f"{ver}: {zi.filename} date_time {zi.date_time} is in the future")
for (v1, z1), (v2, z2) in itertools.combinations(sorted(zips.items()), 2):
    if frozenset({v1, v2}) in grandfathered_pairs: continue
    for n in set(z1.namelist()) & set(z2.namelist()):
        if z1.getinfo(n).date_time == z2.getinfo(n).date_time and z1.read(n) != z2.read(n):
            fail(f"{v1} vs {v2}: {n} has the same date_time but different content (Unity would not recompile it)")

print("OK" if not errors else f"{len(errors)} failure(s)")
sys.exit(1 if errors else 0)
