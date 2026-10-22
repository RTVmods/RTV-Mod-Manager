# Changelog

Builds are published on [GitHub Releases](https://github.com/RTVmods/RTV-Mod-Manager/releases) and on [VostokMods](https://vostokmods.net/mod/rtv-mod-manager). The manager updates itself from GitHub.

## 0.7.0 (2026-10-22)

- **A new interface, drawn the way Road to Vostok draws its own.** The manager is rebuilt in WPF: Lora (the game's typeface), flat grey panels, hairlines and a white selection, with colour only where it means something (amber for updates, salmon for conflicts, blue for testing builds, green for OK). The faux-Cyrillic title, the backdrop and the eight-button title row are gone.
- **A side menu** with Mods, Profiles, Saves, Browse VostokMods, Library, Tools and Settings, and a **launch panel** under it: the profile about to run, the save slot it will continue, a pre-flight check (load order, missing dependencies, updates waiting, conflicts, save slots) and the LAUNCH button. The eight status lines are now one row of chips you can click.
- **Mods**: filter tabs with counts, drag rows to reorder, Ctrl/Shift multi-select, and a details panel that replaces the right-click menu: update (and update all), revert to an earlier version, link to VostokMods, the mod's VostokMods description and changelog, its dependencies, the conflicts it is in with who wins and why, testing / lock flags and a note. The separate Conflicts grid is gone; conflicts show on the mods they involve.
- **Profiles**: every profile as a card, and a table of where they differ across all profiles with a fix per row; apply, clone, import, export, rename and describe from the card.
- **Saves**: each slot as a card with where and when it was played, the profile it is tied to, and whether every mod that profile needs is here.
- **Browse VostokMods**: the site's catalogue inside the manager, with each card showing its state against your setup, **Install into** ticks for every profile, modpacks with **Install the missing mods**, and the real site in a Website tab.
- **Setup**: on first run a setup window walks through five checks (game folder, Mod Loader, mods and profiles, save slots, VostokMods links); Settings → Run setup again reopens it.
- **Tools**: log analysis, the Mod Packager and the support package in one place. In the AI edition, **Analyze with Claude** reads the game's log and proposes a load order.
- Only one manager runs at a time, the window remembers its size and position, and an unexpected error is written to `crash.log` instead of closing the manager.

## 0.6.14 (2026-10-19)

- **Built-in save slots.** The manager now carries its own Save Slots mod (based on Razzer's Multi Save Slots 1.4) and installs it into the mods folder itself: five slots in the game's menu, so New Game no longer wipes your run and Load picks the slot to continue. It is on in every profile, kept through profile switches, updated with the manager, shown as ⚙ built in, and left out of profiles, exports and modpacks. Settings → Built-in save slots turns it off and removes it. Existing slots from Multi Save Slots or Multi Save Slots Reborn carry over; on first start the manager offers to turn those mods off in the profiles that use them, since both would manage the same slot folder.
- **Each slot can be tied to a profile**, so a save always runs with the mods it was made with. In the game, loading a slot tied to another profile restarts the game into that profile and continues the slot.
- **Saves** (title row) lists the slots with name, last map, last played time and state, with a profile picker per slot. **Launch this slot** switches to the slot's profile and starts the game straight into the slot (offering first to turn off another save-slot mod in that profile). **New game** starts a fresh run in the selected slot, tied to the live profile unless the slot is already tied to another: the game opens on the mode screen and Enter starts the run there (an existing save in the slot is zipped first and replaced only when the run begins). **Delete** removes a slot after zipping it to the backups folder; for the save in play it also clears its files from the game's save folder, so it can't come back. **Rename** and **Back up slots** (a zip of the whole slot folder) are there too. Deleting the save in play from the game's own slot panel now clears it the same way.
- When the game switches profile for a slot, the manager follows on its next scan instead of recording the new profile's mods into the old one. It only follows when the mods folder really holds that profile's mods, so a profile picked in the mod loader's own launcher, which moves no files, is left alone.

## 0.6.13 (2026-10-18)

- Updating a mod shows its download as a progress band flowing along that mod's row in the mods list, with the percentage in the Update column and bytes received in the status line. While the file size is not yet known the band slides along the row instead.

## 0.6.12 (2026-10-18)

- First run: adopting the profile model now also offers the mod loader's other profiles for import. Each ticked profile becomes a manager profile with the enabled state and load order recorded in `mod_config.cfg`; a mod the loader lists at several versions is kept once, at the version that is installed or in the Library, else the newest. Mods that are not on disk stay in the profile and show as missing until installed or downloaded. The cfg and the mods folder are not changed by the import.

## 0.6.11 (2026-10-17)

- Library: a search box filters the list by name, id, version or VostokMods source, and **Clean up old versions** deletes every archived version that is not the newest of its mod, not live, and not pinned by a saved profile (confirmed with the list and the space freed first). Multi-select and delete were already there.
- Two mods that share a mod id but are not the same mod (an abandoned mod and its successor, such as "Multi Save Slots" 1.4 and "Multi Save Slots Reborn" 1.1.0) are no longer treated as one lineage: the profile-switch prompt, the clean-up and the Library's live-state column tell them apart by VostokMods source, or by name when a source is missing, so an old mod's higher version number is not offered as an "update" to its successor.

## 0.6.10 (2026-10-17)

- Switching to a profile that pins a mod to a version older than the newest one in the Library (because another profile updated it) now asks which of those mods should move to the newest version before the switch. Unticked mods keep the profile's own version, so profiles no longer drift apart after an update but can still run a mod at an older version on purpose. DEV builds are listed but unticked by default; cancelling the dialog cancels the switch.

## 0.6.9 (2026-10-11)

- The red stars are gone: the title ornament, the MOD CATALOG divider, the watermark and the dialog headers carry the Hannunvaakuna (⌘) emblem used across the mod catalogue's art. The page banner and thumbnail get the same corner emblem.

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
