**RTV Mod Manager** is a desktop app that takes the work out of running a modded Road to Vostok: find mods and modpacks on VostokMods, install them in one click, keep them updated, and know which ones fight each other before you launch the game.

It is a program, not a mod: unzip it anywhere and run the `.exe`. Do not put it in the game's `mods` folder.

Built for Road to Vostok Build 2 and Metro Mod Loader 3.4 or later.

![The main window: your mods, their load order and update state, with conflicts on the right](https://files.vostokmods.net/mod/01a0fe78-1c5d-70a0-8aff-65fd9555670b/screenshots/1791145471717-01-main.png)

## Why use it

**One place for everything.** Your installed mods, their versions, load order, update state, conflicts and profiles, all in one window that writes straight to the mod loader's own `mod_config.cfg`. The game picks up every change on the next launch; no files to edit by hand.

**Install from VostokMods without leaving the app.** The built-in browser opens vostokmods.net. On a mod page, click **Install this mod** and it lands in your active profile, linked to its page so updates are tracked from then on. Downloading through the page's own button does the same.


**Modpacks, installed as the author intended.** The **Modpacks** button lists every pack published on VostokMods. Install one and every mod in it is downloaded at the exact version the pack lists, checked against the pack's checksums, and added to your profile in the pack's load order. On a modpack page in the built-in browser, the same button installs the whole pack.

![The modpack list, straight from VostokMods](https://files.vostokmods.net/mod/01a0fe78-1c5d-70a0-8aff-65fd9555670b/screenshots/1791145473260-02-modpacks.png)

**Updates you can see.** The Update column shows `⬆` when VostokMods has a newer version, `✓` when you're current, and `—` when the mod isn't linked yet. Click `⬆` to update in place; the old file is backed up first and can be restored from the mod's right-click menu. Mods you already had are linked by clicking the Updates row, which matches them to the site by name, or one at a time with **Link to VostokMods…**.

**Conflicts before they bite.** Every mod's manifest and files are compared, and the manager tells you when two mods overwrite the same file, hook the same function, register the same autoload, declare the same `class_name`, or when a dependency is missing or disabled. Each conflict is tiered by how serious it is, and double-clicking one shows exactly which hooks or files collide.

**Profiles for different ways to play.** Save your current loadout as a profile, switch between them, clone and diff them, and export one as a `.vmprofile` that carries the mod files themselves, so a friend can import your exact setup. Profiles use the mod loader's own modpack format, so an exported pack also works on the loader's Modpacks tab.


**A library of every version.** Every mod file you ever installed is kept in a library, so switching profiles or rolling back never needs a download. The Library window shows which versions are live, which profiles use them, and lets you install or delete any of them.

![The library: every version of every mod you have had, and where it is used](https://files.vostokmods.net/mod/01a0fe78-1c5d-70a0-8aff-65fd9555670b/screenshots/1790976464317-03-library.png)

**Dependencies handled on install.** When a mod declares dependencies you don't have, the manager finds them in your library or downloads them from VostokMods, and asks before doing anything.

**Read the game's log for you.** After a session, **Analyze Log** shows the load order the loader actually used, which scripts were overridden by more than one mod, and every error and warning with a guess at which mod caused it.


**Undo a bad launch.** The manager checkpoints your mod setup each time you launch the game. If a change broke something, one click puts the previous setup back.

**For mod authors.** The Mod Packager turns a folder into a `.vmz` the loader accepts, with the manifest, dependencies and VostokMods source filled in, and exports a shareable mod list as JSON.

## Install

1. Download and unzip.
2. Run `VostokModManagerIntegrated.exe`. There is no installer and nothing else to download.
3. If the manager does not find your game, set the mods folder under Settings.

Requires Windows 10 or 11 (64-bit).

## Good to know

- **Network use.** The manager contacts vostokmods.net (mod listings, update checks, downloads) and GitHub (update checks and downloads for the mod loader and for the manager itself). It sends nothing about you or your mods anywhere.
- **Self-update.** The manager updates itself from its GitHub releases, so the copy you download here will offer newer builds as they come out.
- **Modpack settings.** A modpack's bundled mod settings (MCM) are not applied by the manager; apply the pack from the loader's in-game Modpacks tab if you want those too.
- **AI disclosure.** This program was written with the help of generative AI (Claude).
- **AI edition.** A second build adds conflict resolution and log diagnosis through a local Claude Code CLI. It is on GitHub; the download on this page is the standard edition, which needs nothing extra.

## Links

- Source code, issues and all releases: https://github.com/RTVmods/RTV-Mod-Manager
- License: GNU GPL v3.0
