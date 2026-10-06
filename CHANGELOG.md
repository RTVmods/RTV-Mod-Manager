# Changelog

Builds are published on [GitHub Releases](https://github.com/RTVmods/RTV-Mod-Manager/releases) and on [VostokMods](https://vostokmods.net/mod/rtv-mod-manager). The manager updates itself from GitHub.

## 0.6.8 (2026-10-07)

- Launch Game skips the mod loader's launcher window when the installed loader supports it (Metro Mod Loader 3.4.2+): the manager writes the loader's `modloader_skip_ui_once` marker beside the game before the Steam launch, and the active profile loads straight away. A Settings toggle turns this off.

## 0.6.7 (2026-10-05)

- The background watermark rises into the top-right space, and the grids' column headers let it through.

## 0.6.6 (2026-10-05)

- The mods list and the conflicts panel are slightly translucent, so the background decorations show through them.

## 0.6.5 (2026-10-05)

- The active profile follows the live version of each mod, however it got there (manager update, in-game loader update, a file dropped into the mods folder), so switching away and back no longer reinstalls an older version. The old version's enabled/priority/source state is carried to the new one.

## 0.6.4 (2026-10-05)

- Pack entries are matched to installed mods by name when the installed mod has no VostokMods link, so an import no longer downloads a second copy of a mod you already have; the match links the mod.
- A profile's known sources are written back to installed mods that have none.

## 0.6.3 (2026-10-05)

- Every import (VostokMods modpack, modpack zip, mod list) asks where it should go: the active profile, a new profile named after the pack, or another saved profile. The manager switches to that profile first.

## 0.6.2 (2026-10-05)

- Mod Packager → Build modpack… writes the mod set as a modpack `.zip` in the mod loader's format: `profile.json` naming each mod by its VostokMods source and pinned version.
- Modpack zips install from the manager: drop one on the window or pick it under Install mod….

## 0.6.1 (2026-10-05)

- Modpacks button: browse the modpacks published on VostokMods and install one into the active profile at the versions it lists.
- The embedded browser installs a whole modpack from a modpack page.

## 0.6.0 (2026-10-01)

- Road to Vostok mods moved from ModWorkshop to VostokMods; the manager is rebuilt around vostokmods.net (updates, downloads, descriptions, embedded browser, dependencies, list import, profile apply). ModWorkshop support is removed.
- Mods are tracked by their VostokMods page: linked automatically when downloaded through the manager, by name match from the Updates row, or by hand with Link to VostokMods….
- `mod_config.cfg` is edited in place: only the manager's own entries change and everything the loader wrote is kept byte for byte (MML 3.4.1 compatible).
- Profiles use the mod loader's modpack format (metroprofile v1); a profile can be exported as a modpack for the loader's Modpacks tab. Saved profiles are converted on first start, with the originals backed up.
- The manager updates itself from GitHub Releases.
- Log analysis opens the newest log the loader ran in and explains when hook declarations were not logged (MML 3.4+ logs them only in Developer Mode).
