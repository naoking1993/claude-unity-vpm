#!/usr/bin/env python3
"""Build com.coplaydev.unity-mcp-<ver>.zip = upstream 9.7.1 listing zip + patches/*.patch + package.json field overrides.
Every other entry keeps its name, bytes, date and order, so the backslash entry layout that VCC already
accepts on the user's PC is preserved (CPython sets external_attr to 0600<<16; the DOS attribute byte stays 0).
If the output zip already exists (a published version), it is never overwritten: the build only reports
whether it reproduces the committed bytes. Run on Linux/WSL/CI; Windows Python rewrites '\\' in entry names."""
import hashlib, io, json, subprocess, sys, tempfile, zipfile, copy, pathlib, collections

ROOT = pathlib.Path(__file__).resolve().parent.parent
UPSTREAM_ZIP = ROOT / "com.coplaydev.unity-mcp-9.7.1.zip"
UPSTREAM_SHA = "a3eee339e8af3a15cbd676a6f6dd78984eb4ce451b38196ea48ecc726cd81831"
VERSION = "9.7.100"
PATCH_DIR = ROOT / "patches" / VERSION
OUT_ZIP = ROOT / f"com.coplaydev.unity-mcp-{VERSION}.zip"
TL = "Editor\\Services\\Server\\TerminalLauncher.cs"
OVERRIDES = {
    "version": VERSION,
    "displayName": "MCP for Unity (JP path fix)",
    "description_suffix": f"\n\n[claude-unity-vpm {VERSION}] Unofficial build of upstream 9.7.1 with one change: "
                          "the Windows server launch script runs 'chcp 65001' so non-ASCII project paths work. "
                          "See https://github.com/naoking1993/claude-unity-vpm",
}

def sha(b): return hashlib.sha256(b).hexdigest()

def main():
    raw = UPSTREAM_ZIP.read_bytes()
    if sha(raw) != UPSTREAM_SHA:
        sys.exit("upstream zip hash mismatch; refusing to build")
    src = zipfile.ZipFile(UPSTREAM_ZIP)
    # 1) TerminalLauncher.cs + patch (applied with GNU patch; must apply cleanly, no fuzz)
    with tempfile.TemporaryDirectory() as td:
        f = pathlib.Path(td, *TL.split("\\")); f.parent.mkdir(parents=True)
        f.write_bytes(src.read(TL))
        for p in sorted(PATCH_DIR.glob("*.patch")):
            subprocess.run(["patch", "-p1", "--forward", "--fuzz=0", "--no-backup-if-mismatch",
                            "-d", td, "-i", str(p)], check=True)
        tl_new = f.read_bytes()
    # 2) package.json overrides (key order preserved)
    pj = json.loads(src.read("package.json").decode("utf-8"), object_pairs_hook=collections.OrderedDict)
    assert pj["version"] == "9.7.1"
    pj["version"] = OVERRIDES["version"]
    pj["displayName"] = OVERRIDES["displayName"]
    pj["description"] = pj["description"] + OVERRIDES["description_suffix"]
    pj_new = (json.dumps(pj, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    replaced = {TL: tl_new, "package.json": pj_new}
    # 3) copy every entry in original order with original ZipInfo
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as out:
        for zi in src.infolist():
            data = replaced.get(zi.filename, src.read(zi))
            out.writestr(copy.copy(zi), data)
    built = buf.getvalue()
    if OUT_ZIP.exists():
        same = OUT_ZIP.read_bytes() == built
        print(OUT_ZIP.name, "reproduced" if same else "DIFFERS from the committed zip (not overwritten)", sha(built))
        sys.exit(0 if same else 1)
    OUT_ZIP.write_bytes(built)
    print(OUT_ZIP.name, sha(built))

if __name__ == "__main__":
    main()
