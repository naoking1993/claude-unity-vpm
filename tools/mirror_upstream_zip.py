#!/usr/bin/env python3
"""Build com.coplaydev.unity-mcp-<ver>.zip = UNMODIFIED mirror of upstream MCPForUnity/ at a git tag, laid out
like the 9.7.1 listing zip (which came from the OpenUPM tarball via PowerShell Compress-Archive), plus
tools/upstream/<tag>.manifest = "<path>\\t<git blob sha1>" per file in `git ls-tree -r` order.

Layout rules (reproduce the 9.7.1 zip exactly): entry names use '\\' separators, no directory entries, no
zip comment; entries in Compress-Archive order (compress_archive_order below); ZipInfo compress_type=8,
create_system=0, versions 20/20, flag_bits=0, no extra/comment. date_time is the 9.7.1 time
(1985-10-26 17:15:00) for an entry that is byte-identical to the 9.7.1 zip entry of the same name, and the
tag's fixed STAMP otherwise: a constant (not "now") so the build is reproducible, dated so that it is never
in the future in any zone (DOS times have no zone), and earlier than the stamp of the patched release built
on top of it (verify_listing.py requires "same name + same date_time => same bytes" across every pair).
File bytes come from git blobs (git cat-file), so the manifest hashes are git's own. CPython's writestr()
rewrites external_attr 0 as 0600<<16 (DOS attribute byte stays 0), same as build_patched_zip.py.

Existing zips/manifests are never overwritten: the tool only reports whether it reproduces them (exit 1 on
DIFFERS). Run on Linux/WSL/CI; Windows Python rewrites '\\' in entry names.

usage: mirror_upstream_zip.py --git <unity-mcp clone> --tag v10.3.0 [--out ZIP] [--manifest FILE]"""
import argparse, hashlib, io, pathlib, subprocess, sys, zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
PKG = "com.coplaydev.unity-mcp"
REF_ZIP = ROOT / f"{PKG}-9.7.1.zip"
REF_SHA = "a3eee339e8af3a15cbd676a6f6dd78984eb4ce451b38196ea48ecc726cd81831"
REF_TIME = (1985, 10, 26, 17, 15, 0)  # date_time of all 611 entries of the 9.7.1 zip
SUBDIR = "MCPForUnity"  # the Unity package inside the upstream repo
STAMPS = {
    # tag: date_time for entries that differ from the 9.7.1 zip. Must be strictly EARLIER than the stamp of every
    # patched release built on the tag (build_patched_zip.py RELEASES; verify_listing.py checks both constants
    # and entries), because 10.3.100 replaces package.json and a changed entry must be newer than the base's.
    # Decision 2026-10-24: 10.3.100 is stamped 2026-10-23 (day before release, README convention), so the
    # baseline is one day earlier; stamping both 2026-10-23 cannot pass verify, and a later release stamp
    # (2026-10-24) would still be in the future at UTC-12 on build day. Immutable once the zip is committed.
    "v10.3.0": (2026, 10, 22, 0, 0, 0),
}


def sha(b):
    return hashlib.sha256(b).hexdigest()


def compress_archive_order(paths):
    """PowerShell Compress-Archive order for '/'-separated file paths: at the root, each directory (sorted by
    lower()) is walked first and the root files (sorted by lower()) come last; inside a directory its files
    (sorted by lower()) come before its subdirectories (sorted by lower()), recursively. Returns a list."""
    tree = {}
    for p in paths:
        d = tree
        parts = p.split("/")
        for part in parts[:-1]:
            d = d.setdefault(part, {})
        d[parts[-1]] = None

    def walk(d, prefix, root):
        files = [prefix + k for k in sorted((k for k, v in d.items() if v is None), key=str.lower)]
        out = [] if root else files
        for k in sorted((k for k, v in d.items() if v is not None), key=str.lower):
            out += walk(d[k], prefix + k + "/", False)
        return out + files if root else out

    return walk(tree, "", True)


def git_tree(git, tree):
    """[(path, blob sha1)] in `git ls-tree -r` order; every entry must be a regular file (mode 100644)."""
    out = subprocess.run(["git", "-C", git, "ls-tree", "-r", "-z", tree], capture_output=True, check=True).stdout
    rows = []
    for rec in out.split(b"\0"):
        if not rec:
            continue
        meta, path = rec.decode("utf-8").split("\t", 1)
        mode, typ, blob = meta.split()
        if (mode, typ) != ("100644", "blob"):
            sys.exit(f"{path}: unsupported mode/type {mode} {typ}")
        rows.append((path, blob))
    return rows


def git_blobs(git, blobs):
    """{sha1: bytes} via one `git cat-file --batch` (raw blob contents, no filters)."""
    p = subprocess.run(["git", "-C", git, "cat-file", "--batch"], input="\n".join(blobs).encode() + b"\n",
                       capture_output=True, check=True)
    fp, data = io.BytesIO(p.stdout), {}
    for b in blobs:
        hdr = fp.readline().split()
        if hdr[:2] != [b.encode(), b"blob"]:
            sys.exit(f"git cat-file: unexpected header {hdr} for {b}")
        data[b] = fp.read(int(hdr[2]))
        fp.read(1)
    return data


def build(rows, blobs, stamp, ref):
    """rows from git_tree, blobs from git_blobs, ref = ZipFile of the 9.7.1 mirror. Returns zip bytes."""
    by_path = dict(rows)
    ref_names = {n.replace("\\", "/"): n for n in ref.namelist()}
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as out:
        for path in compress_archive_order(by_path):
            data = blobs[by_path[path]]
            same = path in ref_names and ref.read(ref_names[path]) == data
            zi = zipfile.ZipInfo(path.replace("/", "\\"), REF_TIME if same else stamp)
            zi.compress_type, zi.create_system = zipfile.ZIP_DEFLATED, 0
            zi.create_version = zi.extract_version = 20
            zi.external_attr = zi.flag_bits = 0
            zi.extra = zi.comment = b""
            out.writestr(zi, data)
    return buf.getvalue()


def emit(path, built, label):
    """Write `built` to `path` unless it exists; report reproduced/DIFFERS. Returns True on DIFFERS."""
    if path.exists():
        same = path.read_bytes() == built
        print(path.name, "reproduced" if same else "DIFFERS from the committed file (not overwritten)", label)
        return not same
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(built)
    print(path.name, "written", label)
    return False


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--git", required=True, help="path of a CoplayDev/unity-mcp clone")
    ap.add_argument("--tag", required=True, choices=sorted(STAMPS), help="upstream tag, e.g. v10.3.0")
    ap.add_argument("--out", type=pathlib.Path, help=f"default: <repo>/{PKG}-<ver>.zip")
    ap.add_argument("--manifest", type=pathlib.Path, help="default: <repo>/tools/upstream/<tag>.manifest")
    a = ap.parse_args()
    ver = a.tag.lstrip("v")
    out_zip = a.out or ROOT / f"{PKG}-{ver}.zip"
    manifest = a.manifest or ROOT / "tools" / "upstream" / f"{a.tag}.manifest"
    if sha(REF_ZIP.read_bytes()) != REF_SHA:
        sys.exit("9.7.1 zip hash mismatch; refusing to build")
    rows = git_tree(a.git, f"{a.tag}:{SUBDIR}")
    blobs = git_blobs(a.git, sorted({b for _, b in rows}))
    for b, data in blobs.items():
        if hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest() != b:
            sys.exit(f"git cat-file returned bytes not matching blob {b}")
    built = build(rows, blobs, STAMPS[a.tag], zipfile.ZipFile(REF_ZIP))
    failed = emit(manifest, "".join(f"{p}\t{b}\n" for p, b in rows).encode("utf-8"), f"{len(rows)} files")
    failed |= emit(out_zip, built, sha(built))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
