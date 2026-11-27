@tool
class_name BootButton
extends Control

signal pressed

const HOVER_DURATION_SEC := 0.09
const PRESS_DURATION_SEC := 0.06
const LABEL_PADDING_X := 12.0
const COLOR_SHIFT_AMOUNT := 0.10
const HOVER_ALPHA_BOOST := 0.06
const BOOST_EPSILON := 0.001
const DEFAULT_TEXT_COLOR := Color(0.949, 0.961, 0.980, 1.0)

@export var text := "":
	set(value):
		text = value
		if _label != null:
			_label.text = value
@export var font: Font:
	set(value):
		font = value
		if _label == null:
			return
		if value != null:
			_label.add_theme_font_override("font", value)
		else:
			_label.remove_theme_font_override("font")
@export var font_size := 22.0:
	set(value):
		font_size = value
		if _label != null:
			_label.add_theme_font_size_override("font_size", int(value))
@export var bg := Color(1.0, 1.0, 1.0, 0.18):
	set(value):
		bg = value
		queue_redraw()
@export var corner_radius := 8:
	set(value):
		corner_radius = value
		queue_redraw()

var _label: Label
var _box: StyleBoxFlat
var _tween: Tween
var _hover_boost := 0.0:
	set(value):
		_hover_boost = value
		queue_redraw()
var _is_hovered := false
var _is_pressed := false


func _init() -> void:
	mouse_filter = Control.MOUSE_FILTER_STOP
	mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND

	_box = StyleBoxFlat.new()
	_box.set_border_width_all(0)

	_label = Label.new()
	_label.text = text
	_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	_label.set_anchors_preset(Control.PRESET_FULL_RECT)
	_label.offset_left = LABEL_PADDING_X
	_label.offset_right = -LABEL_PADDING_X
	_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_label.add_theme_font_size_override("font_size", int(font_size))
	_label.add_theme_color_override("font_color", DEFAULT_TEXT_COLOR)
	add_child(_label, false, Node.INTERNAL_MODE_FRONT)


func _ready() -> void:
	mouse_entered.connect(_on_mouse_entered)
	mouse_exited.connect(_on_mouse_exited)


func _draw() -> void:
	if _box == null:
		return
	var color := bg
	if _hover_boost > BOOST_EPSILON:
		color = color.lightened(COLOR_SHIFT_AMOUNT * _hover_boost)
	elif _hover_boost < -BOOST_EPSILON:
		color = color.darkened(COLOR_SHIFT_AMOUNT * (-_hover_boost))
	var alpha := clampf(color.a + HOVER_ALPHA_BOOST * maxf(_hover_boost, 0.0), 0.0, 1.0)
	_box.bg_color = Color(color.r, color.g, color.b, alpha)
	_box.set_corner_radius_all(mini(corner_radius, int(minf(size.x, size.y) * 0.5)))
	draw_style_box(_box, Rect2(Vector2.ZERO, size))


func _gui_input(event: InputEvent) -> void:
	if Engine.is_editor_hint() or not (event is InputEventMouseButton):
		return
	var mouse_event := event as InputEventMouseButton
	if mouse_event.button_index != MOUSE_BUTTON_LEFT:
		return
	_is_pressed = mouse_event.pressed
	_animate_state(PRESS_DURATION_SEC)
	if not _is_pressed and Rect2(Vector2.ZERO, size).has_point(mouse_event.position):
		pressed.emit()


func _on_mouse_entered() -> void:
	if Engine.is_editor_hint():
		return
	_is_hovered = true
	_animate_state(HOVER_DURATION_SEC)


func _on_mouse_exited() -> void:
	if Engine.is_editor_hint():
		return
	_is_hovered = false
	_animate_state(HOVER_DURATION_SEC)


func _animate_state(duration: float) -> void:
	var target := -1.0 if _is_pressed else (1.0 if _is_hovered else 0.0)
	if _tween != null:
		_tween.kill()
	_tween = create_tween()
	_tween.tween_property(self, "_hover_boost", target, duration).set_trans(Tween.TRANS_QUAD).set_ease(Tween.EASE_OUT)
