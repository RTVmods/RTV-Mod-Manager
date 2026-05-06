extends Node

# Vostok Mod Manager — autoload entry point used when this mod is loaded
# inside Road to Vostok. Registers F8 as the toggle key for the in-game
# manager UI.
#
# In standalone Godot dev (running this repo as a Godot project), the
# manager is launched directly as the main scene, so this autoload is
# unused there. Same scripts; different entry path.

const _UI_SCENE_PATH := "res://mods/VostokModManager/Ui/MainScreen.tscn"
const _TOGGLE_KEY := KEY_F8

var _ui: Control = null


func _ready() -> void:
	print("[VMM] Vostok Mod Manager loaded — press F8 to open")


func _unhandled_input(event: InputEvent) -> void:
	if not (event is InputEventKey):
		return
	var ek: InputEventKey = event
	if not ek.pressed or ek.echo:
		return
	if ek.keycode == _TOGGLE_KEY:
		_toggle_ui()
		get_viewport().set_input_as_handled()


func _toggle_ui() -> void:
	if _ui != null and is_instance_valid(_ui):
		_ui.queue_free()
		_ui = null
		return
	var scene: PackedScene = load(_UI_SCENE_PATH)
	if scene == null:
		push_error("[VMM] could not load %s" % _UI_SCENE_PATH)
		return
	_ui = scene.instantiate()
	get_tree().root.add_child(_ui)
