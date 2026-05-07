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


func is_available() -> bool:
	return _available


func get_version() -> String:
	return _version


func get_resolved_path() -> String:
	return _claude_path


# Probes for `claude` on PATH and at known install locations.
# Sets `is_available()` accordingly. Safe to call repeatedly.
func detect() -> void:
	_available = false
	_version = ""
	_claude_path = ""
	for candidate in _candidate_paths():
		var output: Array = []
		var exit := OS.execute(candidate, ["--version"], output, true)
		if exit == 0:
			_claude_path = candidate
			_version = _first_line(output)
			_available = true
			return


func _candidate_paths() -> Array[String]:
	var paths: Array[String] = ["claude"]
	var home := OS.get_environment("USERPROFILE")
	if home != "":
		paths.append(home + "/.claude/local/claude.exe")
		paths.append(home + "/.claude/local/claude.cmd")
		paths.append(home + "/.claude/local/claude")
	var appdata := OS.get_environment("APPDATA")
	if appdata != "":
		paths.append(appdata + "/npm/claude.cmd")
	return paths


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
