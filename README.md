# RTV Mod Manager

A standalone desktop app for managing [Road to Vostok](https://modworkshop.net/mod/56801) mods. Detects every kind of conflict the Vostok Mod Loader (MML) cares about, lets you enable / disable / reorder mods with live persistence to `mod_config.cfg`, downloads updates from ModWorkshop, supports named profiles with bundled-archive snapshots, ships a mod-pack importer for sharing curated mod lists as JSON, includes a **Mod Packager** tool for authoring your own `.vmz` releases, and resolves missing dependencies on install with one click.

A single self-contained `.exe`. No installer, no .NET runtime to fetch, no extra dependencies.

**ModWorkshop:** <https://modworkshop.net/mod/56801>

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

### Mods grid
- **Enable / disable** any mod — toggles move the `.vmz` between the live mods folder and `mods/Disabled/`, and rewrite `mod_config.cfg` in lockstep.
- **Update column** — `⬆` when a newer release is on ModWorkshop, `✓` when current, `—` when the mod has no link. Click `⬆` to fetch and swap in the latest `.vmz` (replaced version is auto-backed-up first).
- **Load-order priority** — edit in place; persisted to `mod_config.cfg` immediately.
- **Drag-and-drop install** — drop `.vmz` files to install, or a `.json` mod-pack to open the importer.
- **Filter, bulk enable/disable, multi-select batch delete**, per-column width persistence.

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

The **AI edition** can additionally submit `file_overlap` `.gd` conflicts to Claude, which returns a `merge_safe` / `order_resolves` / `incompatible` verdict (with merged source when safe).

### Profiles
Save / apply / clone / import / export complete mod loadouts as `.vmprofile` files. A profile bundles byte-for-byte `.vmz` copies so it's a full restore point even on a fresh machine. Applying a profile reconciles the live folder, the Library, and `mod_config.cfg`. Locked mods survive profile switches untouched.

### Mod packs
Lightweight JSON files listing mods to merge into the active profile (distinct from full profiles). Import via drag-drop or the **Import list…** button; author via the Mod Packager's **Export JSON…**.

### Mod Packager (creator tool)
Package a folder or existing `.vmz` into a fresh archive with edited manifest fields and a proper `[dependencies]` section — output uses forward-slash entry paths so the in-game loader accepts it. Also exports mod-pack JSON.

### Dependencies, updates, crash checkpoints
- One-click resolution of missing dependencies on install, with recursive re-checking.
- Per-mod update checks against ModWorkshop; MML loader version checks against GitHub releases; the manager checks its own latest version too.
- Two-tier crash rollback (LastLaunch / LastKnownGood) captured on launch and promoted on clean exit.

---

## File formats

- **`.vmz`** — a mod: zip of mod files + a `mod.txt` manifest (Godot INI format).
- **`.vmprofile`** — an exported profile: zip of `profile.json` + bundled `mods/*.vmz`.
- **`mod_config.cfg`** — the in-game loader's per-profile enabled / priority / active-profile state.

---

## Building from source

Requires the .NET 8 SDK (Windows).

```sh
# Integrated edition (default)
dotnet publish -c Release -p:EnableAI=false   # → publish/integrated/VostokModManagerIntegrated.exe

# AI edition
dotnet publish -c Release -p:EnableAI=true    # → publish/ai/VostokModManagerAI.exe
```

The single version source is `<Version>` in `VostokModManager.csproj`. The `AI_RESOLVER` compile constant is defined only in the AI build; AI-aware code paths are wrapped in `#if AI_RESOLVER`, and the AI-only source files (`Ai/`, `Api/ClaudeCodeRunner.cs`, `Ui/ResolutionDialog.cs`) are excluded from the Integrated compile.

### Project layout

| Folder | Contents |
|---|---|
| `Domain/` | Core logic: registry, archive parsing, conflict detection, profiles, library, packager, backups, checkpoints. |
| `Api/` | ModWorkshop client, MML GitHub release checker, Claude CLI runner. |
| `Ai/` | AI conflict resolver (AI edition only). |
| `Ui/` | Dialogs (profiles, packager, settings, themed message boxes, etc.). |
| `MainForm.cs` | The main window. |

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
