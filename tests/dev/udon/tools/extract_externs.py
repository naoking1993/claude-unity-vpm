#!/usr/bin/env python3
"""Extract the Udon extern signature table ("Module.__method__params__ret") from the SDK wrapper DLLs.

Each wrapper module class (Extern<Name> : IUdonWrapperModule) returns its module name from get_Name and
registers its functions via string literals "__name__..." in its constructor/lazy dictionaries. We disassemble
with monodis and collect, per top-level class, the get_Name literal and every "__"-prefixed literal.
Usage: extract_externs.py out.txt wrapper1.dll [wrapper2.dll ...]
"""
import re, subprocess, sys

def classes(il_text):
    cur = None
    for line in il_text.splitlines():
        m = re.match(r'^  \.class (?!nested).*? (\S+)\s*$', line)
        if m:
            if cur: yield cur
            cur = {"name": m.group(1), "module": None, "sigs": [], "is_module": False, "pending_name": False}
            continue
        if cur is None: continue
        if "IUdonWrapperModule" in line and line.strip().startswith(("implements", "extends")) or "implements [VRC.Udon.Common]VRC.Udon.Common.Interfaces.IUdonWrapperModule" in line:
            cur["is_module"] = True
        if "instance default string get_Name ()" in line:
            cur["pending_name"] = True
        m = re.search(r'ldstr "(.*)"$', line)
        if m:
            s = m.group(1)
            if cur["pending_name"] and cur["module"] is None:
                cur["module"] = s; cur["pending_name"] = False
            elif s.startswith("__"):
                cur["sigs"].append(s)
    if cur: yield cur

def main():
    out, dlls = sys.argv[1], sys.argv[2:]
    table = set(); modules = 0
    for dll in dlls:
        il = subprocess.run(["monodis", dll], capture_output=True, text=True, errors="replace").stdout
        for c in classes(il):
            if not c["is_module"] or not c["module"]: continue
            modules += 1
            for s in c["sigs"]:
                table.add(c["module"] + "." + s)
    with open(out, "w", encoding="utf-8") as f:
        for s in sorted(table): f.write(s + "\n")
    print(f"externs: {len(table)} signatures from {modules} modules -> {out}")

if __name__ == "__main__":
    main()
