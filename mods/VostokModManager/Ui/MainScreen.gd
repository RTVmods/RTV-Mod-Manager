extends Control

# Top-level Vostok Mod Manager UI. In dev (standalone Godot project) this
# scene is the main_scene. In-game, it's instantiated by the Main.gd
# autoload when the user presses F8.
#
# Builds the layout programmatically for now — a centered semi-opaque
# panel with status labels and two scrollable lists (mods, conflicts).
# Esc or clicking the dim background dismisses it.

const _DEFAULT_MODS_DIR := "C:/Program Files (x86)/Steam/steamapps/common/Road to Vostok/mods"

var _claude := VmmClaudeCodeRunner.new()
var _registry := VmmModRegistry.new()
var _resolver: VmmConflictResolver
var _mw_client: VmmModWorkshopClient

var _claude_label: Label
var _mods_label: Label
var _conflicts_label: Label
var _updates_label: Label
var _update_button: Button
var _mods_list: VBoxContainer
var _conflicts_list: VBoxContainer

# mw_id (int) -> latest version (str), populated by ModWorkshop check.
var _latest_versions: Dictionary = {}
var _pending_update_request: int = 0


func _ready() -> void:
	# Cover the screen with a dim backdrop. Click on the backdrop dismisses.
	var backdrop := ColorRect.new()
	backdrop.color = Color(0, 0, 0, 0.55)
	backdrop.anchor_right = 1.0
	backdrop.anchor_bottom = 1.0
	backdrop.mouse_filter = Control.MOUSE_FILTER_STOP
	backdrop.gui_input.connect(_on_backdrop_input)
	add_child(backdrop)

	# Centered panel with the actual content.
	var panel := PanelContainer.new()
	panel.anchor_left = 0.05
	panel.anchor_top = 0.05
	panel.anchor_right = 0.95
	panel.anchor_bottom = 0.95
	panel.mouse_filter = Control.MOUSE_FILTER_STOP
	add_child(panel)

	var root := VBoxContainer.new()
	root.add_theme_constant_override("separation", 8)
	panel.add_child(root)

	var header := HBoxContainer.new()
	root.add_child(header)
	var title := Label.new()
	title.text = "Vostok Mod Manager"
	title.add_theme_font_size_override("font_size", 22)
	title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	header.add_child(title)
	_update_button = Button.new()
	_update_button.text = "Check Updates"
	_update_button.pressed.connect(_check_updates)
	_update_button.disabled = true  # enabled after registry scan
	header.add_child(_update_button)
	var close_btn := Button.new()
	close_btn.text = "Close (Esc)"
	close_btn.pressed.connect(_close)
	header.add_child(close_btn)

	_claude_label = Label.new()
	root.add_child(_claude_label)
	_mods_label = Label.new()
	root.add_child(_mods_label)
	_updates_label = Label.new()
	root.add_child(_updates_label)
	_conflicts_label = Label.new()
	root.add_child(_conflicts_label)

	# ModWorkshop client must live in the scene tree (it spawns HTTPRequest
	# children). Add as a child of self so it's freed when we close.
	_mw_client = VmmModWorkshopClient.new()
	add_child(_mw_client)
	_mw_client.versions_ready.connect(_on_versions_ready)
	_mw_client.versions_failed.connect(_on_versions_failed)

	var split := HSplitContainer.new()
	split.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	root.add_child(split)
	_mods_list = _build_scroll_section(split, "Installed mods")
	_conflicts_list = _build_scroll_section(split, "Conflicts")

	set_process_input(true)
	await get_tree().process_frame
	_run_smoke()


func _input(event: InputEvent) -> void:
	if event is InputEventKey:
		var ek: InputEventKey = event
		if ek.pressed and ek.keycode == KEY_ESCAPE:
			_close()
			get_viewport().set_input_as_handled()


func _on_backdrop_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mb: InputEventMouseButton = event
		if mb.pressed and mb.button_index == MOUSE_BUTTON_LEFT:
			_close()


func _close() -> void:
	# We're hosted inside a CanvasLayer that the autoload created. Free
	# the whole layer so it goes away cleanly; the autoload listens on
	# tree_exited to drop its reference.
	var parent := get_parent()
	if parent != null and parent is CanvasLayer:
		parent.queue_free()
	else:
		queue_free()


func _build_scroll_section(parent: Container, header_text: String) -> VBoxContainer:
	var col := VBoxContainer.new()
	col.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	col.size_flags_vertical = Control.SIZE_EXPAND_FILL
	parent.add_child(col)

	var header := Label.new()
	header.text = header_text
	header.add_theme_font_size_override("font_size", 16)
	col.add_child(header)

	var scroll := ScrollContainer.new()
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	col.add_child(scroll)

	var list := VBoxContainer.new()
	list.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.add_child(list)
	return list


func _run_smoke() -> void:
	_claude.detect()
	_update_claude_status()

	_mods_label.text = "Mods: scanning %s ..." % _DEFAULT_MODS_DIR
	await get_tree().process_frame

	var err := _registry.scan(_DEFAULT_MODS_DIR)
	if err != OK:
		_mods_label.text = (
			"Mods: cannot read %s (err=%d). Check that the game is installed there."
			% [_DEFAULT_MODS_DIR, err]
		)
		return

	_update_mods_status()
	_populate_mods_list()
	_update_button.disabled = false

	_conflicts_label.text = "Conflicts: detecting (deep analysis) ..."
	await get_tree().process_frame

	var conflicts := VmmConflictDetector.detect_all(_registry.entries)
	_update_conflicts_status(conflicts)
	_populate_conflicts_list(conflicts)

	_resolver = VmmConflictResolver.new(_claude, _registry)

	# Auto-run a ModWorkshop update check on first open.
	_check_updates()


func _update_claude_status() -> void:
	if _claude.is_available():
		_claude_label.text = "Claude Code: ✓  %s   (%s)" % [
			_claude.get_version(),
			_claude.get_resolved_path(),
		]
	else:
		_claude_label.text = (
			"Claude Code: ✗ not found — AI conflict resolution disabled. "
			+ "Install from claude.com/claude-code or `npm i -g @anthropic-ai/claude-code`."
		)


func _update_mods_status() -> void:
	var total := _registry.entries.size()
	var enabled_count := _registry.enabled().size()
	_mods_label.text = (
		"Mods: %d found  (%d enabled, %d disabled)"
		% [total, enabled_count, total - enabled_count]
	)


func _populate_mods_list() -> void:
	# Clear any previously-rendered rows so the list reflects the current
	# state (used both on first render and when re-rendering after an
	# update check).
	for child in _mods_list.get_children():
		child.queue_free()
	for entry in _registry.entries:
		var row := Label.new()
		row.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		var status := "●" if entry.is_enabled else "○"
		var label_name := entry.display_name()
		if label_name == "":
			label_name = entry.path.get_file()
		var update_badge := _update_badge(entry)
		row.text = "%s  %s  v%s   %s   [%s]" % [
			status,
			label_name,
			entry.version(),
			update_badge,
			entry.mod_id(),
		]
		_mods_list.add_child(row)


# Returns a short status string for the right-hand "version status"
# column. Empty until we've fetched ModWorkshop data.
func _update_badge(entry: VmmModEntry) -> String:
	var mw := entry.modworkshop_id()
	if mw <= 0:
		return "(no ModWorkshop link)"
	if not _latest_versions.has(mw):
		return ""  # check not run yet
	var latest := str(_latest_versions[mw])
	if latest == "":
		return "(unknown)"
	if latest == entry.version():
		return "✓ current"
	return "⚠ %s available" % latest


# --- update check -------------------------------------------------------

func _check_updates() -> void:
	var mw_ids: Array = []
	for e in _registry.enabled():
		var mid := e.modworkshop_id()
		if mid > 0:
			mw_ids.append(mid)
	if mw_ids.is_empty():
		_updates_label.text = "Updates: no mods have a ModWorkshop link"
		return
	_update_button.disabled = true
	_updates_label.text = "Updates: checking %d mods on ModWorkshop ..." % mw_ids.size()
	_pending_update_request = _mw_client.check_versions(mw_ids)


func _on_versions_ready(request_id: int, versions: Dictionary) -> void:
	if request_id != _pending_update_request:
		return
	_pending_update_request = 0
	_update_button.disabled = false
	_latest_versions = versions

	var outdated := 0
	var unknown := 0
	var current := 0
	for e in _registry.enabled():
		var mw := e.modworkshop_id()
		if mw <= 0:
			continue
		if not versions.has(mw):
			unknown += 1
			continue
		if str(versions[mw]) == e.version():
			current += 1
		else:
			outdated += 1
	_updates_label.text = (
		"Updates: %d outdated, %d current, %d unknown"
		% [outdated, current, unknown]
	)
	_populate_mods_list()


func _on_versions_failed(request_id: int, error: String) -> void:
	if request_id != _pending_update_request:
		return
	_pending_update_request = 0
	_update_button.disabled = false
	_updates_label.text = "Updates: check failed — %s" % error


func _update_conflicts_status(conflicts: Array) -> void:
	if conflicts.is_empty():
		_conflicts_label.text = "Conflicts: none detected ✓"
		return
	var by_type: Dictionary = {}
	for c in conflicts:
		var t := str(c.get("type", "?"))
		by_type[t] = int(by_type.get(t, 0)) + 1
	var pieces: Array[String] = []
	for t in by_type:
		pieces.append("%d %s" % [by_type[t], t])
	_conflicts_label.text = "Conflicts: %s" % ", ".join(pieces)


func _populate_conflicts_list(conflicts: Array) -> void:
	for c in conflicts:
		var row := Label.new()
		row.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		row.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		var ids: Array = c.get("mod_ids", [])
		row.text = "[%s]  %s\n    mods: %s" % [
			c.get("type", "?"),
			c.get("key", "?"),
			", ".join(PackedStringArray(ids)),
		]
		_conflicts_list.add_child(row)
