extends RefCounted

# Drives the user's locally-installed `claude` CLI as a subprocess to power
# AI-assisted conflict resolution. The user's existing Claude Code auth
# (Pro/Max subscription, API key, etc.) is reused — this app never sees
# credentials.
#
# Each request runs in a worker thread (OS.execute is blocking). The result
# arrives via the `request_completed` signal on the main thread.
#
# Tools are explicitly disabled (`--disallowed-tools "*"`) so the prompt
# can't trigger filesystem or shell side-effects.

signal request_completed(request_id: int, result: Dictionary)

var _claude_path: String = ""
var _available: bool = false
var _version: String = ""
var _next_request_id: int = 1
var _threads: Dictionary = {}  # id -> Thread

# Manual override path supplied by the user via Settings. Tried first in
# `detect()` before the built-in candidate list, so the user can point us
# at their actual claude binary when auto-detection fails.
var override_path: String = ""


func is_available() -> bool:
	return _available


func get_version() -> String:
	return _version


func get_resolved_path() -> String:
	return _claude_path


# Returns true if the user has Anthropic's Claude Desktop installed via
# Microsoft Store (MSIX). Detected by the presence of the per-package
# reparse point at %APPDATA%/Claude — that folder only exists when the
# MSIX package is registered.
#
# We check this when detection fails, so we can surface a clearer error:
# the MSIX install is sandboxed and its claude binary is not reachable
# from external processes (no execution alias, no PATH entry). The user
# needs the standalone CLI from npm.
func has_msix_install() -> bool:
	var appdata := OS.get_environment("APPDATA")
	if appdata == "":
		return false
	return DirAccess.dir_exists_absolute(appdata.path_join("Claude"))


# Probes for `claude` on PATH and at known install locations.
# Sets `is_available()` accordingly. Safe to call repeatedly.
func detect() -> void:
	_available = false
	_version = ""
	_claude_path = ""
	var paths: Array[String] = []
	if override_path != "":
		paths.append(override_path)
	for p in _candidate_paths():
		paths.append(p)
	print("[VMM] detect(): trying %d candidate path(s)" % paths.size())
	print("[VMM]   APPDATA=", OS.get_environment("APPDATA"))
	for candidate in paths:
		var exists := FileAccess.file_exists(candidate)
		var output: Array = []
		var exit := OS.execute(candidate, ["--version"], output, true)
		var head: String = ""
		if not output.is_empty():
			head = str(output[0]).substr(0, 80).replace("\n", " | ")
		print("[VMM]   try [%s] exists=%s exit=%d  out=%s" % [candidate, exists, exit, head])
		if exit == 0:
			_claude_path = candidate
			_version = _first_line(output)
			_available = true
			return


func _candidate_paths() -> Array[String]:
	var paths: Array[String] = ["claude"]
	var home := OS.get_environment("USERPROFILE")
	var appdata := OS.get_environment("APPDATA")
	var localappdata := OS.get_environment("LOCALAPPDATA")

	# Anthropic Claude Desktop installs the claude-code CLI at
	# %APPDATA%/Claude/claude-code/<version>/claude.exe — multiple
	# versions can coexist after upgrades, so we list the directory
	# and try the lex-newest first (works for typical SemVer; older
	# versions are tried as fallbacks if the newest isn't responsive).
	if appdata != "":
		var claude_code_root := appdata.path_join("Claude").path_join("claude-code")
		var versions := _list_subdirs(claude_code_root)
		versions.sort()
		versions.reverse()
		for v in versions:
			paths.append(claude_code_root.path_join(v).path_join("claude.exe"))

	# npm global install (rare on Windows but still seen)
	if appdata != "":
		paths.append(appdata.path_join("npm").path_join("claude.cmd"))
	if home != "":
		paths.append(home.path_join(".claude").path_join("local").path_join("claude.exe"))
		paths.append(home.path_join(".claude").path_join("local").path_join("claude.cmd"))
		paths.append(home.path_join(".claude").path_join("local").path_join("claude"))
	# Some package managers (scoop, winget) drop here
	if localappdata != "":
		paths.append(localappdata.path_join("Programs").path_join("claude").path_join("claude.exe"))
	return paths


# Lists immediate subdirectory names (no recursion). Returns [] if the
# directory doesn't exist or can't be opened.
func _list_subdirs(dir_path: String) -> Array[String]:
	var out: Array[String] = []
	var dir := DirAccess.open(dir_path)
	if dir == null:
		return out
	dir.list_dir_begin()
	while true:
		var name := dir.get_next()
		if name == "":
			break
		if name.begins_with("."):
			continue
		if dir.current_is_dir():
			out.append(name)
	dir.list_dir_end()
	return out


# Submits a prompt to Claude Code. Returns a request ID; `request_completed`
# fires with the result when the subprocess finishes.
#
# Result shape on success:
#   {"ok": true, "text": <string>, "cost_usd": <float>, "raw": <dict>}
# On failure:
#   {"ok": false, "error": <string>, "exit_code": <int>, "stdout": <string>}
func submit(prompt: String) -> int:
	var id := _next_request_id
	_next_request_id += 1
	if not _available:
		_emit_deferred(id, {
			"ok": false,
			"error": "Claude Code not available; call detect() first.",
		})
		return id
	var t := Thread.new()
	_threads[id] = t
	t.start(_run_subprocess.bind(id, prompt))
	return id


func _run_subprocess(request_id: int, prompt: String) -> void:
	var output: Array = []
	var args := [
		"-p", prompt,
		"--output-format", "json",
		"--disallowed-tools", "*",
	]
	var exit := OS.execute(_claude_path, args, output, true)
	var output_text: String = str(output[0]) if not output.is_empty() else ""
	var result: Dictionary
	if exit != 0:
		result = {
			"ok": false,
			"error": "claude exited with code %d" % exit,
			"exit_code": exit,
			"stdout": output_text,
		}
	else:
		var parsed: Variant = JSON.parse_string(output_text)
		if parsed == null or not (parsed is Dictionary):
			result = {
				"ok": false,
				"error": "could not parse Claude Code JSON output",
				"stdout": output_text,
			}
		else:
			var parsed_dict: Dictionary = parsed
			result = {
				"ok": true,
				"text": str(parsed_dict.get("result", "")),
				"cost_usd": float(parsed_dict.get("total_cost_usd", 0.0)),
				"raw": parsed_dict,
			}
	_emit_deferred(request_id, result)


func _emit_deferred(request_id: int, result: Dictionary) -> void:
	# Marshal back to the main thread.
	call_deferred("_emit_completed", request_id, result)


func _emit_completed(request_id: int, result: Dictionary) -> void:
	var t: Thread = _threads.get(request_id)
	if t != null:
		t.wait_to_finish()
		_threads.erase(request_id)
	request_completed.emit(request_id, result)


func _first_line(output: Array) -> String:
	if output.is_empty():
		return ""
	var text: String = str(output[0]).strip_edges()
	var newline_idx := text.find("\n")
	if newline_idx >= 0:
		return text.substr(0, newline_idx)
	return text
