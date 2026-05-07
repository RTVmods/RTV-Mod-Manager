class_name VmmModWorkshopClient
extends Node

# Minimal HTTP client for the public ModWorkshop API.
# Currently exposes the batch version-check endpoint (/mods/versions),
# which is the workhorse for "is any of my installed mods outdated?".
#
# Quirk: /mods/versions is a GET with a JSON request body. POST 405s.
#
# Auth: none required for read endpoints. The site's terms forbid
# spamming the API and replicating the site, so we only call when the
# user explicitly asks (open manager / refresh button).
#
# Usage:
#   var client := VmmModWorkshopClient.new()
#   add_child(client)
#   client.versions_ready.connect(my_callback)
#   var rid := client.check_versions([56398, 55984, 56156])
#   # ... my_callback(rid, {56398: "0.6.1", 55984: "2.2.4", ...})

signal versions_ready(request_id: int, versions: Dictionary)
signal versions_failed(request_id: int, error: String)

const _BASE_URL := "https://api.modworkshop.net"
const _USER_AGENT := "VostokModManager/0.1.0 (+https://modworkshop.net/g/roadtovostok)"
const _BATCH_LIMIT := 100  # documented per-request cap

var _next_request_id: int = 1


# Submits a batch version check. Returns a request_id; result fires later
# via the `versions_ready` or `versions_failed` signal.
#
# If `mod_ids` exceeds 100, only the first 100 are queried for now.
# (Multi-batch chaining is a TODO once we hit a real-world need for it.)
func check_versions(mod_ids: Array) -> int:
	var rid := _next_request_id
	_next_request_id += 1

	var ints: Array = []
	for m in mod_ids:
		var v := int(m)
		if v > 0:
			ints.append(v)
	if ints.is_empty():
		call_deferred("emit_signal", "versions_ready", rid, {})
		return rid
	if ints.size() > _BATCH_LIMIT:
		push_warning(
			"VmmModWorkshopClient: %d mods exceeds batch limit of %d; only the first %d will be queried"
			% [ints.size(), _BATCH_LIMIT, _BATCH_LIMIT]
		)
		ints = ints.slice(0, _BATCH_LIMIT)

	var http := HTTPRequest.new()
	add_child(http)
	http.request_completed.connect(
		_on_request_completed.bind(rid, http)
	)

	var headers := PackedStringArray([
		"Content-Type: application/json",
		"Accept: application/json",
		"User-Agent: " + _USER_AGENT,
	])
	var body := JSON.stringify({"mod_ids": ints})
	var err := http.request(
		_BASE_URL + "/mods/versions",
		headers,
		HTTPClient.METHOD_GET,
		body,
	)
	if err != OK:
		http.queue_free()
		call_deferred(
			"emit_signal", "versions_failed", rid,
			"HTTPRequest.request returned %d" % err
		)
	return rid


func _on_request_completed(
	result: int,
	response_code: int,
	_headers: PackedStringArray,
	body: PackedByteArray,
	request_id: int,
	http: HTTPRequest,
) -> void:
	http.queue_free()

	if result != HTTPRequest.RESULT_SUCCESS:
		versions_failed.emit(request_id, "transport error (result=%d)" % result)
		return
	if response_code < 200 or response_code >= 300:
		versions_failed.emit(request_id, "HTTP %d" % response_code)
		return

	var text := body.get_string_from_utf8()
	var parsed: Variant = JSON.parse_string(text)
	if not (parsed is Dictionary):
		var preview := text.substr(0, 200)
		versions_failed.emit(
			request_id,
			"response was not a JSON object: %s" % preview
		)
		return

	# Normalize keys to int. The API returns string keys (JSON object).
	var d: Dictionary = parsed
	var out: Dictionary = {}
	for k in d:
		out[int(k)] = str(d[k])
	versions_ready.emit(request_id, out)
