extends RefCounted

# One installed mod, either as a .vmz archive or an unpacked directory.
# Constructed by ModRegistry; consumed by ConflictDetector and the UI.

const VmmModArchive = preload("res://scripts/Core/ModArchive.gd")

var path: String                 # absolute path to .vmz file or directory
var is_archive: bool             # true if .vmz, false if directory
var is_enabled: bool             # false if located under <mods>/Disabled/
var manifest: Dictionary = {}    # parsed mod.txt sections
var files: PackedStringArray     # archive-relative file paths (no leading slash)


func mod_id() -> String:
	var section: Dictionary = manifest.get("mod", {})
	return str(section.get("id", ""))


func display_name() -> String:
	var section: Dictionary = manifest.get("mod", {})
	return str(section.get("name", ""))


func version() -> String:
	var section: Dictionary = manifest.get("mod", {})
	return str(section.get("version", ""))


func priority() -> int:
	var section: Dictionary = manifest.get("mod", {})
	return int(section.get("priority", 0))


func description() -> String:
	var section: Dictionary = manifest.get("mod", {})
	return str(section.get("description", ""))


func modworkshop_id() -> int:
	var updates: Dictionary = manifest.get("updates", {})
	return int(updates.get("modworkshop", 0))


func autoloads() -> Dictionary:
	return manifest.get("autoload", {})


func hooks() -> Dictionary:
	return manifest.get("hooks", {})


func script_extends() -> Dictionary:
	return manifest.get("script_extend", {})


# Returns the text content of `file_path` inside this mod, or "" if absent.
# Re-opens the archive each call; small mods make this cheap, but callers
# in tight loops should batch reads via a freshly opened VmmModArchive.
func read_file_text(file_path: String) -> String:
	if is_archive:
		var arch := VmmModArchive.new()
		if arch.open(path) != OK:
			return ""
		var text := arch.read_text(file_path)
		arch.close()
		return text
	var full := path.path_join(file_path)
	if not FileAccess.file_exists(full):
		return ""
	var f := FileAccess.open(full, FileAccess.READ)
	if f == null:
		return ""
	var text := f.get_as_text()
	f.close()
	return text
