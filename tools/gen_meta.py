#!/usr/bin/env python3
"""Create missing Unity .meta files for a package under packages/ and check meta consistency.

GUIDs are deterministic (md5 of "<package name>/<relative path>"), so re-running never changes an
existing GUID and a deleted .meta comes back with the same GUID. Existing .meta files are never
overwritten. Folders ending in '~' (e.g. Samples~) are ignored by Unity, so the folder itself gets no
.meta, but files inside it do: Package Manager copies them on sample import and the GUIDs stay stable.

  python3 tools/gen_meta.py packages/<name>          # create missing metas, then check
  python3 tools/gen_meta.py --check packages/<name>  # check only (exit 1 on problems)
"""
import hashlib, json, pathlib, sys

MONO = """fileFormatVersion: 2
guid: {guid}
MonoImporter:
  externalObjects: {{}}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {{instanceID: 0}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""
SIMPLE = """fileFormatVersion: 2
guid: {guid}
{importer}:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""
FOLDER = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def importer_for(path):
    name, ext = path.name, path.suffix.lower()
    if ext == ".cs":
        return None  # MonoImporter template
    if name == "package.json":
        return "PackageManifestImporter"
    if ext == ".asmdef":
        return "AssemblyDefinitionImporter"
    if ext in (".md", ".txt", ".json", ".xml", ".bytes", ".csv"):
        return "TextScriptImporter"
    return "DefaultImporter"


def meta_text(path, guid):
    if path.is_dir():
        return FOLDER.format(guid=guid)
    imp = importer_for(path)
    return MONO.format(guid=guid) if imp is None else SIMPLE.format(guid=guid, importer=imp)


def ignored(rel_parts):
    # hidden files/folders are ignored by Unity
    return any(p.startswith(".") for p in rel_parts)


def assets(root):
    """Every file/folder that Unity (or a sample import) treats as an asset needing a .meta."""
    for p in sorted(root.rglob("*")):
        rel = p.relative_to(root)
        if ignored(rel.parts) or p.suffix == ".meta":
            continue
        if p.is_dir() and p.name.endswith("~"):
            continue  # the ~ folder itself is not imported
        yield p


def guid_of(meta_path):
    for line in meta_path.read_text(encoding="utf-8").splitlines():
        if line.startswith("guid: "):
            return line[6:].strip()
    return None


def main(argv):
    check_only = "--check" in argv
    args = [a for a in argv if not a.startswith("--")]
    if len(args) != 1:
        sys.exit(__doc__)
    root = pathlib.Path(args[0]).resolve()
    pkg_name = json.loads((root / "package.json").read_text(encoding="utf-8"))["name"]
    created, problems, guids = [], [], {}
    wanted = set()
    for p in assets(root):
        meta = p.with_name(p.name + ".meta")
        wanted.add(meta)
        if not meta.exists():
            if check_only:
                problems.append(f"missing meta: {meta.relative_to(root)}")
                continue
            rel = p.relative_to(root).as_posix()
            guid = hashlib.md5(f"{pkg_name}/{rel}".encode("utf-8")).hexdigest()
            meta.write_text(meta_text(p, guid), encoding="utf-8", newline="\n")
            created.append(meta.relative_to(root).as_posix())
    for meta in sorted(root.rglob("*.meta")):
        rel = meta.relative_to(root)
        if ignored(rel.parts):
            continue
        if meta not in wanted:
            problems.append(f"orphan meta (no asset): {rel}")
            continue
        g = guid_of(meta)
        if not g or len(g) != 32 or any(c not in "0123456789abcdef" for c in g):
            problems.append(f"bad guid in {rel}: {g}")
        elif g in guids:
            problems.append(f"duplicate guid {g}: {rel} and {guids[g]}")
        else:
            guids[g] = rel
    for c in created:
        print("created", c)
    for pr in problems:
        print("PROBLEM:", pr)
    print(f"{len(guids)} metas, {len(created)} created, {len(problems)} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
