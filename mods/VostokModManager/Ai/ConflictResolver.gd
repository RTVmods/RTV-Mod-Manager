extends RefCounted

# AI-driven resolver for file_overlap conflicts on .gd files. Builds a
# structured prompt (game source + each mod's version) and submits it via
# the Claude Code runner. Parses Claude's JSON verdict back into a Dictionary.

const VmmClaudeCodeRunner = preload("res://mods/VostokModManager/Api/ClaudeCodeRunner.gd")
const VmmModRegistry = preload("res://mods/VostokModManager/Core/ModRegistry.gd")
#
# Verdict shape returned via `resolution_ready`:
#   {
#     "ok":            bool,
#     "conflict_key":  String,
#     "verdict":       "merge_safe" | "order_resolves" | "incompatible",
#     "reason":        String,
#     "merged_source": String,         # populated for merge_safe
#     "load_order":    Array[String],  # populated for order_resolves
#     "cost_usd":      float,
#     "raw_text":      String,         # Claude's raw response, for debugging
#   }
# On failure: {"ok": false, "conflict_key": ..., "error": ...}

signal resolution_ready(verdict: Dictionary)

var runner   # VmmClaudeCodeRunner
var registry # VmmModRegistry

# Optional. When set, the resolver will look up the original game script
# at `<game_source_path>/<rel>` (where `rel` is the conflicting res://
# path with the `res://` prefix stripped) and include it as context for
# Claude. If unset or the file is missing, Claude works with just the
# competing mod versions.
var game_source_path: String = ""

# request_id -> conflict_key
var _pending: Dictionary = {}


func _init(p_runner, p_registry) -> void:
	runner = p_runner
	registry = p_registry
	runner.request_completed.connect(_on_runner_completed)


# Submits a file_overlap conflict for AI resolution. Returns the request
# ID; result arrives later via `resolution_ready`.
func resolve_file_overlap(conflict: Dictionary) -> int:
	var conflict_key := str(conflict.get("key", ""))
	var mod_ids: Array = conflict.get("mod_ids", [])
	var prompt := _build_prompt(conflict_key, mod_ids)
	var rid := runner.submit(prompt)
	_pending[rid] = conflict_key
	return rid


# --- internals -----------------------------------------------------------

func _build_prompt(file_path: String, mod_ids: Array) -> String:
	var parts: Array[String] = []
	parts.append("# Road to Vostok mod conflict resolution")
	parts.append("")
	parts.append(
		"Two or more mods write to the same file path: `%s`." % file_path
	)
	parts.append(
		"Analyze whether the changes can be merged safely, "
		+ "whether one load order resolves the issue, "
		+ "or whether the mods are genuinely incompatible."
	)
	parts.append("")
	parts.append("Respond with strict JSON only — no prose outside the JSON.")
	parts.append("Schema:")
	parts.append("```json")
	parts.append("{")
	parts.append('  "verdict": "merge_safe" | "order_resolves" | "incompatible",')
	parts.append('  "reason": "<one-paragraph explanation for the user>",')
	parts.append('  "merged_source": "<full merged GDScript source, or empty>",')
	parts.append('  "load_order": ["<mod_id_first>", "<mod_id_second>", ...]')
	parts.append("}")
	parts.append("```")
	parts.append("")
	parts.append(
		"- `merge_safe`: changes are orthogonal; provide the merged source "
		+ "in `merged_source`."
	)
	parts.append(
		"- `order_resolves`: one strict load order avoids the override "
		+ "collision (e.g. one mod is a superset); list it in `load_order` "
		+ "(earlier first)."
	)
	parts.append(
		"- `incompatible`: the mods make incompatible changes; explain in "
		+ "`reason`."
	)
	parts.append("")

	# Original game script context, if available.
	var orig_text := _read_original(file_path)
	if orig_text != "":
		parts.append("## Original game script (`%s`)" % file_path)
		parts.append("```gdscript")
		parts.append(orig_text)
		parts.append("```")
		parts.append("")
	else:
		parts.append(
			"_(No original game script available for this path; "
			+ "analyze based on the mod versions alone.)_"
		)
		parts.append("")

	# Each mod's version of the contested file.
	for mid in mod_ids:
		var entry := registry.find_by_id(str(mid))
		if entry == null:
			continue
		parts.append("## Mod `%s` (%s, v%s)" % [
			entry.mod_id(),
			entry.display_name(),
			entry.version(),
		])
		var desc := entry.description()
		if desc != "":
			parts.append("Description: %s" % desc)
		parts.append("Version of `%s`:" % file_path)
		parts.append("```gdscript")
		parts.append(entry.read_file_text(file_path))
		parts.append("```")
		parts.append("")

	return "\n".join(parts)


# Translates a `res://` path to a filesystem path under `game_source_path`
# and reads it. Returns "" if game_source_path is unset, malformed, or the
# file doesn't exist.
func _read_original(res_path: String) -> String:
	if game_source_path == "":
		return ""
	if not res_path.begins_with("res://"):
		return ""
	var rel := res_path.substr("res://".length())
	var full := game_source_path.path_join(rel)
	if not FileAccess.file_exists(full):
		return ""
	var f := FileAccess.open(full, FileAccess.READ)
	if f == null:
		return ""
	var text := f.get_as_text()
	f.close()
	return text


func _on_runner_completed(request_id: int, result: Dictionary) -> void:
	if not _pending.has(request_id):
		return  # not ours
	var conflict_key := str(_pending[request_id])
	_pending.erase(request_id)

	if not bool(result.get("ok", false)):
		resolution_ready.emit({
			"ok": false,
			"conflict_key": conflict_key,
			"error": str(result.get("error", "unknown")),
		})
		return

	var raw_text := str(result.get("text", ""))
	var verdict := _parse_verdict_json(raw_text)
	if verdict.is_empty():
		resolution_ready.emit({
			"ok": false,
			"conflict_key": conflict_key,
			"error": "could not parse Claude's verdict as JSON",
			"raw_text": raw_text,
		})
		return

	verdict["ok"] = true
	verdict["conflict_key"] = conflict_key
	verdict["cost_usd"] = float(result.get("cost_usd", 0.0))
	verdict["raw_text"] = raw_text
	resolution_ready.emit(verdict)


# Tries direct JSON parse first, then falls back to extracting the first
# JSON object inside a markdown code fence.
func _parse_verdict_json(text: String) -> Dictionary:
	var trimmed := text.strip_edges()
	var p: Variant = JSON.parse_string(trimmed)
	if p is Dictionary:
		return p
	var fence_re := RegEx.create_from_string(
		"```(?:json)?\\s*(\\{[\\s\\S]*?\\})\\s*```"
	)
	var m := fence_re.search(text)
	if m != null:
		p = JSON.parse_string(m.get_string(1))
		if p is Dictionary:
			return p
	# Last resort: find the first '{' and try to parse from there.
	var brace_idx := text.find("{")
	if brace_idx >= 0:
		p = JSON.parse_string(text.substr(brace_idx))
		if p is Dictionary:
			return p
	return {}
