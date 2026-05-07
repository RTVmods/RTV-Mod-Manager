extends RefCounted

# Tiny persistence helper for Vostok Mod Manager user settings.
# Stored under the game's user:// dir (which RtV maps to
# %APPDATA%/Road to Vostok/) so it persists across sessions and isn't
# shipped in the .vmz.
#
# Static API — call Settings.load_or_default() / Settings.save({...}).

const PATH := "user://vmm_settings.cfg"
const SECTION := "vmm"

# Default values are returned when keys are missing or the file doesn't
# exist yet, so callers don't have to special-case first launch.
const DEFAULTS := {
	"claude_path": "",         # manual override; empty = use ClaudeCodeRunner auto-detect
	"game_source_path": "",    # path to decompiled game source (Decomp/) for AI context
	"mods_dir": "",            # override the default mods folder; empty = use hardcoded default
}


static func load_or_default() -> Dictionary:
	var out: Dictionary = DEFAULTS.duplicate()
	var cfg := ConfigFile.new()
	if cfg.load(PATH) != OK:
		return out
	for key in DEFAULTS:
		out[key] = str(cfg.get_value(SECTION, key, DEFAULTS[key]))
	return out


static func save(values: Dictionary) -> Error:
	var cfg := ConfigFile.new()
	for key in DEFAULTS:
		var v: String = str(values.get(key, DEFAULTS[key]))
		cfg.set_value(SECTION, key, v)
	return cfg.save(PATH)
