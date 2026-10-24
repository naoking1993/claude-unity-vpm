#!/usr/bin/env python3
"""Build io.github.naoking1993.avatar-checker-<ver>.zip from packages/io.github.naoking1993.avatar-checker/.

The zip is reproducible: entries are sorted, stored uncompressed (so the bytes do not depend on the
zlib build), and every entry gets the release's fixed `stamp` as its date_time. Like the MCP for Unity
builds, the stamp is the day before the release date at 00:00:00 so it is never in the future in any
time zone, and each new version needs a newer stamp so Unity recompiles the changed .cs files.

Every file and folder in the package must have a .meta with a stable GUID. `--write-meta` creates
missing ones (GUID derived from the package name and path, so it never changes). Existing zips are
never overwritten: the build only reports whether it reproduces the committed bytes."""
import hashlib, io, json, pathlib, sys, zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
NAME = "io.github.naoking1993.avatar-checker"
SRC = ROOT / "packages" / NAME
RELEASES = {
    # version: date_time for every entry
    "1.0.0": (2026, 10, 23, 0, 0, 0),
}

IMPORTERS = {
    ".cs": "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n",
    ".asmdef": "AssemblyDefinitionImporter:\n  externalObjects: {}\n",
    ".json": "TextScriptImporter:\n  externalObjects: {}\n",
    ".md": "TextScriptImporter:\n  externalObjects: {}\n",
}
FOLDER_OR_OTHER = "DefaultImporter:\n  externalObjects: {}\n"


def sha(b):
    return hashlib.sha256(b).hexdigest()


def guid(rel):
    return hashlib.md5(f"{NAME}/{rel}".encode("utf-8")).hexdigest()


def meta_text(rel, is_dir):
    head = f"fileFormatVersion: 2\nguid: {guid(rel)}\n"
    if is_dir:
        head += "folderAsset: yes\n"
    body = FOLDER_OR_OTHER if is_dir else IMPORTERS.get(pathlib.PurePosixPath(rel).suffix, FOLDER_OR_OTHER)
    return head + body + "  userData: \n  assetBundleName: \n  assetBundleVariant: \n"


def assets():
    """(relative posix path, is_dir) for every asset that needs a .meta (the package root itself does not)."""
    out = []
    for p in sorted(SRC.rglob("*")):
        if p.suffix == ".meta":
            continue
        out.append((p.relative_to(SRC).as_posix(), p.is_dir()))
    return out


def check_meta(write):
    problems = []
    want = set()
    for rel, is_dir in assets():
        m = SRC / (rel + ".meta")
        want.add(m)
        if not m.exists():
            if write:
                m.write_text(meta_text(rel, is_dir), encoding="utf-8", newline="\n")
                print("wrote", m.relative_to(ROOT))
            else:
                problems.append(f"missing .meta for {rel}")
    for m in SRC.rglob("*.meta"):
        if m not in want:
            problems.append(f"orphan .meta {m.relative_to(SRC).as_posix()}")
    guids = {}
    for m in sorted(want):
        if not m.exists():
            continue
        g = next((l.split(":", 1)[1].strip() for l in m.read_text(encoding="utf-8").splitlines() if l.startswith("guid:")), None)
        if not g or len(g) != 32:
            problems.append(f"bad guid in {m.name}")
        elif g in guids:
            problems.append(f"duplicate guid {g}: {guids[g]} and {m.name}")
        else:
            guids[g] = m.name
    return problems


def build(stamp):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_STORED) as z:
        for p in sorted(x for x in SRC.rglob("*") if x.is_file()):
            zi = zipfile.ZipInfo(p.relative_to(SRC).as_posix(), date_time=stamp)
            zi.external_attr = 0o644 << 16
            z.writestr(zi, p.read_bytes())
    return buf.getvalue()


def main():
    problems = check_meta("--write-meta" in sys.argv)
    for p in problems:
        print("FAIL:", p)
    if problems:
        sys.exit(1)
    version = json.loads((SRC / "package.json").read_text(encoding="utf-8"))["version"]
    if version not in RELEASES:
        sys.exit(f"package.json version {version} is not in RELEASES")
    failed = False
    for ver, stamp in RELEASES.items():
        out = ROOT / f"{NAME}-{ver}.zip"
        if ver != version:
            continue  # older releases: the source has moved on; their zips are frozen
        data = build(stamp)
        if out.exists():
            same = out.read_bytes() == data
            print(f"{ver}: {'reproduced' if same else 'DIFFERS from committed zip (bump the version)'} {sha(out.read_bytes())}")
            failed |= not same
        else:
            out.write_bytes(data)
            print(f"{ver}: wrote {out.name} {sha(data)}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
