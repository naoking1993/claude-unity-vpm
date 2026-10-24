#!/usr/bin/env python3
"""Build com.coplaydev.unity-mcp-<ver>.zip = base upstream mirror zip + patches/<ver>/*.patch + package.json overrides.

Each release in RELEASES names its base (an unmodified upstream mirror pinned in BASES: the 9.7.1 listing zip, or
a zip built by mirror_upstream_zip.py), its stamp, whether patches/<ver>/ is applied to TerminalLauncher.cs
(10.3.100 has no patches: only package.json changes), its displayName and its description suffix.

Unchanged entries keep their base name, bytes, date and order, so the backslash entry layout that
VCC already accepts on the user's PC is preserved (CPython sets external_attr to 0600<<16; the DOS
attribute byte stays 0).

Replaced entries (TerminalLauncher.cs, package.json) get the release's fixed `stamp` as their zip
date_time. The official VCC writes the zip entry time to the extracted file's LastWriteTime, and
Unity's script compilation decides whether a .cs file changed by its modification time only. If a
replaced entry kept the base time (1985-10-26), Unity would keep the old compiled DLL after an
update. 9.7.100 was published before this was known (stamp None) and is kept byte-for-byte.
The stamp is a constant (not "now") so that builds are reproducible; use the day before the release
date at 00:00:00 so it is never in the future in any time zone (DOS times have no time zone).

Existing zips (published versions) are never overwritten: the build only reports whether it
reproduces the committed bytes. Run on Linux/WSL/CI; Windows Python rewrites '\\' in entry names."""
import collections, copy, hashlib, io, json, pathlib, subprocess, sys, tempfile, zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
PKG = "com.coplaydev.unity-mcp"
BASES = {  # unmodified upstream mirrors, pinned
    "9.7.1": "a3eee339e8af3a15cbd676a6f6dd78984eb4ce451b38196ea48ecc726cd81831",
    "10.3.0": "83c5c41e118efda0b5096a23d1dd3bb5e7910f5fb4d0badc8e1ccf0e51509891",
}
TL = "Editor\\Services\\Server\\TerminalLauncher.cs"
JP_FIX = ("MCP for Unity (JP path fix)",
          "\n\n[claude-unity-vpm {version}] Unofficial build of upstream 9.7.1 with one change: "
          "the Windows server launch script runs 'chcp 65001' so non-ASCII project paths work. "
          "See https://github.com/naoking1993/claude-unity-vpm")
KIT = ("MCP for Unity (Claude-Unity kit)",
       "\n\n[claude-unity-vpm {version}] Unofficial build of upstream 10.3.0 (tag v10.3.0); only package.json "
       "differs. Expects the FastMCP 4 server wheel via MCPForUnity.GitUrlOverride; with it unset, uvx looks for "
       "mcpforunityserver=={version}, which does not exist on PyPI, so the launch fails instead of silently using "
       "the FastMCP 3 server. See https://github.com/naoking1993/claude-unity-vpm")
RELEASES = {
    # version: base, date_time for replaced entries (None = keep base time; 9.7.100 only, see docstring),
    #          whether patches/<version>/ is applied to TerminalLauncher.cs, (displayName, description suffix)
    "9.7.100": {"base": "9.7.1", "stamp": None, "patches": True, "text": JP_FIX},
    "9.7.101": {"base": "9.7.1", "stamp": (2026, 9, 28, 0, 0, 0), "patches": True, "text": JP_FIX},
    # 10.3.100: released 2026-10-24 -> 2026-10-23; the 10.3.0 base is stamped 2026-10-22 (mirror_upstream_zip.py
    # STAMPS) so the replaced package.json is strictly newer than the base entry. Both are immutable now.
    "10.3.100": {"base": "10.3.0", "stamp": (2026, 10, 23, 0, 0, 0), "patches": False, "text": KIT},
}


def sha(b):
    return hashlib.sha256(b).hexdigest()


def patched_tl(src, version):
    """TerminalLauncher.cs of `src` + patches/<version>/*.patch (GNU patch; must apply cleanly, no fuzz)."""
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
        return f.read_bytes()


def build(src, version, rel):
    replaced = {}
    # 1) TerminalLauncher.cs + patches
    if rel["patches"]:
        replaced[TL] = patched_tl(src, version)
    # 2) package.json overrides (key order preserved)
    pj = json.loads(src.read("package.json").decode("utf-8"), object_pairs_hook=collections.OrderedDict)
    assert pj["version"] == rel["base"]
    pj["version"] = version
    pj["displayName"] = rel["text"][0]
    pj["description"] = pj["description"] + rel["text"][1].format(version=version)
    replaced["package.json"] = (json.dumps(pj, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    # 3) copy every entry in base order; replaced entries get the release stamp
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as out:
        for zi in src.infolist():
            zi2 = copy.copy(zi)
            if zi.filename in replaced and rel["stamp"] is not None:
                zi2.date_time = rel["stamp"]
            out.writestr(zi2, replaced.get(zi.filename, src.read(zi)))
    return buf.getvalue()


def main():
    bases = {}
    for ver, h in BASES.items():
        p = ROOT / f"{PKG}-{ver}.zip"
        if sha(p.read_bytes()) != h:
            sys.exit(f"base {ver} zip hash mismatch; refusing to build")
        bases[ver] = zipfile.ZipFile(p)
    failed = False
    for version, rel in RELEASES.items():
        out_zip = ROOT / f"{PKG}-{version}.zip"
        built = build(bases[rel["base"]], version, rel)
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
