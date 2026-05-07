extends RefCounted

# Finds conflicts among enabled mods. Returns Array of Conflict dicts:
#   {
#     "type":     String,        # see TYPE_* constants below
#     "key":      String,        # the resource that's contested
#     "mod_ids":  Array[String], # mods involved (load-order-undefined)
#     "details":  Dictionary,    # type-specific extras
#   }
#
# Manifest-only signals (cheap):
#   TYPE_FILE_OVERLAP            same file path written by 2+ mods
#   TYPE_AUTOLOAD_COLLISION      same autoload name registered by 2+ mods
#   TYPE_HOOK_COLLISION          same (script, method) hooked by 2+ mods
#   TYPE_SCRIPT_EXTEND_COLLISION same script extended by 2+ mods
#
# Script-level signals (require reading .gd contents from archives):
#   TYPE_CLASS_NAME_COLLISION    same class_name declared by 2+ mods
#   TYPE_TAKE_OVER_COLLISION     same take_over_path target used by 2+ mods
#   TYPE_SUPER_CHAIN_CONSTRAINT  not a conflict per se: an inferred
#                                load-order constraint (mod A's func calls
#                                super(), mod B overrides same func without)

const VmmModArchive = preload("res://mods/VostokModManager/Core/ModArchive.gd")
const VmmGDScriptAnalyzer = preload("res://mods/VostokModManager/Core/GDScriptAnalyzer.gd")

const TYPE_FILE_OVERLAP := "file_overlap"
const TYPE_AUTOLOAD_COLLISION := "autoload_collision"
const TYPE_HOOK_COLLISION := "hook_collision"
const TYPE_SCRIPT_EXTEND_COLLISION := "script_extend_collision"
const TYPE_CLASS_NAME_COLLISION := "class_name_collision"
const TYPE_TAKE_OVER_COLLISION := "take_over_collision"
const TYPE_SUPER_CHAIN_CONSTRAINT := "super_chain_constraint"


# Manifest-only detection. Fast; no archive reads beyond what
# VmmModRegistry already did.
static func detect_manifest_conflicts(entries: Array) -> Array:
	var live: Array = []
	for e in entries:
		if e.is_enabled:
			live.append(e)
	var out: Array = []
	out.append_array(_overlap_by_files(live))
	out.append_array(_overlap_by_section(
		live, "autoload", TYPE_AUTOLOAD_COLLISION
	))
	out.append_array(_overlap_by_section(
		live, "hooks", TYPE_HOOK_COLLISION
	))
	out.append_array(_overlap_by_section(
		live, "script_extend", TYPE_SCRIPT_EXTEND_COLLISION
	))
	return out


# Deep detection: opens each archive, reads .gd contents, runs analyzer.
# Costs ~1 archive open + N file reads per enabled mod. For 50 mods with
# ~10 .gd files each, expect a few hundred milliseconds.
static func detect_script_conflicts(entries: Array) -> Array:
	var live: Array = []
	for e in entries:
		if e.is_enabled:
			live.append(e)
	# mod_id -> { script_path: analysis_dict }
	var analyses: Dictionary = {}
	for e in live:
		analyses[e.mod_id()] = _analyze_mod_scripts(e)

	var out: Array = []
	out.append_array(_class_name_collisions(live, analyses))
	out.append_array(_take_over_collisions(live, analyses))
	out.append_array(_super_chain_constraints(live, analyses))
	return out


# Convenience: both passes.
static func detect_all(entries: Array) -> Array:
	var out := detect_manifest_conflicts(entries)
	out.append_array(detect_script_conflicts(entries))
	return out


# --- internals -----------------------------------------------------------

static func _overlap_by_files(entries: Array) -> Array:
	var by_path: Dictionary = {}
	for e in entries:
		for f in e.files:
			# Skip the manifest itself, directory markers, and Godot's
			# pre-imported .ctex / .uid / .import sidecar files — those
			# only collide if the actual source file does.
			if f == "mod.txt":
				continue
			if f.ends_with("/"):
				continue
			if f.begins_with(".godot/"):
				continue
			if not by_path.has(f):
				by_path[f] = []
			by_path[f].append(e)
	var out: Array = []
	for path in by_path:
		var owners: Array = by_path[path]
		if owners.size() > 1:
			out.append({
				"type": TYPE_FILE_OVERLAP,
				"key": path,
				"mod_ids": owners.map(func(e) -> String: return e.mod_id()),
				"details": {
					"is_gdscript": path.ends_with(".gd"),
				},
			})
	return out


static func _overlap_by_section(
	entries: Array, section: String, conflict_type: String
) -> Array:
	var by_key: Dictionary = {}  # key -> Array[{mod, value}]
	for e in entries:
		var section_data: Dictionary = e.manifest.get(section, {})
		for key in section_data:
			if not by_key.has(key):
				by_key[key] = []
			by_key[key].append({"mod": e, "value": section_data[key]})
	var out: Array = []
	for key in by_key:
		var owners: Array = by_key[key]
		if owners.size() > 1:
			out.append({
				"type": conflict_type,
				"key": str(key),
				"mod_ids": owners.map(
					func(o: Dictionary) -> String: return o["mod"].mod_id()
				),
				"details": {
					"values": owners.map(
						func(o: Dictionary) -> String: return str(o["value"])
					),
				},
			})
	return out


static func _analyze_mod_scripts(entry) -> Dictionary:
	var out: Dictionary = {}
	if entry.is_archive:
		var arch := VmmModArchive.new()
		if arch.open(entry.path) != OK:
			return out
		for f in entry.files:
			if not f.ends_with(".gd"):
				continue
			var src := arch.read_text(f)
			if src != "":
				out[f] = VmmGDScriptAnalyzer.analyze(src)
		arch.close()
	else:
		for f in entry.files:
			if not f.ends_with(".gd"):
				continue
			var src := entry.read_file_text(f)
			if src != "":
				out[f] = VmmGDScriptAnalyzer.analyze(src)
	return out


static func _class_name_collisions(
	entries: Array, analyses: Dictionary
) -> Array:
	var by_class: Dictionary = {}
	for e in entries:
		var mod_analyses: Dictionary = analyses.get(e.mod_id(), {})
		for script_path in mod_analyses:
			var info: Dictionary = mod_analyses[script_path]
			var cls := str(info.get("class_name", ""))
			if cls == "":
				continue
			if not by_class.has(cls):
				by_class[cls] = []
			by_class[cls].append(e.mod_id())
	var out: Array = []
	for cls in by_class:
		var owners: Array = by_class[cls]
		# Dedupe — a single mod with two scripts declaring the same
		# class_name is its own bug, not a cross-mod conflict.
		var unique_owners: Array = []
		for m in owners:
			if m not in unique_owners:
				unique_owners.append(m)
		if unique_owners.size() > 1:
			out.append({
				"type": TYPE_CLASS_NAME_COLLISION,
				"key": str(cls),
				"mod_ids": unique_owners,
				"details": {},
			})
	return out


static func _take_over_collisions(
	entries: Array, analyses: Dictionary
) -> Array:
	var by_target: Dictionary = {}
	for e in entries:
		var mod_analyses: Dictionary = analyses.get(e.mod_id(), {})
		for script_path in mod_analyses:
			var info: Dictionary = mod_analyses[script_path]
			var targets: Array = info.get("take_over_paths", [])
			for t in targets:
				if not by_target.has(t):
					by_target[t] = []
				if e.mod_id() not in by_target[t]:
					by_target[t].append(e.mod_id())
	var out: Array = []
	for target in by_target:
		var owners: Array = by_target[target]
		if owners.size() > 1:
			out.append({
				"type": TYPE_TAKE_OVER_COLLISION,
				"key": str(target),
				"mod_ids": owners,
				"details": {},
			})
	return out


# Inferred load-order constraint. For each game-script path that 2+ mods
# extend (via `extends "res://X.gd"`):
#   - For each function that any extending mod overrides:
#     - If mod A's override calls super() in that function and mod B's
#       does not, A must load AFTER B (B replaces, A chains).
#
# Emitted as a "conflict" of type SUPER_CHAIN_CONSTRAINT carrying the
# implied ordering in details.before / details.after.
static func _super_chain_constraints(
	entries: Array, analyses: Dictionary
) -> Array:
	# extended_path -> { mod_id: { func_name: calls_super } }
	var by_target: Dictionary = {}
	for e in entries:
		var mod_analyses: Dictionary = analyses.get(e.mod_id(), {})
		for script_path in mod_analyses:
			var info: Dictionary = mod_analyses[script_path]
			var ext_path := str(info.get("extends_path", ""))
			if ext_path == "":
				continue
			var funcs: Dictionary = info.get("functions", {})
			if funcs.is_empty():
				continue
			if not by_target.has(ext_path):
				by_target[ext_path] = {}
			by_target[ext_path][e.mod_id()] = funcs

	var out: Array = []
	for target in by_target:
		var per_mod: Dictionary = by_target[target]
		var mod_ids: Array = per_mod.keys()
		if mod_ids.size() < 2:
			continue
		var fn_set: Dictionary = {}
		for mid in mod_ids:
			var funcs: Dictionary = per_mod[mid]
			for fn in funcs:
				fn_set[fn] = true
		for fn in fn_set:
			var chainers: Array = []
			var replacers: Array = []
			for mid in mod_ids:
				var funcs: Dictionary = per_mod[mid]
				if not funcs.has(fn):
					continue
				var info: Dictionary = funcs[fn]
				if bool(info.get("calls_super", false)):
					chainers.append(mid)
				else:
					replacers.append(mid)
			# Emit one constraint per (chainer, replacer) pair:
			# replacer must come BEFORE chainer.
			for chainer in chainers:
				for replacer in replacers:
					out.append({
						"type": TYPE_SUPER_CHAIN_CONSTRAINT,
						"key": "%s::%s" % [target, fn],
						"mod_ids": [replacer, chainer],
						"details": {
							"target_script": target,
							"function": fn,
							"before": replacer,
							"after": chainer,
						},
					})
	return out
