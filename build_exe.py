#!/usr/bin/env python3
"""Build a self-contained Vostok Mod Manager .exe via Godot's headless
export.

Usage:
    python build_exe.py

Output:
    dist/VostokModManager.exe   — single file, ~70 MB, no external runtime

Prerequisites:
    1. Godot 4.6 editor installed.
       Easiest:  winget install GodotEngine.GodotEngine
    2. Windows export templates installed (one-time):
       Open Godot, Editor menu → Manage Export Templates → Download.
       This grabs the binary templates the export tooling needs to splice
       our .pck into a runnable .exe.

If `godot` isn't on PATH, set GODOT_BIN to the full path of your
godot.exe; otherwise the script looks in winget / scoop / Program Files
locations.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent
DIST_DIR = REPO_ROOT / "dist"
OUTPUT = DIST_DIR / "VostokModManager.exe"
PRESET_NAME = "Windows Desktop"


def find_godot() -> str | None:
    # 1. Explicit override.
    env = os.environ.get("GODOT_BIN", "").strip()
    if env and Path(env).is_file():
        return env

    # 2. PATH (winget puts a `godot` shim that some installs expose).
    for name in ("godot", "Godot_v4.6-stable_win64", "Godot", "godot4"):
        p = shutil.which(name)
        if p:
            return p

    # 3. Common Windows install locations.
    home = Path.home()
    candidates: list[Path] = [
        home / "AppData/Local/Programs/Godot/Godot_v4.6-stable_win64.exe",
        home / "scoop/apps/godot/current/godot.exe",
        Path("C:/Program Files/Godot/Godot_v4.6-stable_win64.exe"),
        Path("C:/Program Files (x86)/Godot/Godot_v4.6-stable_win64.exe"),
    ]
    # winget tucks Godot into a deep packages dir; glob for any version.
    winget_root = home / "AppData/Local/Microsoft/WinGet/Packages"
    if winget_root.is_dir():
        for godot_pkg in winget_root.glob("GodotEngine.GodotEngine*"):
            for exe in godot_pkg.glob("Godot_v*-stable_win64.exe"):
                candidates.append(exe)
    for c in candidates:
        if c.is_file():
            return str(c)
    return None


def main() -> int:
    godot = find_godot()
    if godot is None:
        print(
            "build_exe: could not locate Godot 4.6.\n"
            "  Install via:  winget install GodotEngine.GodotEngine\n"
            "  Or set GODOT_BIN=<path to godot.exe>",
            file=sys.stderr,
        )
        return 1
    print(f"Using Godot: {godot}")

    DIST_DIR.mkdir(exist_ok=True)
    if OUTPUT.exists():
        OUTPUT.unlink()

    # --headless: no display required (works on a server / CI runner).
    # --export-release: use release templates (smaller, no debug symbols).
    cmd = [godot, "--headless", "--export-release", PRESET_NAME, str(OUTPUT)]
    print("Running:", " ".join(repr(x) if " " in x else x for x in cmd))
    result = subprocess.run(cmd, cwd=REPO_ROOT)
    if result.returncode != 0:
        print(
            f"build_exe: export failed (exit {result.returncode}). "
            "Most common cause: Windows export templates aren't installed. "
            "Open Godot → Editor menu → Manage Export Templates → Download.",
            file=sys.stderr,
        )
        return result.returncode
    if not OUTPUT.exists():
        print(
            "build_exe: export reported success but output is missing.",
            file=sys.stderr,
        )
        return 2

    size_mb = OUTPUT.stat().st_size / (1024 * 1024)
    print(f"\nBuilt {OUTPUT.relative_to(REPO_ROOT)}  ({size_mb:.1f} MB)")
    print(
        "\nDistribute by copying that single .exe — Windows users don't "
        "need Godot or any runtime installed."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
