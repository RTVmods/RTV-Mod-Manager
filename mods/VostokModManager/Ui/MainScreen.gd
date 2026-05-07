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
const VmmSettings = preload("res://mods/VostokModManager/Core/Settings.gd")

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
var _claude_path_input: LineEdit
var _decomp_path_input: LineEdit
var _setup_banner: Panel
var _setup_banner_label: Label
var _settings: Dictionary = VmmSettings.DEFAULTS.duplicate()
# request_id of an in-flight conflict resolution call.
var _pending_resolve_request: int = 0

# mw_id (int) -> latest version (str), populated by ModWorkshop check.
var _latest_versions: Dictionary = {}
var _pending_update_request: int = 0

# mod_id -> 1-based load-order position. Populated by _populate_mods_list
# and read by _populate_conflicts_list to mark super-chain constraints
# as satisfied (✓) or violated (⚠).
var _position_by_id: Dictionary = {}

# In-flight ModWorkshop download. We allow only one at a time for
# simplicity — the per-row Update buttons disable while one is running.
var _pending_download_request: int = 0
var _pending_download_target: String = ""    # final destination path
var _pending_download_temp: String = ""      # download-into path
var _pending_download_label: String = ""     # for status messages

# Last conflict set (cached so we can re-render after settings or
# resolver state changes without re-running the deep detector).
var _last_conflicts: Array = []


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

	# Setup banner: shown when Claude Code or Decomp aren't configured.
	# Empty/hidden when both are good. Built lazily in _refresh_setup_banner.
	_setup_banner = Panel.new()
	_setup_banner.visible = false
	var banner_style := StyleBoxFlat.new()
	banner_style.bg_color = Color(0.55, 0.40, 0.10, 0.65)
	banner_style.set_border_width_all(2)
	banner_style.border_color = Color(1.0, 0.75, 0.30, 1.0)
	banner_style.set_corner_radius_all(4)
	banner_style.content_margin_left = 12
	banner_style.content_margin_right = 12
	banner_style.content_margin_top = 8
	banner_style.content_margin_bottom = 8
	_setup_banner.add_theme_stylebox_override("panel", banner_style)
	root.add_child(_setup_banner)
	_setup_banner_label = Label.new()
	_setup_banner_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_setup_banner.add_child(_setup_banner_label)
	# Anchor the label to fill the panel so the stylebox padding is honored.
	_setup_banner_label.anchor_right = 1.0
	_setup_banner_label.anchor_bottom = 1.0
	_setup_banner.custom_minimum_size = Vector2(0, 60)

	_claude_label = Label.new()
	root.add_child(_claude_label)

	# Inline settings: Claude Code path + Decomp/ source path. These let
	# the user enable AI conflict resolution by pointing us at their
	# claude binary when our auto-detection misses it, and at the
	# decompiled game source so Claude has the original script as
	# context when comparing two mod overrides of the same file.
	_claude_path_input = _build_path_row(
		root,
		"  Claude Code path:",
		"(auto-detect — only fill if not found above)",
		_save_claude_path,
		_browse_claude_path,
	)
	_decomp_path_input = _build_path_row(
		root,
		"  Game source (Decomp/):",
		"path to the decompiled game source folder",
		_save_decomp_path,
		_browse_decomp_path,
		"How?",
		_show_decomp_help,
	)

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
	_mw_client.download_complete.connect(_on_download_complete)

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

	# Load persisted settings and apply them to the runner / inputs.
	_settings = VmmSettings.load_or_default()
	_claude.override_path = _settings["claude_path"]
	_claude_path_input.text = _settings["claude_path"]
	_decomp_path_input.text = _settings["game_source_path"]

	# Auto-detect Decomp on first run. Only applied if the user hasn't
	# already set a path — we never overwrite an explicit choice.
	if _settings["game_source_path"] == "":
		var detected := _autodetect_decomp_path()
		if detected != "":
			_settings["game_source_path"] = detected
			VmmSettings.save(_settings)
			_decomp_path_input.text = detected

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


func _build_path_row(
	parent: Container,
	label_text: String,
	placeholder: String,
	on_save: Callable,
	on_browse: Callable,
	extra_button_text: String = "",
	on_extra: Callable = Callable(),
) -> LineEdit:
	var row := HBoxContainer.new()
	row.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	parent.add_child(row)
	var lbl := Label.new()
	lbl.text = label_text
	lbl.custom_minimum_size = Vector2(220, 0)
	row.add_child(lbl)
	var input := LineEdit.new()
	input.placeholder_text = placeholder
	input.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(input)
	var browse_btn := Button.new()
	browse_btn.text = "Browse..."
	browse_btn.pressed.connect(on_browse)
	row.add_child(browse_btn)
	var save_btn := Button.new()
	save_btn.text = "Save"
	save_btn.pressed.connect(on_save)
	row.add_child(save_btn)
	if extra_button_text != "" and on_extra.is_valid():
		var extra_btn := Button.new()
		extra_btn.text = extra_button_text
		extra_btn.pressed.connect(on_extra)
		row.add_child(extra_btn)
	return input


func _save_claude_path() -> void:
	_settings["claude_path"] = _claude_path_input.text.strip_edges()
	VmmSettings.save(_settings)
	_claude.override_path = _settings["claude_path"]
	_claude.detect()
	_update_claude_status()
	_populate_conflicts_list(_last_conflicts)  # re-render so Resolve buttons enable


func _save_decomp_path() -> void:
	_settings["game_source_path"] = _decomp_path_input.text.strip_edges()
	VmmSettings.save(_settings)
	if _resolver != null:
		_resolver.game_source_path = _settings["game_source_path"]
	# A path saved-but-empty resets it; either way no UI change needed
	# beyond reflecting the current value back into the input.
	_decomp_path_input.text = _settings["game_source_path"]
	_refresh_setup_banner()


# --- Browse pickers -----------------------------------------------------

func _browse_claude_path() -> void:
	var dialog := FileDialog.new()
	dialog.access = FileDialog.ACCESS_FILESYSTEM
	dialog.file_mode = FileDialog.FILE_MODE_OPEN_FILE
	dialog.filters = PackedStringArray(["*.exe,*.cmd,*.bat ; Claude Code binary"])
	dialog.title = "Locate claude.exe"
	dialog.size = Vector2i(900, 600)
	dialog.file_selected.connect(func(path: String):
		_claude_path_input.text = path
		_save_claude_path()
		dialog.queue_free()
	)
	dialog.canceled.connect(dialog.queue_free)
	add_child(dialog)
	dialog.popup_centered_ratio(0.7)


func _browse_decomp_path() -> void:
	var dialog := FileDialog.new()
	dialog.access = FileDialog.ACCESS_FILESYSTEM
	dialog.file_mode = FileDialog.FILE_MODE_OPEN_DIR
	dialog.title = "Locate the decompiled game source folder"
	dialog.size = Vector2i(900, 600)
	dialog.dir_selected.connect(func(path: String):
		_decomp_path_input.text = path
		_save_decomp_path()
		dialog.queue_free()
	)
	dialog.canceled.connect(dialog.queue_free)
	add_child(dialog)
	dialog.popup_centered_ratio(0.7)


# Auto-detect a Decomp folder by checking a list of plausible locations.
# A folder counts as "the Decomp/" if it contains both Scripts/Loader.gd
# and Scripts/Interface.gd — landmark files we know the game ships.
# Returns the absolute path if found, "" otherwise.
func _autodetect_decomp_path() -> String:
	var candidates: Array[String] = []
	var home := OS.get_environment("USERPROFILE")
	var exe_path := OS.get_executable_path()
	var game_dir := exe_path.get_base_dir() if exe_path != "" else ""
	if game_dir != "":
		candidates.append(game_dir.path_join("Decomp"))
		candidates.append(game_dir.get_base_dir().path_join("Decomp"))
	if home != "":
		candidates.append(home.path_join("Documents").path_join("RoadToVostok_Decomp"))
		candidates.append(home.path_join("Desktop").path_join("RoadToVostok Dev").path_join("Decomp"))
		candidates.append(home.path_join("Desktop").path_join("Decomp"))
	for c in candidates:
		if _looks_like_decomp(c):
			return c
	return ""


func _looks_like_decomp(path: String) -> bool:
	if path == "" or not DirAccess.dir_exists_absolute(path):
		return false
	var landmarks: Array[String] = [
		path.path_join("Scripts").path_join("Loader.gd"),
		path.path_join("Scripts").path_join("Interface.gd"),
	]
	for f in landmarks:
		if not FileAccess.file_exists(f):
			return false
	return true


# --- Decomp how-to modal ------------------------------------------------

func _show_decomp_help() -> void:
	var dialog := Window.new()
	dialog.title = "How to set up the Decomp/ folder"
	dialog.size = Vector2i(820, 620)
	dialog.exclusive = false
	dialog.close_requested.connect(dialog.queue_free)

	var vbox := VBoxContainer.new()
	vbox.anchor_right = 1.0
	vbox.anchor_bottom = 1.0
	vbox.offset_left = 16
	vbox.offset_top = 16
	vbox.offset_right = -16
	vbox.offset_bottom = -16
	vbox.add_theme_constant_override("separation", 10)
	dialog.add_child(vbox)

	var intro := Label.new()
	intro.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	intro.text = (
		"The Decomp/ folder is the decompiled source of Road to Vostok. "
		+ "It's optional but strongly recommended: when two mods both override "
		+ "the same game script, the AI conflict resolver uses the original "
		+ "version from Decomp/ as context to produce a much better merge.\n\n"
		+ "It's a one-time setup. Once extracted, the Manager remembers the "
		+ "path."
	)
	vbox.add_child(intro)

	var steps_header := Label.new()
	steps_header.text = "Extracting it (one-time, ~5 minutes):"
	steps_header.add_theme_font_size_override("font_size", 16)
	vbox.add_child(steps_header)

	var steps := Label.new()
	steps.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	steps.text = (
		"1. Download gdre_tools (Godot RE Tools) from:\n"
		+ "       https://github.com/bruvzg/gdsdecomp/releases\n"
		+ "    Pick the latest Windows release (gdre_tools.exe).\n\n"
		+ "2. Run gdre_tools.exe. In the GUI, choose \"RE Tools\" >\n"
		+ "    \"Recover Project\" (or the equivalent extract option).\n\n"
		+ "3. Point it at your game pack:\n"
		+ "       %s\n\n"
		+ "4. Choose an output folder. Suggested:\n"
		+ "       %s\n\n"
		+ "5. Wait for extraction to finish (~5GB of output).\n\n"
		+ "6. Come back here and paste that output folder into\n"
		+ "    the \"Game source (Decomp/)\" input above. Or click\n"
		+ "    Browse... to pick it visually."
	) % [
		_guess_pck_path(),
		_guess_default_decomp_output(),
	]
	vbox.add_child(steps)

	var note := Label.new()
	note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	note.modulate = Color(0.75, 0.78, 0.85)
	note.text = (
		"Note: gdre_tools is a separate community tool, not part of the "
		+ "Mod Manager. We don't bundle it because it's a 50MB+ Godot "
		+ "executable in its own right and the user-data .pck is too "
		+ "large (~5GB) to extract in the background without a clear "
		+ "consent step. Once extracted, you only do this once."
	)
	vbox.add_child(note)

	var close_btn := Button.new()
	close_btn.text = "Close"
	close_btn.pressed.connect(dialog.queue_free)
	vbox.add_child(close_btn)

	add_child(dialog)
	dialog.popup_centered()


func _guess_pck_path() -> String:
	var exe := OS.get_executable_path()
	if exe == "":
		return "<game install>/RTV.pck"
	var dir := exe.get_base_dir()
	for name in ["RTV.pck", "Road to Vostok.pck"]:
		var p := dir.path_join(name)
		if FileAccess.file_exists(p):
			return p
	return dir.path_join("RTV.pck")


func _guess_default_decomp_output() -> String:
	var home := OS.get_environment("USERPROFILE")
	if home == "":
		return "<your-documents>/RoadToVostok_Decomp"
	return home.path_join("Documents").path_join("RoadToVostok_Decomp")


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

	_last_conflicts = VmmConflictDetector.detect_all(_registry.entries)
	_update_conflicts_status(_last_conflicts)

	_resolver = VmmConflictResolver.new(_claude, _registry)
	_resolver.game_source_path = _settings["game_source_path"]
	_resolver.resolution_ready.connect(_on_resolution_ready)

	_populate_conflicts_list(_last_conflicts)

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
			+ "Install from claude.com/claude-code, then click Browse... below."
		)
	_refresh_setup_banner()


# Banner shown above everything when one or both required paths are
# missing. Hidden once both are configured.
func _refresh_setup_banner() -> void:
	var msgs: Array[String] = []
	if not _claude.is_available():
		msgs.append(
			"• Claude Code not detected. Install from claude.com/claude-code, "
			+ "then click Browse... next to \"Claude Code path\" and pick "
			+ "claude.exe."
		)
	if _settings["game_source_path"] == "" or not _looks_like_decomp(_settings["game_source_path"]):
		msgs.append(
			"• Game source (Decomp/) not configured. Click \"How?\" next to "
			+ "the Decomp input below — extracting it once enables much "
			+ "better AI conflict resolution."
		)
	if msgs.is_empty():
		_setup_banner.visible = false
		return
	_setup_banner_label.text = "⚙ Setup needed:\n\n" + "\n\n".join(PackedStringArray(msgs))
	_setup_banner.custom_minimum_size = Vector2(0, 40 + 36 * msgs.size())
	_setup_banner.visible = true


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

	# Display order matches the game's load order: enabled mods first,
	# sorted by priority (lower first), tie-broken by filename. Disabled
	# mods follow, alphabetical. The numeric position is the load index.
	var sorted_entries: Array = _registry.entries.duplicate()
	sorted_entries.sort_custom(_sort_for_load_order)

	_position_by_id.clear()
	var position := 0
	for entry in sorted_entries:
		var row := HBoxContainer.new()
		row.size_flags_horizontal = Control.SIZE_EXPAND_FILL

		var toggle := Button.new()
		toggle.text = "Disable" if entry.is_enabled else "Enable"
		toggle.custom_minimum_size = Vector2(80, 0)
		toggle.disabled = _pending_download_request != 0
		toggle.pressed.connect(_toggle_mod.bind(entry))
		row.add_child(toggle)

		var update_btn := Button.new()
		update_btn.text = "Update"
		update_btn.custom_minimum_size = Vector2(80, 0)
		update_btn.disabled = (
			not _is_outdated(entry)
			or _pending_download_request != 0
		)
		update_btn.pressed.connect(_update_mod.bind(entry))
		row.add_child(update_btn)

		var lbl := Label.new()
		lbl.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		lbl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		var pos_str: String
		if entry.is_enabled:
			position += 1
			_position_by_id[entry.mod_id()] = position
			pos_str = "[%2d]" % position
		else:
			pos_str = "  · "
		var status := "●" if entry.is_enabled else "○"
		var label_name: String = entry.display_name()
		if label_name == "":
			label_name = entry.path.get_file()
		var update_badge := _update_badge(entry)
		var prio: int = entry.priority()
		lbl.text = "%s %s  %s  v%s   p=%d   %s   [%s]" % [
			pos_str,
			status,
			label_name,
			entry.version(),
			prio,
			update_badge,
			entry.mod_id(),
		]
		row.add_child(lbl)

		_mods_list.add_child(row)


# Comparator: enabled mods come first, then sort by priority ascending
# (matches what the game's modloader does), then by filename for ties.
# Disabled mods go below, sorted alphabetically.
func _sort_for_load_order(a, b) -> bool:
	if a.is_enabled != b.is_enabled:
		return a.is_enabled  # true (enabled) sorts before false (disabled)
	if a.is_enabled:
		if a.priority() != b.priority():
			return a.priority() < b.priority()
	var an: String = a.path.get_file().to_lower()
	var bn: String = b.path.get_file().to_lower()
	return an < bn


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
	# Re-run conflict detection — different enabled set means different
	# conflicts. Cheap (a few archive reads); fine to do per-click.
	_last_conflicts = VmmConflictDetector.detect_all(_registry.entries)
	_update_conflicts_status(_last_conflicts)
	_populate_conflicts_list(_last_conflicts)


# True if we have ModWorkshop data for this mod and its installed
# version differs from the latest reported version.
func _is_outdated(entry) -> bool:
	var mw: int = entry.modworkshop_id()
	if mw <= 0:
		return false
	if not _latest_versions.has(mw):
		return false
	var latest: String = str(_latest_versions[mw])
	if latest == "":
		return false
	return latest != entry.version()


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


# --- per-mod update download ---------------------------------------------

func _update_mod(entry) -> void:
	if _pending_download_request != 0:
		return
	var mw: int = entry.modworkshop_id()
	if mw <= 0:
		return

	# Download to a sibling .download file so a failure mid-transfer
	# doesn't trash the existing .vmz. We rename to the final path on
	# success.
	var final_path: String = entry.path
	var temp_path: String = final_path + ".download"
	var label: String = entry.display_name()
	if label == "":
		label = final_path.get_file()

	# Remove any leftover .download from a prior failed attempt.
	if FileAccess.file_exists(temp_path):
		DirAccess.remove_absolute(temp_path)

	_pending_download_target = final_path
	_pending_download_temp = temp_path
	_pending_download_label = label
	_pending_download_request = _mw_client.download_latest(mw, temp_path)
	_updates_label.text = "Downloading %s ..." % label
	# Re-render to disable buttons.
	_populate_mods_list()


func _on_download_complete(rid: int, save_path: String, error: String) -> void:
	if rid != _pending_download_request:
		return
	var label := _pending_download_label
	var final_path := _pending_download_target
	var temp_path := _pending_download_temp
	_pending_download_request = 0
	_pending_download_target = ""
	_pending_download_temp = ""
	_pending_download_label = ""

	if error != "":
		# Clean up partial download.
		if FileAccess.file_exists(save_path):
			DirAccess.remove_absolute(save_path)
		_updates_label.text = "Update failed for %s: %s" % [label, error]
		_populate_mods_list()
		return

	# Swap: remove old file, rename temp into place.
	if FileAccess.file_exists(final_path):
		var rm_err := DirAccess.remove_absolute(final_path)
		if rm_err != OK:
			# File is probably locked by the running game. Leave the
			# .download alongside and tell the user.
			_updates_label.text = (
				"Downloaded %s to %s but the existing file is locked "
				+ "(err=%d). Quit the game, then manually replace "
				+ "%s with the .download file."
			) % [label, temp_path, rm_err, final_path.get_file()]
			_populate_mods_list()
			return
	var rn_err := DirAccess.rename_absolute(temp_path, final_path)
	if rn_err != OK:
		_updates_label.text = (
			"Downloaded %s but could not rename %s -> %s (err=%d)."
			% [label, temp_path, final_path, rn_err]
		)
		_populate_mods_list()
		return

	_updates_label.text = (
		"Updated %s — restart game to load new version."
		% label
	)
	# Rescan + refresh badges. The new mod.txt should report the new
	# version, so the row's badge should flip from "⚠ X available" to
	# "✓ current" after rescan + a fresh /mods/versions check (already
	# in cache, so we can just refresh from _latest_versions).
	_registry.scan(_DEFAULT_MODS_DIR)
	_update_mods_status()
	_populate_mods_list()


# --- AI conflict resolution ---------------------------------------------

func _resolve_conflict(conflict: Dictionary) -> void:
	if _pending_resolve_request != 0:
		return
	if not _claude.is_available():
		return
	_pending_resolve_request = _resolver.resolve_file_overlap(conflict)
	_conflicts_label.text = (
		"Resolving %s with Claude Code ..." % str(conflict.get("key", ""))
	)
	# Re-render the conflicts list to disable all Resolve buttons.
	_populate_conflicts_list(_last_conflicts)


func _on_resolution_ready(verdict: Dictionary) -> void:
	_pending_resolve_request = 0
	_update_conflicts_status(_last_conflicts)
	_populate_conflicts_list(_last_conflicts)
	_show_resolution_dialog(verdict)


# Builds a popup window showing Claude's verdict for a file_overlap.
# Free-form: a header, the reason, and either the merged source (read-only
# TextEdit, copy-able) or the suggested load order. v2 will add Apply
# buttons; for now this is read-only — the user reviews and patches their
# mods themselves based on what Claude suggests.
func _show_resolution_dialog(verdict: Dictionary) -> void:
	var dialog := Window.new()
	var conflict_key: String = str(verdict.get("conflict_key", ""))
	dialog.title = "Conflict resolution: %s" % conflict_key
	dialog.size = Vector2i(960, 640)
	dialog.exclusive = false
	dialog.close_requested.connect(dialog.queue_free)

	var vbox := VBoxContainer.new()
	vbox.anchor_right = 1.0
	vbox.anchor_bottom = 1.0
	vbox.offset_left = 12
	vbox.offset_top = 12
	vbox.offset_right = -12
	vbox.offset_bottom = -12
	vbox.add_theme_constant_override("separation", 8)
	dialog.add_child(vbox)

	var ok: bool = bool(verdict.get("ok", false))
	if not ok:
		var err_lbl := Label.new()
		err_lbl.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		err_lbl.text = "Error: %s" % str(verdict.get("error", "unknown"))
		vbox.add_child(err_lbl)
		var raw: String = str(verdict.get("raw_text", ""))
		if raw != "":
			var raw_lbl := Label.new()
			raw_lbl.text = "Claude's raw response:"
			vbox.add_child(raw_lbl)
			var raw_edit := TextEdit.new()
			raw_edit.text = raw
			raw_edit.editable = false
			raw_edit.size_flags_vertical = Control.SIZE_EXPAND_FILL
			raw_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			vbox.add_child(raw_edit)
	else:
		var v: String = str(verdict.get("verdict", ""))
		var icon := "?"
		match v:
			"merge_safe":
				icon = "✓"
			"order_resolves":
				icon = "⚠"
			"incompatible":
				icon = "✗"
		var verdict_lbl := Label.new()
		verdict_lbl.text = "%s  %s" % [icon, v]
		verdict_lbl.add_theme_font_size_override("font_size", 18)
		vbox.add_child(verdict_lbl)

		var reason_lbl := Label.new()
		reason_lbl.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		reason_lbl.text = str(verdict.get("reason", ""))
		vbox.add_child(reason_lbl)

		if v == "merge_safe":
			var hdr := Label.new()
			hdr.text = "Proposed merged source (read-only — copy and review before applying):"
			vbox.add_child(hdr)
			var src_edit := TextEdit.new()
			src_edit.text = str(verdict.get("merged_source", ""))
			src_edit.editable = false
			src_edit.size_flags_vertical = Control.SIZE_EXPAND_FILL
			src_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
			vbox.add_child(src_edit)
		elif v == "order_resolves":
			var order_lbl := Label.new()
			order_lbl.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
			var order: Array = verdict.get("load_order", [])
			order_lbl.text = "Suggested load order (earlier first):\n  " + "\n  ".join(
				PackedStringArray(order)
			)
			vbox.add_child(order_lbl)

		var cost_lbl := Label.new()
		cost_lbl.text = "Cost: $%.4f" % float(verdict.get("cost_usd", 0.0))
		cost_lbl.modulate = Color(0.7, 0.72, 0.78)
		vbox.add_child(cost_lbl)

	var close_btn := Button.new()
	close_btn.text = "Close"
	close_btn.pressed.connect(dialog.queue_free)
	vbox.add_child(close_btn)

	add_child(dialog)
	dialog.popup_centered()


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
	for child in _conflicts_list.get_children():
		child.queue_free()
	for c in conflicts:
		var t: String = str(c.get("type", "?"))
		var ids: Array = c.get("mod_ids", [])

		var row := HBoxContainer.new()
		row.size_flags_horizontal = Control.SIZE_EXPAND_FILL

		# Per-row Resolve button — only for file_overlap conflicts on
		# .gd files (the only thing the AI resolver currently knows
		# how to merge / order). Disabled if Claude Code isn't reachable.
		if t == "file_overlap":
			var details: Dictionary = c.get("details", {})
			var resolve_btn := Button.new()
			resolve_btn.text = "Resolve"
			resolve_btn.custom_minimum_size = Vector2(80, 0)
			var is_gd: bool = bool(details.get("is_gdscript", false))
			resolve_btn.disabled = (
				not _claude.is_available()
				or _pending_resolve_request != 0
				or not is_gd
			)
			if not is_gd:
				resolve_btn.tooltip_text = (
					"Resolve only handles .gd file conflicts for now."
				)
			elif not _claude.is_available():
				resolve_btn.tooltip_text = (
					"Claude Code not detected. Set the path in Settings."
				)
			resolve_btn.pressed.connect(_resolve_conflict.bind(c))
			row.add_child(resolve_btn)
		else:
			# Spacer to keep label columns aligned across rows.
			var spacer := Control.new()
			spacer.custom_minimum_size = Vector2(80, 0)
			row.add_child(spacer)

		var lbl := Label.new()
		lbl.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		lbl.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		var prefix := ""
		if t == "super_chain_constraint":
			var details: Dictionary = c.get("details", {})
			var before: String = str(details.get("before", ""))
			var after: String = str(details.get("after", ""))
			if _position_by_id.has(before) and _position_by_id.has(after):
				if int(_position_by_id[before]) < int(_position_by_id[after]):
					prefix = "✓ "  # current order satisfies the constraint
				else:
					prefix = "⚠ "  # current order violates: `before` loads after `after`
			else:
				prefix = "·  "  # constraint involves a disabled mod
		lbl.text = "%s[%s]  %s\n    mods: %s" % [
			prefix,
			t,
			c.get("key", "?"),
			", ".join(PackedStringArray(ids)),
		]
		row.add_child(lbl)

		_conflicts_list.add_child(row)
