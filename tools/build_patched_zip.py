#!/usr/bin/env python3
"""Build com.coplaydev.unity-mcp-<ver>.zip = upstream 9.7.1 listing zip + patches/<ver>/*.patch + package.json overrides.

Unchanged entries keep their upstream name, bytes, date and order, so the backslash entry layout that
VCC already accepts on the user's PC is preserved (CPython sets external_attr to 0600<<16; the DOS
attribute byte stays 0).

Replaced entries (TerminalLauncher.cs, package.json) get the release's fixed `stamp` as their zip
date_time. The official VCC writes the zip entry time to the extracted file's LastWriteTime, and
Unity's script compilation decides whether a .cs file changed by its modification time only. If a
replaced entry kept the upstream time (1985-10-26), Unity would keep the old compiled DLL after an
update. 9.7.100 was published before this was known (stamp None) and is kept byte-for-byte.
The stamp is a constant (not "now") so that builds are reproducible; use the day before the release
date at 00:00:00 so it is never in the future in any time zone (DOS times have no time zone).

Existing zips (published versions) are never overwritten: the build only reports whether it
reproduces the committed bytes. Run on Linux/WSL/CI; Windows Python rewrites '\\' in entry names."""
import collections, copy, hashlib, io, json, pathlib, subprocess, sys, tempfile, zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
UPSTREAM_ZIP = ROOT / "com.coplaydev.unity-mcp-9.7.1.zip"
UPSTREAM_SHA = "a3eee339e8af3a15cbd676a6f6dd78984eb4ce451b38196ea48ecc726cd81831"
TL = "Editor\\Services\\Server\\TerminalLauncher.cs"
DISPLAY_NAME = "MCP for Unity (JP path fix)"
RELEASES = {
    # version: date_time for replaced entries (None = keep upstream time; 9.7.100 only, see docstring)
    "9.7.100": {"stamp": None},
    "9.7.101": {"stamp": (2026, 9, 28, 0, 0, 0)},
}


def sha(b):
    return hashlib.sha256(b).hexdigest()


def description_suffix(version):
    return (f"\n\n[claude-unity-vpm {version}] Unofficial build of upstream 9.7.1 with one change: "
            "the Windows server launch script runs 'chcp 65001' so non-ASCII project paths work. "
            "See https://github.com/naoking1993/claude-unity-vpm")


def build(src, version, stamp):
    # 1) TerminalLauncher.cs + patches (applied with GNU patch; must apply cleanly, no fuzz)
    with tempfile.TemporaryDirectory() as td:
        f = pathlib.Path(td, *TL.split("\\"))
        f.parent.mkdir(parents=True)
        f.write_bytes(src.read(TL))
        patches = sorted((ROOT / "patches" / version).glob("*.patch"))
        if not patches:
            sys.exit(f"no patches for {version}")
        for p in patches:
            subprocess.run(["patch", "-p1", "--forward", "--fuzz=0", "--no-backup-if-mismatch",
                            "-d", td, "-i", str(p)], check=True, stdout=subprocess.DEVNULL)
        tl_new = f.read_bytes()
    # 2) package.json overrides (key order preserved)
    pj = json.loads(src.read("package.json").decode("utf-8"), object_pairs_hook=collections.OrderedDict)
    assert pj["version"] == "9.7.1"
    pj["version"] = version
    pj["displayName"] = DISPLAY_NAME
    pj["description"] = pj["description"] + description_suffix(version)
    pj_new = (json.dumps(pj, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    replaced = {TL: tl_new, "package.json": pj_new}
    # 3) copy every entry in original order; replaced entries get the release stamp
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as out:
        for zi in src.infolist():
            zi2 = copy.copy(zi)
            if zi.filename in replaced and stamp is not None:
                zi2.date_time = stamp
            out.writestr(zi2, replaced.get(zi.filename, src.read(zi)))
    return buf.getvalue()


def main():
    raw = UPSTREAM_ZIP.read_bytes()
    if sha(raw) != UPSTREAM_SHA:
        sys.exit("upstream zip hash mismatch; refusing to build")
    src = zipfile.ZipFile(UPSTREAM_ZIP)
    failed = False
    for version, rel in RELEASES.items():
        out_zip = ROOT / f"com.coplaydev.unity-mcp-{version}.zip"
        built = build(src, version, rel["stamp"])
        if out_zip.exists():
            same = out_zip.read_bytes() == built
            print(out_zip.name, "reproduced" if same else "DIFFERS from the committed zip (not overwritten)", sha(built))
            failed |= not same
        else:
            out_zip.write_bytes(built)
            print(out_zip.name, "written", sha(built))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
