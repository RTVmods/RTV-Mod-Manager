# RTV Mod Manager

A standalone desktop app for managing Road to Vostok mods. Detects every kind of conflict the Metro Mod Loader (MML) cares about, lets you enable / disable / reorder mods with live persistence to `mod_config.cfg`, browses, installs and updates mods from [VostokMods](https://vostokmods.net), supports named profiles with bundled-archive snapshots, ships a mod-pack importer for sharing curated mod lists as JSON, includes a **Mod Packager** tool for authoring your own `.vmz` releases, and resolves missing dependencies on install with one click.

A single self-contained `.exe`. No installer, no .NET runtime to fetch, no extra dependencies.

**Downloads:** <https://github.com/RTVmods/RTV-Mod-Manager/releases>

Built for Road to Vostok Build 2 and MML 3.4 or later.

---

## Editions

One source tree, two builds:

| Edition | Assembly | What it adds |
|---|---|---|
| **Integrated** (default release) | `VostokModManagerIntegrated.exe` | Full conflict *detection*. No external dependencies. The right choice for most users. |
| **AI** | `VostokModManagerAI.exe` | Everything in Integrated, plus Claude Code–driven `file_overlap` resolution (merge generation for overlapping `.gd` files). Requires a local `claude` CLI. |

Both editions detect the same conflicts, share the same `settings.json`, and work standalone.

---

## What it does

The window is laid out like the game's own menus: a side menu (Mods, Profiles, Saves, Browse VostokMods, Library, Tools, Settings), the page you opened, and a launch panel that shows the profile about to run, a pre-flight check (load order, missing dependencies, updates, conflicts, save slots) and the **LAUNCH** button. It is set in Lora, the typeface the game uses.

### Mods
- **Enable / disable** any mod with its tick; `mod_config.cfg` is rewritten in lockstep. Ctrl/Shift-select several mods to act on them together.
- **Status chips** above the list: updates waiting (**Update all**), conflicts, mods not linked to VostokMods (**Link**), and changes not yet saved to the active profile. Filter tabs with counts: All, Enabled, Disabled, Updates, Conflicts, Testing.
- **Load order**: drag rows to reorder, or set a number from the row's menu; persisted to `mod_config.cfg` immediately.
- **Install**: drop `.vmz` files on the page, or **Install mod…**; dropped modpack `.zip` and mod-list `.json` files open the importer.
- **Details panel** for the selected mod: update to the latest release (the replaced version is backed up first, and **Revert to an earlier version…** puts one back), the VostokMods description and the mod's changelog, its dependencies and their state, the conflicts it is in with who wins and why, the testing / lock flags and a personal note.
- **Undo last change** restores the checkpoint taken before the last launch.

### Conflict detection
Every detected conflict appears under one of three severity tiers:

| Type | Tier | Meaning |
|---|---|---|
| `class_name_collision` | LOAD-BLOCKING | Two mods declare the same `class_name`; Godot refuses to load. |
| `missing_dependency` | LOAD-BLOCKING | A required dependency isn't installed or is disabled. |
| `file_overlap` | BEHAVIOR-AFFECTING | Two mods write the same file; last to load wins. |
| `autoload_collision` | BEHAVIOR-AFFECTING | Two mods register the same autoload name. |
| `hook_collision` | BEHAVIOR-AFFECTING | Two mods hook the same function. |
| `script_extend_collision` | BEHAVIOR-AFFECTING | Two mods extend the same script. |
| `take_over_collision` | BEHAVIOR-AFFECTING | Two mods `take_over_path` the same script. |
| `duplicate_mod_id` | BEHAVIOR-AFFECTING | Two `.vmz` files share the same `mod_id`. |
| `dependency_order` | INFO | A mod loads before its declared dependency. |
| `super_chain_constraint` | INFO | Inferred load-order hint for `super`-chaining scripts. |

The **AI edition** can additionally submit `file_overlap` `.gd` conflicts to Claude (**Ask Claude to resolve** on the conflict), which returns a `merge_safe` / `order_resolves` / `incompatible` verdict (with merged source when safe), and in Tools → Log analysis **Analyze with Claude** reads the game's log and proposes a load order.

### Where a mod comes from
A mod's source is `vostokmods:<slug>`, where the slug is the last part of its page URL (`vostokmods.net/mod/<slug>`). The manager reads it from the same two places the mod loader does:

- `mod.txt`: `[updates]` `source="vostokmods:<slug>"`, set by the mod's author.
- `mod_config.cfg`: the `[mod_sources]` record for the mod, written when the manager (or the loader) downloads it, or when you link it by hand.

**Link to VostokMods…** in a mod's details links one; the **not linked · Link** chip links every mod whose name matches exactly one page on the site.

### Browse VostokMods
The site's catalogue inside the manager: category tabs, cards that show each mod's state against your setup (Install, Update, Installed, In another profile), a details panel with **Install into** ticks for every profile, the modpacks with **Install the missing mods**, and a **Website** tab with the real site, where downloading a mod installs it.

### Profiles
The Profiles page shows every profile as a card and a table of where they differ, with fixes per row (copy an entry across, remove it). Save / apply / clone / import / export complete mod loadouts as `.vmprofile` files. A profile bundles byte-for-byte `.vmz` copies so it's a full restore point even on a fresh machine. Applying a profile reconciles the live folder, the Library, and `mod_config.cfg`. Locked mods survive profile switches untouched. The active profile follows the version of each mod that is actually live, however it got there (manager update, in-game loader update, a file dropped into the folder), so switching away and back never reinstalls an older version. When a profile you switch to pins a mod to a version older than the newest one in the Library, the manager asks which of those mods should move to the newest version first; unticked mods keep the profile's own version. The **Library** window has a search box, multi-select delete, and **Clean up old versions**, which removes every archived version that is not the newest of its mod, not live, and not pinned by a saved profile. Mods that share an id but are different mods (an abandoned mod and its successor) are told apart by their VostokMods source or name.

A profile's `profile.json` uses the mod loader's own modpack schema (`metroprofile` 1), so a profile exported as **Mod loader modpack (.zip)** and placed in the game's `mods` folder shows up on the loader's Modpacks tab.

On first run the manager adopts the mods folder as one profile (named after the loader's active profile) and offers the other profiles the loader keeps in `mod_config.cfg` for import, each with its enabled state and load order, so a set-up made in the loader's launcher carries over.

### Mod packs
Lightweight JSON files listing mods to merge into the active profile (distinct from full profiles). Import by dropping one on the Mods page or with **Install mod…**; author one with the Mod Packager's **Export mod list…**.

### VostokMods modpacks
Browse → **Modpacks** lists every modpack published on VostokMods. Installing one adds every mod in it at the versions the pack lists, verified against the pack's checksums. Every import first asks where it should go: the active profile, a new profile named after the pack, or another saved profile (the manager switches to it first), then shows the plan, one row per mod, before anything is downloaded.

### Mod Packager (creator tool)
Package a folder or existing `.vmz` into a fresh archive with edited manifest fields and a proper `[dependencies]` section — output uses forward-slash entry paths so the in-game loader accepts it. Also exports mod-pack JSON, and **builds a modpack `.zip`** in the mod loader's format (the same form a VostokMods modpack takes): `profile.json` naming each mod by its VostokMods source and pinned version. Such a zip works on the loader's Modpacks tab and can be dropped on the manager, which installs everything in it.

### Dependencies, updates, crash checkpoints
- One-click resolution of missing dependencies on install, with recursive re-checking.
- Per-mod update checks against VostokMods; MML loader version checks against GitHub releases; the manager checks its own latest GitHub release and can update itself in place.
- Two-tier crash rollback (LastLaunch / LastKnownGood) captured on launch and promoted on clean exit.
- **LAUNCH** starts the game through Steam and, with Metro Mod Loader 3.4.2 or later, skips the loader's launcher window (the mods are already set up here); the active profile loads straight away. Off switch in Settings.

### Saves
Save slots are built in: the manager installs its own Save Slots mod (based on Razzer's Multi Save Slots 1.4), keeps it on in every profile and updates it with itself. The game's menu gets five slots, and each slot can be tied to a profile so a save always runs with the mods it was made with; loading a slot tied to another profile restarts the game into that profile. The Saves page shows each slot as a card (where, when, the profile it is tied to, and whether every mod that profile needs is here) and **Launch slot** switches profile and starts the game straight into it. Settings → Built-in save slots turns the feature off.

### Tools and setup
Tools holds the game-log analysis (which mods hooked or replaced the same scripts, errors and warnings), the Mod Packager and the support package (one file with your mod list, `mod_config.cfg` and the logs, for bug reports). On first run a setup window walks through five checks: game folder, Mod Loader, mods and profiles, save slots and VostokMods links; Settings → **Run setup again** reopens it.

---

## File formats

- **`.vmz`** — a mod: zip of mod files + a `mod.txt` manifest (Godot INI format).
- **`.vmprofile`** — an exported profile: zip of `profile.json` (metroprofile schema) + bundled `mods/*.vmz`.
- **`mod_config.cfg`** — the in-game loader's per-profile enabled / priority / active-profile state and its `[mod_sources]` records. The manager changes only the entries it manages and writes everything else back exactly as it found it.

---

## Building from source

Requires the .NET 8 SDK (Windows).

```sh
# Integrated edition (default)
dotnet publish src/VostokModManager.Wpf -c Release -p:EnableAI=false   # → publish/integrated/VostokModManagerIntegrated.exe

# AI edition
dotnet publish src/VostokModManager.Wpf -c Release -p:EnableAI=true    # → publish/ai/VostokModManagerAI.exe

# Tests (run against a temp sandbox, never your real files)
dotnet test
```

The single version source is `<Version>` in `Directory.Build.props`, which also defines the two editions. The `AI_RESOLVER` compile constant is defined only in the AI build; AI-aware code paths are wrapped in `#if AI_RESOLVER`, and the AI-only source files (`Ai/`, `Api/ClaudeCodeRunner.cs`) are excluded from the Integrated compile. The `.exe` names must stay as they are: the self-update looks for a release asset named like the running `.exe`.

To run the manager against a sandbox instead of your real `%APPDATA%`, set `VMM_APPDATA_ROOT` to a folder standing in for `AppData\Roaming` before starting it. A Debug build also takes `--screenshot <page> <out.png>` to render one page (mods, profiles, saves, browse, library, tools, settings, setup) to a PNG and exit.

### Project layout

| Folder | Contents |
|---|---|
| `src/VostokModManager.Core/Domain/` | Core logic: registry, archive parsing, conflict detection, profiles, library, packager, backups, checkpoints. |
| `src/VostokModManager.Core/Api/` | VostokMods client, GitHub release checkers (MML and the manager itself), Claude CLI runner. |
| `src/VostokModManager.Core/Ai/` | AI conflict resolver (AI edition only). |
| `src/VostokModManager.Core/Services/` | `ManagerSession`: every workflow (scan, install, update, profiles, saves, launch, setup), with no UI. It asks the user through `IUserPrompt`. |
| `src/VostokModManager.Wpf/` | The WPF front end: `Themes/` (the game-style theme), `Views/`, `ViewModels/`. |
| `tests/VostokModManager.Tests/` | Tests; each runs in a temp-folder sandbox. |

---

## Requirements

- Windows 10 / 11 (x64).
- Road to Vostok installed (any path — the mods folder is configurable in Settings).
- Vostok Mod Loader (MML) to actually run mods in-game.

No .NET install required for the released `.exe`; it's self-contained.

---

## License

GPL-3.0. See [LICENSE](LICENSE).

Built for the Road to Vostok modding community.
