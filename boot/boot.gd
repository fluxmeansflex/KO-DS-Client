extends Control

const PACK_STATUS := {
	"assets_core": "Downloading update...",
	"assets_maps": "Downloading maps...",
	"assets_skins": "Downloading weapons & skins...",
	"game": "Downloading game update...",
}
const BOOT_MAX_FPS := 30
const STATUS_MAX_WIDTH := 620.0
const STATUS_WIDTH_PADDING := 12.0
const STATUS_FONT_SIZE := 32
const ERROR_FONT_SIZE := 24
const SPINNER_SPEED := 4.8
const MAX_EXTRACT_DOTS := 3
const NATIVE_EXTENSION_PATH := "res://native/ko_native/ko_native.gdextension"
const GAME_THEME_PATH := "res://ui/theme/deadshot_theme.tres"
const OFFLINE_LAUNCH_DELAY_SEC := 0.6

@onready var _version_label: Label = %VersionLabel
@onready var _error_icon: TextureRect = %ErrorIcon
@onready var _status_label: Label = %StatusLabel
@onready var _spinner: TextureRect = %SpinnerRect
@onready var _progress_block: VBoxContainer = %ProgressBlock
@onready var _progress_bar: ProgressBar = %ProgressBar
@onready var _detail_label: Label = %DetailLabel
@onready var _speed_label: Label = %SpeedLabel
@onready var _actions_box: HBoxContainer = %Actions
@onready var _offline_btn: BootButton = %OfflineBtn
@onready var _retry_btn: BootButton = %RetryBtn
@onready var _close_btn: BootButton = %CloseBtn
@onready var _footer_label: Label = %FooterLabel
@onready var _updater: PackUpdater = %PackUpdater
@onready var _extract_timer: Timer = %ExtractPulseTimer

var _display_version := ""
var _extract_dots := 0
var _last_status_message := ""
var _last_status_variation: StringName = &""
var _prev_max_fps := 0
var _prev_low_processor_mode := false


func _ready() -> void:
	if _try_software_renderer_fallback():
		return

	var running_version := str(ProjectSettings.get_setting("application/config/version", "1.0.0"))
	_display_version = running_version
	_footer_label.text = _format_footer(running_version)

	if OS.has_feature("editor"):
		set_process(false)
		_enter_game.call_deferred()
		return

	_apply_boot_power_profile()
	_display_version = _updater.initialize(running_version)
	_version_label.text = "v" + _display_version
	_footer_label.text = _format_footer(_display_version)

	_connect_signals()
	_updater.start_check()


func _process(delta: float) -> void:
	_spinner.offset_transform_rotation = fmod(_spinner.offset_transform_rotation + delta * SPINNER_SPEED, TAU)


func _connect_signals() -> void:
	_retry_btn.pressed.connect(_updater.start_check)
	_offline_btn.pressed.connect(_enter_game)
	_close_btn.pressed.connect(func() -> void: get_tree().quit(0))
	_extract_timer.timeout.connect(_on_extract_pulse_timeout)

	_updater.check_started.connect(_on_check_started)
	_updater.version_detected.connect(_on_version_detected)
	_updater.pack_download_started.connect(_on_pack_download_started)
	_updater.download_progress_changed.connect(_on_download_progress_changed)
	_updater.extract_started.connect(_on_extract_started)
	_updater.update_completed.connect(_on_update_completed)
	_updater.offline_ready.connect(_on_offline_ready)
	_updater.update_failed.connect(_on_update_failed)


func _apply_boot_power_profile() -> void:
	if DisplayServer.get_name() == "headless":
		return
	_prev_max_fps = Engine.max_fps
	_prev_low_processor_mode = OS.low_processor_usage_mode
	Engine.max_fps = BOOT_MAX_FPS
	OS.low_processor_usage_mode = true


func _restore_power_profile() -> void:
	if DisplayServer.get_name() == "headless":
		return
	Engine.max_fps = _prev_max_fps
	OS.low_processor_usage_mode = _prev_low_processor_mode


func _try_software_renderer_fallback() -> bool:
	if OS.has_feature("editor") or DisplayServer.get_name() == "headless":
		return false
	if RenderingServer.get_current_rendering_method() == "gl_compatibility":
		return false
	var adapter := RenderingServer.get_video_adapter_name()
	var is_software := (
		RenderingServer.get_video_adapter_type() == RenderingDevice.DEVICE_TYPE_CPU
		or adapter.to_lower().contains("basic render driver")
	)
	if not is_software:
		return false
	OS.set_restart_on_exit(true, PackedStringArray(["--rendering-method", "gl_compatibility"]))
	get_tree().quit(0)
	return true


func _on_check_started() -> void:
	_extract_timer.stop()
	_actions_box.visible = false
	_error_icon.visible = false
	_set_spinner_active(true)
	_progress_block.visible = false
	_show_status("Checking for updates...")
	_detail_label.text = ""
	_speed_label.text = ""
	_footer_label.text = _format_footer()
	_progress_bar.value = 0.0


func _on_version_detected(version: String) -> void:
	_display_version = version
	_version_label.text = "v" + version


func _on_pack_download_started(pack_id: String, shown_pct: float, step_caption: String) -> void:
	_extract_timer.stop()
	_error_icon.visible = false
	_set_spinner_active(true)
	_progress_block.visible = true
	_show_status(str(PACK_STATUS.get(pack_id, "Downloading update...")))
	_detail_label.text = step_caption
	_speed_label.text = "%.0f%%" % shown_pct
	_footer_label.text = _format_footer(_display_version)
	_progress_bar.value = shown_pct


func _on_download_progress_changed(shown_pct: float, step_caption: String) -> void:
	_progress_bar.value = shown_pct
	if not step_caption.is_empty():
		_detail_label.text = step_caption
		_speed_label.text = "%d%%" % int(floor(shown_pct))


func _on_extract_started(shown_pct: float) -> void:
	_progress_bar.value = shown_pct
	_speed_label.text = "%d%%" % int(floor(shown_pct))
	_show_status("Installing update...")
	_extract_dots = 1
	_detail_label.text = "Installing."
	_extract_timer.start()


func _on_extract_pulse_timeout() -> void:
	_extract_dots = (_extract_dots % MAX_EXTRACT_DOTS) + 1
	_detail_label.text = "Installing" + ".".repeat(_extract_dots)


func _on_update_completed(needs_restart: bool, version: String) -> void:
	_extract_timer.stop()
	_progress_bar.value = 100.0
	_speed_label.text = ""
	_progress_block.visible = false

	if needs_restart:
		_show_status("Installing update...")
		_footer_label.text = "Restarting KO Client..."
		OS.set_restart_on_exit(true)
		get_tree().quit(0)
		return

	_show_status("Starting game...")
	_detail_label.text = ""
	_footer_label.text = _format_footer(version)
	_enter_game.call_deferred()


func _on_offline_ready(version: String) -> void:
	_extract_timer.stop()
	_progress_block.visible = false
	_show_status("Starting in offline mode...")
	_footer_label.text = _format_footer(version)
	_progress_bar.value = 100.0
	get_tree().create_timer(OFFLINE_LAUNCH_DELAY_SEC).timeout.connect(_enter_game)


func _on_update_failed(message: String, can_offline: bool) -> void:
	_extract_timer.stop()
	_set_spinner_active(false)
	_error_icon.visible = true
	_progress_block.visible = false
	_show_error_status(message)
	_detail_label.text = ""
	_speed_label.text = ""
	_footer_label.text = _format_footer()
	_offline_btn.visible = can_offline
	_actions_box.visible = true


func _enter_game() -> void:
	if not OS.has_feature("editor"):
		_restore_power_profile()
		if not _updater.mount_local_packs():
			push_error("[KO-Updater] Failed to mount data/game.pck")
			_on_update_failed("Game files are missing or corrupted. Please click Retry.", false)
			return
		_load_native_extension()
		_reload_game_theme()
	var err := get_tree().change_scene_to_file("res://main.tscn")
	if err != OK:
		push_error("[KO-Updater] Failed to start game scene (err %d)" % err)
		_on_update_failed("Could not start game. Please try again.", false)


func _load_native_extension() -> void:
	if not FileAccess.file_exists(NATIVE_EXTENSION_PATH):
		return
	if GDExtensionManager.is_extension_loaded(NATIVE_EXTENSION_PATH):
		return
	var status := GDExtensionManager.load_extension(NATIVE_EXTENSION_PATH)
	if status != GDExtensionManager.LOAD_STATUS_OK and status != GDExtensionManager.LOAD_STATUS_ALREADY_LOADED:
		push_warning("[KO-Updater] Native extension load status: %d" % status)


func _reload_game_theme() -> void:
	if not ResourceLoader.exists(GAME_THEME_PATH):
		return
	var game_theme := ResourceLoader.load(GAME_THEME_PATH, "Theme", ResourceLoader.CACHE_MODE_REPLACE) as Theme
	if game_theme == null:
		return
	get_tree().root.theme = game_theme
	var project_theme := ThemeDB.get_project_theme()
	if project_theme != null and project_theme != game_theme:
		project_theme.merge_with(game_theme)


func _set_spinner_active(active: bool) -> void:
	_spinner.visible = active
	set_process(active)


func _show_status(message: String) -> void:
	_apply_status(message, &"HeadingLabel", STATUS_FONT_SIZE)


func _show_error_status(message: String) -> void:
	_apply_status(message, &"DangerLabel", ERROR_FONT_SIZE)


func _apply_status(message: String, variation: StringName, font_size: int) -> void:
	if message == _last_status_message and variation == _last_status_variation:
		return
	_last_status_message = message
	_last_status_variation = variation

	_status_label.theme_type_variation = variation
	_status_label.add_theme_font_size_override("font_size", font_size)
	_status_label.text = message

	var font := _status_label.get_theme_font("font")
	var text_width := STATUS_MAX_WIDTH
	if font != null:
		var measured := font.get_string_size(message, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
		text_width = minf(STATUS_MAX_WIDTH, ceilf(measured) + STATUS_WIDTH_PADDING)
	_status_label.custom_minimum_size = Vector2(text_width, 0.0)


func _format_footer(version: String = "") -> String:
	var resolved := version if not version.is_empty() else _display_version
	return ("KO Client v" + resolved) if not resolved.is_empty() else ""
