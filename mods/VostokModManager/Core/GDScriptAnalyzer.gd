extends RefCounted

# Static analysis of GDScript source. Recognizes:
#   - extends "res://..."  (path-based)
#   - extends ClassName    (name-based)
#   - class_name Foo
#   - super() / super.method()  per function
#   - take_over_path("res://...")
#
# This is the surface Dildz/RtV-Load-Order-Editor uses to derive load-order
# constraints (a mod that calls super() in foo() must load AFTER any mod
# that overrides foo() without super()).
#
# Function tracking is line-based and naive — it assumes "the next func line
# starts a new function". GDScript has no nested funcs, so this is fine for
# our purposes. Inner classes are not handled separately.

const _RE_EXTENDS_PATH := "^\\s*extends\\s+\"((?:res://|user://)[^\"]+)\""
const _RE_EXTENDS_CLASS := "^\\s*extends\\s+([A-Za-z_][A-Za-z0-9_]*)\\s*$"
const _RE_CLASS_NAME := "^\\s*class_name\\s+([A-Za-z_][A-Za-z0-9_]*)"
const _RE_FUNC := "^\\s*(?:static\\s+)?func\\s+([A-Za-z_][A-Za-z0-9_]*)\\s*\\("
const _RE_SUPER_BARE := "\\bsuper\\s*\\("
const _RE_SUPER_METHOD := "\\bsuper\\s*\\.\\s*([A-Za-z_][A-Za-z0-9_]*)\\s*\\("
const _RE_TAKEOVER := "take_over_path\\s*\\(\\s*\"([^\"]+)\"\\s*\\)"


# Returns:
# {
#   "extends_path":     String,             # "res://..." or ""
#   "extends_class":    String,             # ClassName or ""
#   "class_name":       String,
#   "functions":        { <name>: { "calls_super": bool, "super_methods": Array } },
#   "take_over_paths":  Array[String],
# }
static func analyze(source: String) -> Dictionary:
	var result: Dictionary = {
		"extends_path": "",
		"extends_class": "",
		"class_name": "",
		"functions": {},
		"take_over_paths": [],
	}
	var re_ext_path := RegEx.create_from_string(_RE_EXTENDS_PATH)
	var re_ext_class := RegEx.create_from_string(_RE_EXTENDS_CLASS)
	var re_class_name := RegEx.create_from_string(_RE_CLASS_NAME)
	var re_func := RegEx.create_from_string(_RE_FUNC)
	var re_super_bare := RegEx.create_from_string(_RE_SUPER_BARE)
	var re_super_method := RegEx.create_from_string(_RE_SUPER_METHOD)
	var re_takeover := RegEx.create_from_string(_RE_TAKEOVER)

	var current_func := ""
	var lines := source.split("\n")

	for line in lines:
		var hash_idx := _index_of_unescaped_hash(line)
		var code: String = line.substr(0, hash_idx) if hash_idx >= 0 else line

		if result["extends_path"] == "" and result["extends_class"] == "":
			var em := re_ext_path.search(code)
			if em != null:
				result["extends_path"] = em.get_string(1)
			else:
				em = re_ext_class.search(code)
				if em != null:
					result["extends_class"] = em.get_string(1)

		if result["class_name"] == "":
			var cm := re_class_name.search(code)
			if cm != null:
				result["class_name"] = cm.get_string(1)

		var fm := re_func.search(code)
		if fm != null:
			current_func = fm.get_string(1)
			if not result["functions"].has(current_func):
				result["functions"][current_func] = {
					"calls_super": false,
					"super_methods": [],
				}
			continue

		if current_func != "":
			if re_super_bare.search(code) != null:
				result["functions"][current_func]["calls_super"] = true
			var sm := re_super_method.search(code)
			if sm != null:
				result["functions"][current_func]["calls_super"] = true
				var method := sm.get_string(1)
				var arr: Array = result["functions"][current_func]["super_methods"]
				if method not in arr:
					arr.append(method)

		var tm := re_takeover.search(code)
		if tm != null:
			var p := tm.get_string(1)
			var arr: Array = result["take_over_paths"]
			if p not in arr:
				arr.append(p)

	return result


# Index of the first '#' in `line` that is not inside a quoted string.
# Returns -1 if none.
static func _index_of_unescaped_hash(line: String) -> int:
	var in_string := false
	var quote := ""
	var i := 0
	while i < line.length():
		var c := line.substr(i, 1)
		if in_string:
			if c == "\\":
				i += 2
				continue
			if c == quote:
				in_string = false
		else:
			if c == "\"" or c == "'":
				in_string = true
				quote = c
			elif c == "#":
				return i
		i += 1
	return -1
