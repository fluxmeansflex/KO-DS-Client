class_name PackUpdater
extends Node

signal check_started
signal version_detected(version: String)
signal pack_download_started(pack_id: String, shown_pct: float, step_caption: String)
signal download_progress_changed(shown_pct: float, step_caption: String)
signal extract_started(shown_pct: float)
signal update_completed(needs_restart: bool, version: String)
signal offline_ready(version: String)
signal update_failed(message: String, can_offline: bool)

const MANIFEST_URL := "https://download.ko-client.online/version.json"
const MANIFEST_TIMEOUT_SEC := 10.0
const DOWNLOAD_TIMEOUT_SEC := 600.0
const DOWNLOAD_CHUNK_SIZE := 1048576
const MIN_PACK_WEIGHT_BYTES := 1048576

const DOWNLOAD_WEIGHT_RATIO := 0.92
const EXTRACT_WEIGHT_RATIO := 0.95
const MAX_ACTIVE_PERCENT := 99.0
const ALMOST_READY_PERCENT := 92.0
const PROGRESS_EMIT_STEP := 0.25

const REQUIRED_OFFLINE_PACKS: Array[String] = [
	"game.pck",
	"assets_core.pck",
	"assets_maps.pck",
	"assets_skins.pck",
]

@export var manifest_request: HTTPRequest
@export var download_request: HTTPRequest


class PackTask extends RefCounted:
	var id := ""
	var is_main := false
	var target_path := ""
	var version := ""
	var url := ""
	var size := 0

	func weight() -> int:
		return maxi(size, MIN_PACK_WEIGHT_BYTES)


var _extract_task_id := -1
var _base_dir := ""
var _data_dir := ""
var _installed_path := ""

var _installed: Dictionary = {}
var _running_version := ""
var _remote_version := ""
var _queue: Array[PackTask] = []
var _queue_index := 0
var _queue_total_bytes := 0
var _queue_done_bytes := 0
var _shown_pct := 0.0
var _last_emitted_pct := -1.0
var _last_pct_int := -1
var _current_pack: PackTask = null
var _current_download_path := ""
var _needs_restart := false


func _ready() -> void:
	set_process(false)
	_configure_http_requests()


func _exit_tree() -> void:
	_wait_extract_worker()


func _process(_delta: float) -> void:
	_update_download_progress()


func initialize(running_version: String) -> String:
	_running_version = running_version
	_base_dir = OS.get_executable_path().get_base_dir()
	_data_dir = _base_dir.path_join("data")
	_installed_path = _data_dir.path_join("installed.json")

	DirAccess.make_dir_recursive_absolute(_data_dir)
	PackUpdaterIO.remove_old_dll_backups(_base_dir)

	_installed = PackUpdaterIO.read_json_file(_installed_path)
	return get_display_version()


func get_display_version() -> String:
	return str(_installed.get("version", _running_version))


func mount_local_packs() -> bool:
	if _base_dir.is_empty():
		return false
	return PackUpdaterIO.mount_data_packs(_base_dir)


func start_check() -> void:
	set_process(false)
	_needs_restart = false
	check_started.emit()

	var url := "%s?t=%d" % [MANIFEST_URL, int(Time.get_unix_time_from_system())]
	var err := manifest_request.request(url, PackedStringArray(["Cache-Control: no-cache"]), HTTPClient.METHOD_GET)
	if err != OK:
		push_error("[KO-Updater] Manifest request failed (err %d)" % err)
		_handle_offline_or_error("Can't connect to update server. Check your internet and try again.")


func _configure_http_requests() -> void:
	if manifest_request == null:
		manifest_request = HTTPRequest.new()
		manifest_request.name = "ManifestRequest"
		manifest_request.timeout = MANIFEST_TIMEOUT_SEC
		manifest_request.use_threads = true
		add_child(manifest_request)
	if not manifest_request.request_completed.is_connected(_on_manifest_completed):
		manifest_request.request_completed.connect(_on_manifest_completed)

	if download_request == null:
		download_request = HTTPRequest.new()
		download_request.name = "DownloadRequest"
		download_request.timeout = DOWNLOAD_TIMEOUT_SEC
		download_request.use_threads = true
		add_child(download_request)
	download_request.download_chunk_size = DOWNLOAD_CHUNK_SIZE
	download_request.accept_gzip = false
	if not download_request.request_completed.is_connected(_on_download_completed):
		download_request.request_completed.connect(_on_download_completed)


func _on_manifest_completed(result: int, code: int, _headers: PackedStringArray, body: PackedByteArray) -> void:
	if not _is_http_ok(result, code):
		push_error("[KO-Updater] Manifest fetch failed (result=%d, http=%d)" % [result, code])
		_handle_offline_or_error("Can't connect to update server. Check your internet and try again.")
		return

	var parsed: Variant = JSON.parse_string(body.get_string_from_utf8())
	if not (parsed is Dictionary):
		_handle_offline_or_error("Can't reach update server right now. Please try again.")
		return

	_plan_updates(parsed as Dictionary)


func _plan_updates(manifest: Dictionary) -> void:
	_remote_version = str(manifest.get("version", ""))
	if not _remote_version.is_empty():
		version_detected.emit(_remote_version)

	var packs_val: Variant = manifest.get("packs", {})
	if not (packs_val is Dictionary) or (packs_val as Dictionary).is_empty():
		_handle_offline_or_error("Update info isn't available right now. Please try again.")
		return

	_queue = _build_update_queue(packs_val as Dictionary)
	if _queue.is_empty():
		update_completed.emit(false, _remote_version)
		return

	_queue_index = 0
	_queue_done_bytes = 0
	_queue_total_bytes = 0
	_shown_pct = 0.0
	_last_emitted_pct = -1.0
	_last_pct_int = -1
	for task in _queue:
		_queue_total_bytes += task.weight()
	_download_next()


func _build_update_queue(packs: Dictionary) -> Array[PackTask]:
	var installed_packs: Dictionary = _installed.get("packs", {})
	var tasks: Array[PackTask] = []

	for pack_id in PackUpdaterIO.ordered_pack_ids(packs):
		var spec_val: Variant = packs[pack_id]
		if not (spec_val is Dictionary):
			continue
		var spec := spec_val as Dictionary
		var is_main := pack_id == "game"
		var file_name := str(spec.get("file", pack_id + ".pck"))
		var target_path := _data_dir.path_join(file_name)
		var remote_ver := str(spec.get("version", _remote_version))
		var local_ver := str(installed_packs.get(pack_id, ""))

		var needs_download := local_ver != remote_ver or not PackUpdaterIO.is_valid_pck(target_path)
		if needs_download:
			var task := PackTask.new()
			task.id = pack_id
			task.is_main = is_main
			task.target_path = target_path
			task.version = remote_ver
			task.url = PackUpdaterIO.resolve_url(MANIFEST_URL, str(spec.get("url", "")))
			task.size = int(spec.get("size", 0))
			tasks.append(task)

	return tasks


func _download_next() -> void:
	if _queue_index >= _queue.size():
		_installed["version"] = _remote_version
		PackUpdaterIO.write_json_file(_installed_path, _installed)
		update_completed.emit(_needs_restart, _remote_version)
		return

	_current_pack = _queue[_queue_index]
	if _current_pack.url.is_empty():
		update_failed.emit("Couldn't download the update. Please try Retry.", _has_local_assets())
		return

	var is_zip := _current_pack.url.to_lower().split("?")[0].ends_with(".zip")
	var suffix := ".zip.download" if is_zip else ".pck.download"
	_current_download_path = _data_dir.path_join(_current_pack.id + suffix)
	PackUpdaterIO.remove_if_exists(_current_download_path)

	pack_download_started.emit(_current_pack.id, _shown_pct, _step_caption(_shown_pct))
	download_request.download_file = _current_download_path
	var err := download_request.request(_current_pack.url, PackedStringArray(), HTTPClient.METHOD_GET)
	if err != OK:
		update_failed.emit("Can't connect to update server. Check your internet and try again.", _has_local_assets())
		return
	set_process(true)


func _update_download_progress() -> void:
	if _current_pack == null:
		return
	var downloaded := download_request.get_downloaded_bytes()
	var pack_total := download_request.get_body_size()
	if pack_total <= 0:
		pack_total = _current_pack.size

	var pack_fraction := clampf(float(downloaded) / float(pack_total), 0.0, 1.0) if pack_total > 0 else 0.0
	var overall_done := float(_queue_done_bytes) + float(_current_pack.weight()) * (pack_fraction * DOWNLOAD_WEIGHT_RATIO)
	_shown_pct = _advance_percent(overall_done)

	var pct_int := int(floor(_shown_pct))
	var caption := ""
	if pct_int != _last_pct_int:
		_last_pct_int = pct_int
		caption = _step_caption(_shown_pct)
	elif _shown_pct - _last_emitted_pct < PROGRESS_EMIT_STEP:
		return

	_last_emitted_pct = _shown_pct
	download_progress_changed.emit(_shown_pct, caption)


func _on_download_completed(result: int, code: int, _headers: PackedStringArray, _body: PackedByteArray) -> void:
	set_process(false)
	download_request.download_file = ""

	if not _is_http_ok(result, code):
		PackUpdaterIO.remove_if_exists(_current_download_path)
		update_failed.emit("Update download failed. Check your internet and try Retry.", _has_local_assets())
		return

	if PackUpdaterIO.is_zip_file(_current_download_path):
		_start_extract()
		return

	if _current_pack.size > 0 and PackUpdaterIO.file_size(_current_download_path) != _current_pack.size:
		PackUpdaterIO.remove_if_exists(_current_download_path)
		update_failed.emit("Update file was interrupted. Please try Retry.", _has_local_assets())
		return

	_finalize_pack(_current_download_path, {})


func _start_extract() -> void:
	var overall_done := float(_queue_done_bytes) + float(_current_pack.weight()) * EXTRACT_WEIGHT_RATIO
	_shown_pct = _advance_percent(overall_done)
	_last_emitted_pct = _shown_pct
	extract_started.emit(_shown_pct)

	var tmp_pck_path := _data_dir.path_join(_current_pack.id + ".pck.tmp")
	PackUpdaterIO.remove_if_exists(tmp_pck_path)
	_wait_extract_worker()
	_extract_task_id = WorkerThreadPool.add_task(
		_run_extract_worker.bind(_current_download_path, tmp_pck_path, _current_pack.is_main)
	)


func _run_extract_worker(zip_path: String, out_pck_path: String, is_main_pack: bool) -> void:
	var extract_result := (
		PackUpdaterIO.extract_main_pack_from_zip(zip_path, out_pck_path)
		if is_main_pack
		else PackUpdaterIO.extract_pck_from_zip(zip_path, out_pck_path)
	)
	_on_extract_done.call_deferred(out_pck_path, extract_result)


func _on_extract_done(tmp_pck_path: String, extract_result: PackUpdaterIO.ExtractResult) -> void:
	_wait_extract_worker()
	PackUpdaterIO.remove_if_exists(_current_download_path)

	if not extract_result.is_ok:
		_cleanup_staged_files(tmp_pck_path, extract_result.staged_dlls)
		update_failed.emit(extract_result.error_message, _has_local_assets())
		return

	_finalize_pack(tmp_pck_path, extract_result.staged_dlls)


func _finalize_pack(staged_pck: String, staged_dlls: Dictionary) -> void:
	if not PackUpdaterIO.is_valid_pck(staged_pck):
		_cleanup_staged_files(staged_pck, staged_dlls)
		update_failed.emit("Update file was interrupted. Please try Retry.", _has_local_assets())
		return

	var swap_error := PackUpdaterIO.replace_file(staged_pck, _current_pack.target_path)
	if not swap_error.is_empty():
		_cleanup_staged_files(staged_pck, staged_dlls)
		update_failed.emit(swap_error, false)
		return

	if _current_pack.is_main and not staged_dlls.is_empty():
		for dll_name in staged_dlls.keys():
			var dll_target := _base_dir.path_join(str(dll_name))
			var dll_error := PackUpdaterIO.replace_file_with_backup(str(staged_dlls[dll_name]), dll_target)
			if not dll_error.is_empty():
				_cleanup_staged_files("", staged_dlls)
				update_failed.emit(dll_error, false)
				return
		_needs_restart = true

	_record_pack_version(_current_pack.id, _current_pack.version)
	_queue_done_bytes += _current_pack.weight()
	_shown_pct = _advance_percent(float(_queue_done_bytes))
	_queue_index += 1
	_download_next()


func _handle_offline_or_error(reason: String) -> void:
	if _has_local_assets():
		offline_ready.emit(get_display_version())
		return
	update_failed.emit(reason, false)


func _wait_extract_worker() -> void:
	if _extract_task_id != -1:
		WorkerThreadPool.wait_for_task_completion(_extract_task_id)
		_extract_task_id = -1


func _cleanup_staged_files(pck_path: String, dlls: Dictionary) -> void:
	PackUpdaterIO.remove_if_exists(pck_path)
	for path in dlls.values():
		PackUpdaterIO.remove_if_exists(str(path))


func _advance_percent(done_bytes: float) -> float:
	var raw_pct := done_bytes / float(maxi(_queue_total_bytes, 1)) * 100.0
	return maxf(_shown_pct, clampf(raw_pct, 0.0, MAX_ACTIVE_PERCENT))


func _step_caption(percent: float) -> String:
	if percent >= ALMOST_READY_PERCENT:
		return "Almost ready"
	if _queue.size() > 1:
		return "Step %d of %d" % [_queue_index + 1, _queue.size()]
	return "Syncing"


func _has_local_assets() -> bool:
	for pack_file in REQUIRED_OFFLINE_PACKS:
		if not PackUpdaterIO.is_valid_pck(_data_dir.path_join(pack_file)):
			return false
	return true


func _record_pack_version(pack_id: String, version: String) -> void:
	if not (_installed.get("packs") is Dictionary):
		_installed["packs"] = {}
	(_installed["packs"] as Dictionary)[pack_id] = version
	PackUpdaterIO.write_json_file(_installed_path, _installed)


static func _is_http_ok(result: int, code: int) -> bool:
	return result == HTTPRequest.RESULT_SUCCESS and code >= 200 and code < 300
