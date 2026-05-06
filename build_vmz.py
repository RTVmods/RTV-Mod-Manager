#!/usr/bin/env python3
"""Package the Vostok Mod Manager as a .vmz (zip) for the game's mod loader.

Run from the repo root:
    python build_vmz.py

Output: dist/vostok-mod-manager.vmz containing:
    mod.txt
    mods/VostokModManager/...      (every file in this subtree)

Files outside that subtree (project.godot, icon.svg, this script, .git, etc.)
are dev-only and NOT included in the package.
"""

import os
import sys
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent
DIST_DIR = REPO_ROOT / "dist"
OUTPUT = DIST_DIR / "vostok-mod-manager.vmz"

# Whitelist of paths to include, relative to REPO_ROOT.
INCLUDE_FILES = ["mod.txt"]
INCLUDE_TREES = ["mods/VostokModManager"]


def collect_files() -> list[Path]:
    files: list[Path] = []
    for f in INCLUDE_FILES:
        p = REPO_ROOT / f
        if not p.is_file():
            sys.exit(f"build_vmz: required file missing: {f}")
        files.append(p)
    for tree in INCLUDE_TREES:
        root = REPO_ROOT / tree
        if not root.is_dir():
            sys.exit(f"build_vmz: required tree missing: {tree}")
        for p in sorted(root.rglob("*")):
            if p.is_file():
                files.append(p)
    return files


def main() -> int:
    DIST_DIR.mkdir(exist_ok=True)
    if OUTPUT.exists():
        OUTPUT.unlink()

    files = collect_files()
    with zipfile.ZipFile(OUTPUT, "w", zipfile.ZIP_DEFLATED) as z:
        for p in files:
            arcname = p.relative_to(REPO_ROOT).as_posix()
            z.write(p, arcname)

    size_kb = OUTPUT.stat().st_size / 1024
    print(f"Built {OUTPUT.relative_to(REPO_ROOT)}  ({len(files)} files, {size_kb:.1f} KB)")
    print()
    print("Install:")
    print(f"  copy  {OUTPUT}")
    print('  to    "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Road to Vostok\\mods\\"')
    print()
    print("Then launch the game and press F8 to open the manager.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
