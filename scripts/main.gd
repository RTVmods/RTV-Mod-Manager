extends Control

# Smoke-test entry point. Builds a status panel that exercises the core
# modules end-to-end so the engine can be verified before the proper UI
# is built. Replace once the real app shell lands.

const DEFAULT_MODS_DIR := "C:/Program Files (x86)/Steam/steamapps/common/Road to Vostok/mods"

var _claude := ClaudeCodeRunner.new()
var _registry := ModRegistry.new()
var _resolver: ConflictResolver

var _claude_status_label: Label
var _mods_status_label: Label
var _conflicts_status_label: Label
var _mods_list_box: VBoxContainer
var _conflicts_list_box: VBoxContainer


func _ready() -> void:
	_build_ui()

	_claude.detect()
	_update_claude_status()

	_mods_status_label.text = "Mods: scanning %s ..." % DEFAULT_MODS_DIR
	await get_tree().process_frame

	var err := _registry.scan(DEFAULT_MODS_DIR)
	if err != OK:
		_mods_status_label.text = (
			"Mods: cannot read %s (err=%d). Check that the game is installed there."
			% [DEFAULT_MODS_DIR, err]
		)
		return

	_update_mods_status()
	_populate_mods_list()

	_conflicts_status_label.text = "Conflicts: detecting (deep analysis) ..."
	await get_tree().process_frame

	var conflicts := ConflictDetector.detect_all(_registry.entries)
	_update_conflicts_status(conflicts)
	_populate_conflicts_list(conflicts)

	# Wire the resolver. It won't run unless we trigger it explicitly,
	# but having it constructed proves the dependency chain compiles.
	_resolver = ConflictResolver.new(_claude, _registry)


func _build_ui() -> void:
	var root := VBoxContainer.new()
	root.anchors_preset = Control.PRESET_FULL_RECT
	root.anchor_right = 1.0
	root.anchor_bottom = 1.0
	root.add_theme_constant_override("separation", 8)
	root.offset_left = 12
	root.offset_top = 12
	root.offset_right = -12
	root.offset_bottom = -12
	add_child(root)

	var title := Label.new()
	title.text = "Vostok Mod Manager"
	title.add_theme_font_size_override("font_size", 24)
	root.add_child(title)

	_claude_status_label = Label.new()
	root.add_child(_claude_status_label)
	_mods_status_label = Label.new()
	root.add_child(_mods_status_label)
	_conflicts_status_label = Label.new()
	root.add_child(_conflicts_status_label)

	var split := HSplitContainer.new()
	split.size_flags_vertical = Control.SIZE_EXPAND_FILL
	split.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	root.add_child(split)

	_mods_list_box = _build_scroll_section(split, "Installed mods")
	_conflicts_list_box = _build_scroll_section(split, "Conflicts")


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


func _update_claude_status() -> void:
	if _claude.is_available():
		_claude_status_label.text = "Claude Code: ✓  %s   (%s)" % [
			_claude.get_version(),
			_claude.get_resolved_path(),
		]
	else:
		_claude_status_label.text = (
			"Claude Code: ✗ not found — AI conflict resolution disabled. "
			+ "Install from claude.com/claude-code or `npm i -g @anthropic-ai/claude-code`."
		)


func _update_mods_status() -> void:
	var total := _registry.entries.size()
	var enabled_count := _registry.enabled().size()
	_mods_status_label.text = (
		"Mods: %d found  (%d enabled, %d disabled)"
		% [total, enabled_count, total - enabled_count]
	)


func _populate_mods_list() -> void:
	for entry in _registry.entries:
		var row := Label.new()
		row.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		var status := "●" if entry.is_enabled else "○"
		var label_name := entry.display_name()
		if label_name == "":
			label_name = entry.path.get_file()
		row.text = "%s  %s  v%s   [%s]   modworkshop=%d" % [
			status,
			label_name,
			entry.version(),
			entry.mod_id(),
			entry.modworkshop_id(),
		]
		_mods_list_box.add_child(row)


func _update_conflicts_status(conflicts: Array) -> void:
	if conflicts.is_empty():
		_conflicts_status_label.text = "Conflicts: none detected ✓"
		return
	var by_type: Dictionary = {}
	for c in conflicts:
		var t := str(c.get("type", "?"))
		by_type[t] = int(by_type.get(t, 0)) + 1
	var pieces: Array[String] = []
	for t in by_type:
		pieces.append("%d %s" % [by_type[t], t])
	_conflicts_status_label.text = "Conflicts: %s" % ", ".join(pieces)


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
		_conflicts_list_box.add_child(row)
