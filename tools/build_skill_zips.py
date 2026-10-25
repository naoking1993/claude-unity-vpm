#!/usr/bin/env python3
"""Package each Claude skill under claude-skills/<name>/ as claude-skills/dist/<name>.zip for upload.

Layout inside each zip is <name>/SKILL.md (+ references/ ...), which is what
claude.ai "Customize > Skills > + > Upload skill" expects. Checks before packaging:
frontmatter has only `name` and `description`, name == folder name (kebab-case), description
<= 1024 characters without '<' or '>', and every relative link in the .md files resolves.
Writes claude-skills/dist/manifest.json with SHA256 of every zip. Zips are reproducible
(sorted entries, fixed timestamp, stored).

  python3 tools/build_skill_zips.py
"""
import hashlib, io, json, pathlib, re, sys, zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
SKILLS = ROOT / "claude-skills"
DIST = SKILLS / "dist"
STAMP = (2026, 10, 25, 0, 0, 0)
LINK = re.compile(r"\]\(([^)#\s]+)(?:#[^)]*)?\)")


def frontmatter(text):
    if not text.startswith("---\n"):
        return None, "SKILL.md must start with '---' frontmatter"
    end = text.find("\n---", 4)
    if end < 0:
        return None, "unterminated frontmatter"
    fm, key = {}, None
    for line in text[4:end].splitlines():
        m = re.match(r"^([A-Za-z_][\w-]*):\s?(.*)$", line)
        if m:
            key = m.group(1)
            fm[key] = m.group(2).strip()
        elif key and line.startswith((" ", "\t")):
            fm[key] = (fm[key] + " " + line.strip()).strip()
    for k, v in fm.items():
        if len(v) >= 2 and v[0] == v[-1] and v[0] in "\"'":
            fm[k] = v[1:-1]
        if v in (">-", "|", ">", "|-"):
            fm[k] = fm[k][len(v):].strip()
    return fm, None


def check(skill_dir):
    problems = []
    text = (skill_dir / "SKILL.md").read_text(encoding="utf-8")
    fm, err = frontmatter(text)
    if err:
        return [err]
    if set(fm) != {"name", "description"}:
        problems.append(f"frontmatter keys must be name/description only, got {sorted(fm)}")
    if fm.get("name") != skill_dir.name or not re.fullmatch(r"[a-z0-9]+(-[a-z0-9]+)*", fm.get("name", "")):
        problems.append(f"name '{fm.get('name')}' must equal the kebab-case folder name '{skill_dir.name}'")
    desc = fm.get("description", "")
    if not desc or len(desc) > 1024:
        problems.append(f"description length {len(desc)} (must be 1..1024)")
    if "<" in desc or ">" in desc:
        problems.append("description must not contain '<' or '>'")
    for md in skill_dir.rglob("*.md"):
        for target in LINK.findall(md.read_text(encoding="utf-8")):
            if re.match(r"^[a-z]+:", target):
                continue  # URL
            if md.parent == skill_dir and re.fullmatch(r"\.\./[a-z0-9-]+/SKILL\.md", target):
                continue  # link to another skill of the account (may not be in this repo)
            if not (md.parent / target).resolve().exists():
                problems.append(f"{md.relative_to(SKILLS)}: broken link {target}")
    return problems


def build(skill_dir):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_STORED) as z:
        for p in sorted(skill_dir.rglob("*"), key=lambda x: x.relative_to(skill_dir).as_posix()):
            rel = p.relative_to(skill_dir)
            if p.is_dir() or any(part.startswith(".") for part in rel.parts):
                continue
            zi = zipfile.ZipInfo(f"{skill_dir.name}/{rel.as_posix()}", date_time=STAMP)
            zi.external_attr = 0o100644 << 16
            zi.create_system = 3
            z.writestr(zi, p.read_bytes())
    return buf.getvalue()


def main():
    DIST.mkdir(exist_ok=True)
    manifest, failed = {}, False
    for skill_dir in sorted(p for p in SKILLS.iterdir() if p.is_dir() and p.name != "dist" and (p / "SKILL.md").exists()):
        problems = check(skill_dir)
        for pr in problems:
            print(f"PROBLEM {skill_dir.name}: {pr}")
        failed |= bool(problems)
        data = build(skill_dir)
        (DIST / f"{skill_dir.name}.zip").write_bytes(data)
        manifest[f"{skill_dir.name}.zip"] = {"sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
        print(f"{skill_dir.name}.zip", len(data), "bytes", "(with problems)" if problems else "ok")
    (DIST / "manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
