#!/usr/bin/env python3
"""Build <name>-<version>.zip for packages that live as source under packages/<name>/.

The zip is reproducible: entries are sorted, use '/' separators, have mode 0644, are stored without
compression (deflate output depends on the zlib build, so CI could not reproduce the bytes) and all
carry the release's fixed `stamp` as date_time. A new version always gets a new stamp, so a file whose content
changes between versions also changes its zip time; the official VCC writes that time to the extracted
file and Unity's script compilation only notices a changed .cs by its modification time (see
build_patched_zip.py). Use the day before the release date at 00:00:00 so the stamp is never in the
future in any time zone.

Published zips are immutable: an existing zip is never overwritten, only compared.

  python3 tools/build_vpm_package.py                 # build missing zips / verify existing ones
  python3 tools/build_vpm_package.py --update-index  # also write the listing entry of each built zip
                                                     # into index.json (fields copied from package.json)
"""
import hashlib, io, json, pathlib, sys, zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
PACKAGES = {
    "io.github.naoking1993.claude-creator-tools": {
        # version: stamp (zip date_time for every entry)
        "0.1.0": (2026, 10, 23, 0, 0, 0),
    },
}


def sha(b):
    return hashlib.sha256(b).hexdigest()


def package_files(src):
    for p in sorted(src.rglob("*"), key=lambda x: x.relative_to(src).as_posix()):
        rel = p.relative_to(src)
        if p.is_dir() or any(part.startswith(".") for part in rel.parts):
            continue
        yield rel.as_posix(), p.read_bytes()


def build(name, version, stamp):
    src = ROOT / "packages" / name
    pj = json.loads((src / "package.json").read_text(encoding="utf-8"))
    if pj["name"] != name or pj["version"] != version:
        sys.exit(f"{src}/package.json is {pj['name']} {pj['version']}, expected {name} {version}")
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_STORED) as z:
        for rel, data in package_files(src):
            zi = zipfile.ZipInfo(rel, date_time=stamp)
            zi.compress_type = zipfile.ZIP_STORED
            zi.external_attr = 0o100644 << 16
            zi.create_system = 3  # unix, so external_attr permissions are honoured identically everywhere
            z.writestr(zi, data)
    return buf.getvalue()


LISTING_KEYS = ("name", "displayName", "version", "unity", "description", "author", "license",
                "dependencies", "vpmDependencies", "samples")
BASE_URL = "https://naoking1993.github.io/claude-unity-vpm/"


def listing_entry(name, version, zip_bytes):
    pj = json.loads(zipfile.ZipFile(io.BytesIO(zip_bytes)).read("package.json").decode("utf-8"))
    entry = {k: pj[k] for k in LISTING_KEYS if k in pj}
    entry["url"] = f"{BASE_URL}{name}-{version}.zip"
    entry["zipSHA256"] = sha(zip_bytes)
    return entry


def update_index(name, version, zip_bytes):
    path = ROOT / "index.json"
    idx = json.loads(path.read_text(encoding="utf-8"))
    versions = idx["packages"].setdefault(name, {"versions": {}})["versions"]
    new = {version: listing_entry(name, version, zip_bytes)}
    new.update({v: e for v, e in versions.items() if v != version})  # newest first, like the existing listing
    idx["packages"][name]["versions"] = new
    path.write_text(json.dumps(idx, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("index.json updated:", name, version)


def main():
    failed = False
    want_index = "--update-index" in sys.argv[1:]
    for name, releases in PACKAGES.items():
        src_version = json.loads((ROOT / "packages" / name / "package.json").read_text(encoding="utf-8"))["version"]
        for version, stamp in releases.items():
            out = ROOT / f"{name}-{version}.zip"
            if version != src_version:
                # only the source's current version can be rebuilt; older zips are kept as published
                print(out.name, "kept (source is now", src_version + ")")
                continue
            built = build(name, version, stamp)
            if out.exists():
                same = out.read_bytes() == built
                print(out.name, "reproduced" if same else "DIFFERS from the committed zip (not overwritten)", sha(built))
                failed |= not same
            else:
                out.write_bytes(built)
                print(out.name, "written", sha(built))
            if want_index and out.read_bytes() == built:
                update_index(name, version, built)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
