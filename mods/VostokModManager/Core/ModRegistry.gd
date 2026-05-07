extends RefCounted

# Scans the game's mods folder and builds a list of installed VmmModEntry
# instances. Recognizes:
#   - <mods>/*.vmz                    → enabled archive mod
#   - <mods>/<DirName>/mod.txt        → enabled directory mod
#   - <mods>/Disabled/*.vmz           → disabled archive mod
#   - <mods>/Disabled/<DirName>/...   → disabled directory mod
# Other folders without a mod.txt (e.g. config-only folders like MoreJobs/)
# are ignored.

const VmmModArchive = preload("res://mods/VostokModManager/Core/ModArchive.gd")
const VmmModEntry = preload("res://mods/VostokModManager/Core/ModEntry.gd")

var mods_dir: String
var entries: Array = []  # Array of VmmModEntry instances


func scan(p_mods_dir: String) -> Error:
	mods_dir = p_mods_dir
	entries.clear()
	if not DirAccess.dir_exists_absolute(mods_dir):
		return ERR_FILE_NOT_FOUND
	_scan_dir(mods_dir, true)
	var disabled := mods_dir.path_join("Disabled")
	if DirAccess.dir_exists_absolute(disabled):
		_scan_dir(disabled, false)
	return OK


func find_by_id(p_mod_id: String):
	for e in entries:
		if e.mod_id() == p_mod_id:
			return e
	return null


func enabled() -> Array:
	var out: Array = []
	for e in entries:
		if e.is_enabled:
			out.append(e)
	return out


func _scan_dir(dir_path: String, is_enabled: bool) -> void:
	var dir := DirAccess.open(dir_path)
	if dir == null:
		return
	dir.list_dir_begin()
	while true:
		var name := dir.get_next()
		if name == "":
			break
		if name.begins_with("."):
			continue
		var full := dir_path.path_join(name)
		if dir.current_is_dir():
			# Skip the Disabled subfolder when scanning the top level — we
			# scan it separately with is_enabled=false.
			if is_enabled and name == "Disabled":
				continue
			var manifest_path := full.path_join("mod.txt")
			if FileAccess.file_exists(manifest_path):
				var entry = _load_dir_entry(full, is_enabled)
				if entry != null:
					entries.append(entry)
		elif name.to_lower().ends_with(".vmz"):
			var entry = _load_archive_entry(full, is_enabled)
			if entry != null:
				entries.append(entry)
	dir.list_dir_end()


func _load_archive_entry(p_path: String, is_enabled: bool):
	var arch := VmmModArchive.new()
	if arch.open(p_path) != OK:
		push_warning("VmmModRegistry: could not open archive %s" % p_path)
		return null
	var entry := VmmModEntry.new()
	entry.path = p_path
	entry.is_archive = true
	entry.is_enabled = is_enabled
	entry.manifest = arch.get_manifest()
	entry.files = arch.file_list()
	arch.close()
	return entry


func _load_dir_entry(p_path: String, is_enabled: bool):
	var entry := VmmModEntry.new()
	entry.path = p_path
	entry.is_archive = false
	entry.is_enabled = is_enabled
	var manifest_path := p_path.path_join("mod.txt")
	var f := FileAccess.open(manifest_path, FileAccess.READ)
	if f == null:
		return null
	var text := f.get_as_text()
	f.close()
	var cfg := ConfigFile.new()
	if cfg.parse(text) != OK:
		push_warning("VmmModRegistry: failed to parse %s" % manifest_path)
		return null
	for section in cfg.get_sections():
		var section_data: Dictionary = {}
		for key in cfg.get_section_keys(section):
			section_data[key] = cfg.get_value(section, key)
		entry.manifest[section] = section_data
	var files := PackedStringArray()
	_list_files_recursive(p_path, "", files)
	entry.files = files
	return entry


func _list_files_recursive(root: String, rel: String, out: PackedStringArray) -> void:
	var dir_path := root.path_join(rel) if rel != "" else root
	var dir := DirAccess.open(dir_path)
	if dir == null:
		return
	dir.list_dir_begin()
	while true:
		var name := dir.get_next()
		if name == "":
			break
		if name.begins_with("."):
			continue
		var rel_child := rel.path_join(name) if rel != "" else name
		if dir.current_is_dir():
			_list_files_recursive(root, rel_child, out)
		else:
			out.append(rel_child)
	dir.list_dir_end()
