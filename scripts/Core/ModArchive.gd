extends RefCounted

# Reads a Road to Vostok .vmz mod archive (zip) and parses its mod.txt.
#
# NOTE: We deliberately don't declare `class_name` on any mod script.
# Godot registers class_name globals at static parse time, but the game's
# ModLoader mounts our .vmz at runtime — too late for the global cache.
# Other mods reference each other's scripts via preload(); we follow the
# same convention. See the log entry "Class cache has only 2 entries" for
# the modloader's own diagnosis of this constraint.
#
# mod.txt is Godot ConfigFile format. Returned manifest shape:
#   {
#     "mod":           {id, name, version, priority?, description?, author?, url?},
#     "autoload":      {<Name>: <res:// path>, ...},
#     "hooks":         {<res:// target>: <method>, ...},
#     "script_extend": {<res:// target>: <res:// override>, ...},
#     "updates":       {modworkshop: <int>, ...},
#   }
# Sections that aren't present in the file are simply absent from the dict.

var path: String
var _zip: ZIPReader
var _files: PackedStringArray
var _manifest: Dictionary


func open(p_path: String) -> Error:
	path = p_path
	_zip = ZIPReader.new()
	var err := _zip.open(p_path)
	if err != OK:
		_zip = null
		return err
	_files = _zip.get_files()
	return OK


func close() -> void:
	if _zip != null:
		_zip.close()
		_zip = null


func is_open() -> bool:
	return _zip != null


func file_list() -> PackedStringArray:
	return _files


func has_file(file_path: String) -> bool:
	return _files.has(file_path)


func read_text(file_path: String) -> String:
	if _zip == null or not _files.has(file_path):
		return ""
	var bytes := _zip.read_file(file_path)
	return bytes.get_string_from_utf8()


func read_bytes(file_path: String) -> PackedByteArray:
	if _zip == null or not _files.has(file_path):
		return PackedByteArray()
	return _zip.read_file(file_path)


func get_manifest() -> Dictionary:
	if not _manifest.is_empty():
		return _manifest
	if _zip == null or not _files.has("mod.txt"):
		return {}
	var text := read_text("mod.txt")
	var cfg := ConfigFile.new()
	var err := cfg.parse(text)
	if err != OK:
		push_warning("VmmModArchive: failed to parse mod.txt in %s (err=%d)" % [path, err])
		return {}
	var m: Dictionary = {}
	for section in cfg.get_sections():
		var section_data: Dictionary = {}
		for key in cfg.get_section_keys(section):
			section_data[key] = cfg.get_value(section, key)
		m[section] = section_data
	_manifest = m
	return m


func mod_id() -> String:
	var section: Dictionary = get_manifest().get("mod", {})
	return str(section.get("id", ""))


func mod_name() -> String:
	var section: Dictionary = get_manifest().get("mod", {})
	return str(section.get("name", ""))


func mod_version() -> String:
	var section: Dictionary = get_manifest().get("mod", {})
	return str(section.get("version", ""))


func mod_priority() -> int:
	var section: Dictionary = get_manifest().get("mod", {})
	return int(section.get("priority", 0))


func mod_description() -> String:
	var section: Dictionary = get_manifest().get("mod", {})
	return str(section.get("description", ""))


func modworkshop_id() -> int:
	var updates: Dictionary = get_manifest().get("updates", {})
	return int(updates.get("modworkshop", 0))
