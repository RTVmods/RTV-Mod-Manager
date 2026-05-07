extends Control

# Top-level Vostok Mod Manager UI. Instantiated by Main.gd autoload when
# the user presses F8.
#
# Builds the layout programmatically for now — a centered semi-opaque
# panel with status labels and two scrollable lists (mods, conflicts).
# Esc or clicking the dim background dismisses it.
#
# All cross-script references are via preload() rather than class_name
# globals — the game's ModLoader mounts our .vmz at runtime, so global
# class_name registration doesn't apply.

const _DEFAULT_MODS_DIR := "C:/Program Files (x86)/Steam/steamapps/common/Road to Vostok/mods"

const VmmClaudeCodeRunner = preload("res://mods/VostokModManager/Api/ClaudeCodeRunner.gd")
const VmmModRegistry = preload("res://mods/VostokModManager/Core/ModRegistry.gd")
const VmmModWorkshopClient = preload("res://mods/VostokModManager/Api/ModWorkshopClient.gd")
const VmmConflictDetector = preload("res://mods/VostokModManager/Core/ConflictDetector.gd")
const VmmConflictResolver = preload("res://mods/VostokModManager/Ai/ConflictResolver.gd")

var _claude := VmmClaudeCodeRunner.new()
var _registry := VmmModRegistry.new()
var _resolver  # VmmConflictResolver
var _mw_client # VmmModWorkshopClient

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
	# Force explicit sizing from the viewport rect. Control children of a
	# CanvasLayer don't always resolve anchor-based sizing reliably,
	# especially under canvas_items stretch mode (which Road to Vostok uses).
	# Setting position+size explicitly bypasses anchor inheritance entirely.
	var vp := get_viewport().get_visible_rect()
	print("[VMM] MainScreen _ready  viewport=%s" % str(vp.size))
	position = Vector2.ZERO
	size = vp.size

	var backdrop := ColorRect.new()
	backdrop.color = Color(0, 0, 0, 0.55)
	backdrop.position = Vector2.ZERO
	backdrop.size = vp.size
	backdrop.mouse_filter = Control.MOUSE_FILTER_STOP
	backdrop.gui_input.connect(_on_backdrop_input)
	add_child(backdrop)

	# Centered panel — explicit pos/size, not anchors. Custom stylebox so
	# the panel reads clearly against the game's already-dark menu.
	var panel := PanelContainer.new()
	panel.position = vp.size * 0.05
	panel.size = vp.size * 0.9
	panel.mouse_filter = Control.MOUSE_FILTER_STOP
	var stylebox := StyleBoxFlat.new()
	stylebox.bg_color = Color(0.10, 0.12, 0.16, 0.97)
	stylebox.set_border_width_all(2)
	stylebox.border_color = Color(0.45, 0.65, 0.85, 1.0)
	stylebox.set_corner_radius_all(6)
	stylebox.content_margin_left = 16
	stylebox.content_margin_right = 16
	stylebox.content_margin_top = 12
	stylebox.content_margin_bottom = 12
	panel.add_theme_stylebox_override("panel", stylebox)
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

	var footer := Label.new()
	footer.text = (
		"Note: enable/disable changes apply on next game launch — "
		+ "the game reads the mods folder once at startup."
	)
	footer.modulate = Color(0.75, 0.78, 0.85)
	root.add_child(footer)

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
	# update check or a toggle).
	for child in _mods_list.get_children():
		child.queue_free()
	for entry in _registry.entries:
		var row := HBoxContainer.new()
		row.size_flags_horizontal = Control.SIZE_EXPAND_FILL

		var toggle := Button.new()
		toggle.text = "Disable" if entry.is_enabled else "Enable"
		toggle.custom_minimum_size = Vector2(80, 0)
		toggle.pressed.connect(_toggle_mod.bind(entry))
		row.add_child(toggle)

		var lbl := Label.new()
		lbl.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		lbl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		var status := "●" if entry.is_enabled else "○"
		var label_name: String = entry.display_name()
		if label_name == "":
			label_name = entry.path.get_file()
		var update_badge := _update_badge(entry)
		lbl.text = "%s  %s  v%s   %s   [%s]" % [
			status,
			label_name,
			entry.version(),
			update_badge,
			entry.mod_id(),
		]
		row.add_child(lbl)

		_mods_list.add_child(row)


# Moves a mod between <mods>/ and <mods>/Disabled/. The game reads mods
# at startup, so the user has to relaunch for changes to take effect —
# that's surfaced in the footer label.
func _toggle_mod(entry) -> void:
	var src: String = entry.path
	var file_name: String = src.get_file()
	var dst: String
	if entry.is_enabled:
		var disabled_dir := _DEFAULT_MODS_DIR.path_join("Disabled")
		if not DirAccess.dir_exists_absolute(disabled_dir):
			DirAccess.make_dir_absolute(disabled_dir)
		dst = disabled_dir.path_join(file_name)
	else:
		dst = _DEFAULT_MODS_DIR.path_join(file_name)

	var err := DirAccess.rename_absolute(src, dst)
	if err != OK:
		push_error("[VMM] failed to move %s -> %s (err=%d)" % [src, dst, err])
		_updates_label.text = "Toggle failed (err=%d) — see Godot output" % err
		return

	# Rescan from disk and refresh the UI. ModWorkshop versions are kept
	# from the previous check (no need to re-hit the API on a local toggle).
	_registry.scan(_DEFAULT_MODS_DIR)
	_update_mods_status()
	_populate_mods_list()


# Returns a short status string for the right-hand "version status"
# column. Empty until we've fetched ModWorkshop data.
func _update_badge(entry) -> String:
	var mw: int = entry.modworkshop_id()
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
		var mid: int = e.modworkshop_id()
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
		var mw: int = e.modworkshop_id()
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
