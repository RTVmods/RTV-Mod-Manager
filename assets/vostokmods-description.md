**RTV Mod Manager** is a desktop app that takes the work out of running a modded Road to Vostok: find mods and modpacks on VostokMods, install them in one click, keep them updated, know which ones fight each other before you launch, and keep a save slot for every setup.

It is a program, not a mod: unzip it anywhere and run the `.exe`. Do not put it in the game's `mods` folder.

Built for Road to Vostok Build 2 and Metro Mod Loader 3.4 or later.

![The Mods page: every mod with its load order, version and status, and the selected mod's details on the right](https://files.vostokmods.net/mod/01a0fe78-1c5d-70a0-8aff-65fd9555670b/screenshots/1792666269258-01-mods.png)

## Why use it

**One place for everything, before the game starts.** Your mods, their versions, load order, updates, conflicts, profiles and saves, in one window drawn like the game's own menus. It writes straight to the mod loader's `mod_config.cfg`, and launching from the manager skips the loader's own window, so you go straight into the game.

**A check before every launch.** The launch panel shows the profile about to run, the save it will continue, and anything wrong: missing dependencies, updates waiting, known conflicts. **CONTINUE** resumes your last save; the arrow beside it has **Launch game** for a plain start.

**Save slots tied to your mods.** Five save slots are built in, based on Razzer's Multi Save Slots: New Game no longer wipes your run. Tie a slot to a profile and it always runs with the mods it was made with; continuing it switches to that profile first, from the manager or from the game's own menu.

![The Saves page: each slot with where and when it was played and the profile it runs under](https://files.vostokmods.net/mod/01a0fe78-1c5d-70a0-8aff-65fd9555670b/screenshots/1792666273222-03-saves.png)

**Browse VostokMods without leaving the app.** The site's catalogue is built in: each mod shows whether you have it, need an update, or have it in another profile, and **Install into** puts it in one or several profiles at once. Modpacks install at the exact versions the pack lists, checked against its checksums, with the full plan shown before anything downloads. The real site is in the Website tab, and downloads made there install too.

![Browse: the VostokMods catalogue and modpacks inside the manager](https://files.vostokmods.net/mod/01a0fe78-1c5d-70a0-8aff-65fd9555670b/screenshots/1792666274676-04-browse.png)

**Updates you can see.** Every mod linked to VostokMods shows when a new version is out, and **Update all** fetches them with a progress bar on each row. The old version is backed up first, and any mod can go back to an earlier version from its details. Mods you already had are linked to their pages by name in one click.

**Conflicts before they bite.** Every archive is read the way the mod loader will read it: two mods shipping the same file, hooking the same function, registering the same autoload or declaring the same `class_name`, a missing or disabled dependency, and more. Each conflict is tiered by how serious it is and shows on the mods involved, with which one wins under the current order and why.

**Profiles for different ways to play.** A profile is a full loadout: which mods, which versions, on or off, in what order. Switch, clone and share them; a profile can carry the mod files themselves, so a friend gets your exact setup. One table shows where your profiles differ, with a fix per row, and an exported profile also works as a mod loader modpack.

![Profiles: every profile as a card, and a table of where they differ](https://files.vostokmods.net/mod/01a0fe78-1c5d-70a0-8aff-65fd9555670b/screenshots/1792666271795-02-profiles.png)

**A library of every version.** Every mod file you ever installed is kept, so switching profiles or rolling back never needs a download, and a profile can stay on an older version on purpose. The Library shows which versions are live and which profiles use them, and **Clean up old versions** frees the space.

![The Library: every version of every mod you have had, and where it is used](https://files.vostokmods.net/mod/01a0fe78-1c5d-70a0-8aff-65fd9555670b/screenshots/1792666277792-05-library.png)

**Dependencies handled on install.** When a mod needs others you don't have, the manager finds them in your library or downloads them from VostokMods, dependencies of dependencies included.

**Undo a bad launch.** The manager checkpoints your setup each time you launch the game. If a change broke something, **Undo last change** puts the previous setup back.

**For mod authors and bug reports.** Tools reads the game's log and lists which mods replaced the same scripts, plus every error and warning. The Mod Packager turns a folder into a `.vmz` the loader accepts, with the manifest, dependencies and VostokMods source filled in, and builds modpacks in the loader's own format. The support package bundles your mod list, `mod_config.cfg` and the logs into one file.

![Tools: game-log analysis, the Mod Packager and the support package](https://files.vostokmods.net/mod/01a0fe78-1c5d-70a0-8aff-65fd9555670b/screenshots/1792666279238-06-tools.png)

## Install

1. Download and unzip.
2. Run `VostokModManagerIntegrated.exe`. There is no installer and nothing else to download.
3. On first run a setup window checks the game folder, the Mod Loader, your mods and profiles, save slots and VostokMods links. If the manager does not find your game, set the mods folder there or under Settings.

Requires Windows 10 or 11 (64-bit).

## Good to know

- **Network use.** The manager contacts vostokmods.net (mod listings, update checks, downloads) and GitHub (update checks and downloads for the mod loader and for the manager itself). It sends nothing about you or your mods anywhere.
- **Self-update.** The manager updates itself from its GitHub releases, so the copy you download here will offer newer builds as they come out.
- **Built-in save slots.** The manager installs its Save Slots mod into the mods folder and keeps it on in every profile. Your slots from Multi Save Slots or Multi Save Slots Reborn carry over; those two mods are retired while the built-in slots are on. Settings → Built-in save slots turns the feature off.
- **Modpack settings.** A modpack's bundled mod settings (MCM) are not applied by the manager; apply the pack from the loader's in-game Modpacks tab if you want those too.
- **AI disclosure.** This program was written with the help of generative AI (Claude).
- **AI edition.** A second build adds conflict resolution and log diagnosis through a local Claude Code CLI. It is on GitHub; the download on this page is the standard edition, which needs nothing extra.

## Links

- Source code, issues and all releases: [https://github.com/RTVmods/RTV-Mod-Manager](https://github.com/RTVmods/RTV-Mod-Manager)
- License: GNU GPL v3.0

---

## My mods in one pack

All my Road to Vostok mods are also published as one pack: **[Joni's "REDACTED" Modpack](https://vostokmods.net/modpack/jonis-redacted-modpack)**. The manager's Browse page installs it at the versions the pack lists.
