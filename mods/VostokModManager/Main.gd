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
# Game UI commonly lives on layers 1..10. Crank ours well above any
# reasonable game layer so we render on top regardless of stack order.
const _CANVAS_LAYER := 1024

# We host the manager UI inside our own CanvasLayer so it renders on top
# of the game's menus rather than behind them.
var _layer: CanvasLayer = null


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
	print("[VMM] _toggle_ui called  (layer alive: %s)" % str(_layer != null and is_instance_valid(_layer)))
	if _layer != null and is_instance_valid(_layer):
		_layer.queue_free()
		_layer = null
		return
	var scene: PackedScene = load(_UI_SCENE_PATH)
	if scene == null:
		push_error("[VMM] could not load %s" % _UI_SCENE_PATH)
		return
	_layer = CanvasLayer.new()
	_layer.layer = _CANVAS_LAYER
	_layer.name = "VostokModManagerLayer"
	# Keep our reference in sync if the UI self-closes (Esc / backdrop
	# click frees the layer from inside).
	_layer.tree_exited.connect(_on_layer_freed)
	_layer.add_child(scene.instantiate())
	get_tree().root.add_child(_layer)
	print("[VMM] CanvasLayer added to root, layer=%d" % _CANVAS_LAYER)


func _on_layer_freed() -> void:
	_layer = null
