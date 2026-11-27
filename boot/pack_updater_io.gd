class_name PackUpdaterIO
extends RefCounted

const PACK_PRIORITY: Array[String] = ["assets_core", "assets_maps", "assets_skins"]
const PCK_MAGIC := "GDPC"
const ZIP_MAGIC := "PK"
const MIN_PCK_BYTES := 1024
const MIN_ZIP_BYTES := 4

const ERR_INTERRUPTED := "Update file was interrupted. Please try Retry."
const ERR_DISK_WRITE := "Couldn't save the update. Check free disk space and try Retry."
const ERR_FILE_LOCKED := "Please close any other KO Client window and click Retry."
const ERR_INSTALL_FAILED := "Couldn't finish installing the update. Please try Retry."


class ExtractResult extends RefCounted:
	var error_message := ""
	var staged_dlls: Dictionary = {}

	var is_ok: bool:
		get:
			return error_message.is_empty()


static func ordered_pack_ids(packs: Dictionary) -> Array[String]:
	var ordered: Array[String] = []
	for pack_id in PACK_PRIORITY:
		if packs.has(pack_id):
			ordered.append(pack_id)

	var extras: Array[String] = []
	for key in packs.keys():
		var pack_id := str(key)
		if pack_id != "game" and not ordered.has(pack_id):
			extras.append(pack_id)
	extras.sort()
	ordered.append_array(extras)

	if packs.has("game"):
		ordered.append("game")
	return ordered


static func resolve_url(manifest_url: String, raw_url: String) -> String:
	if raw_url.begins_with("http://") or raw_url.begins_with("https://"):
		return raw_url
	var base_url := manifest_url.split("?")[0]
	var last_slash := base_url.rfind("/")
	if last_slash < 0:
		return raw_url
	return base_url.substr(0, last_slash + 1) + raw_url.trim_prefix("/")


static func read_json_file(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		return {}
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(path))
	return (parsed as Dictionary) if parsed is Dictionary else {}


static func write_json_file(path: String, data: Dictionary) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		return
	file.store_string(JSON.stringify(data, "\t"))
	file.close()


static func file_size(path: String) -> int:
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		return -1
	var size_bytes := file.get_length()
	file.close()
	return size_bytes


static func remove_if_exists(path: String) -> void:
	if not path.is_empty() and FileAccess.file_exists(path):
		DirAccess.remove_absolute(path)


static func remove_old_dll_backups(dir_path: String) -> void:
	var dir := DirAccess.open(dir_path)
	if dir == null:
		return
	dir.list_dir_begin()
	var file_name := dir.get_next()
	while not file_name.is_empty():
		if not dir.current_is_dir() and file_name.to_lower().ends_with(".dll.old"):
			remove_if_exists(dir_path.path_join(file_name))
		file_name = dir.get_next()
	dir.list_dir_end()


static func is_zip_file(path: String) -> bool:
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null or file.get_length() < MIN_ZIP_BYTES:
		return false
	var header := file.get_buffer(2)
	file.close()
	return header.size() == 2 and header.get_string_from_ascii() == ZIP_MAGIC


static func is_valid_pck(path: String) -> bool:
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null or file.get_length() < MIN_PCK_BYTES:
		return false
	var header := file.get_buffer(4)
	file.close()
	return header.size() == 4 and header.get_string_from_ascii() == PCK_MAGIC


static func extract_pck_from_zip(zip_path: String, out_pck_path: String) -> ExtractResult:
	return _extract_zip(zip_path, out_pck_path, false)


static func extract_main_pack_from_zip(zip_path: String, out_pck_path: String) -> ExtractResult:
	return _extract_zip(zip_path, out_pck_path, true)


static func replace_file(staged_path: String, target_path: String) -> String:
	if FileAccess.file_exists(target_path) and DirAccess.remove_absolute(target_path) != OK:
		return ERR_FILE_LOCKED
	if DirAccess.rename_absolute(staged_path, target_path) != OK:
		return ERR_INSTALL_FAILED
	return ""


static func replace_file_with_backup(staged_path: String, target_path: String) -> String:
	if not FileAccess.file_exists(target_path):
		return replace_file(staged_path, target_path)

	var backup_path := target_path + ".old"
	remove_if_exists(backup_path)
	if DirAccess.rename_absolute(target_path, backup_path) != OK:
		return ERR_FILE_LOCKED
	if DirAccess.rename_absolute(staged_path, target_path) != OK:
		DirAccess.rename_absolute(backup_path, target_path)
		return ERR_INSTALL_FAILED
	return ""


static func mount_data_packs(base_dir: String) -> bool:
	var data_dir := base_dir.path_join("data")
	var game_pck_path := data_dir.path_join("game.pck")
	if not is_valid_pck(game_pck_path):
		return false
	if not ProjectSettings.load_resource_pack(game_pck_path, true):
		return false

	var dir := DirAccess.open(data_dir)
	if dir == null:
		return true
	var pack_files: Array[String] = []
	dir.list_dir_begin()
	var entry := dir.get_next()
	while not entry.is_empty():
		var is_data_pck := (
			not dir.current_is_dir()
			and entry.ends_with(".pck")
			and entry != "KO-Client.pck"
			and entry != "game.pck"
		)
		if is_data_pck:
			pack_files.append(entry)
		entry = dir.get_next()
	dir.list_dir_end()

	pack_files.sort()
	for file_name in pack_files:
		ProjectSettings.load_resource_pack(data_dir.path_join(file_name), false)
	return true


static func _extract_zip(zip_path: String, out_pck_path: String, include_dlls: bool) -> ExtractResult:
	var result := ExtractResult.new()
	var reader := ZIPReader.new()
	if reader.open(zip_path) != OK:
		result.error_message = ERR_INTERRUPTED
		return result

	var pck_entry := ""
	var dll_entries: Array[String] = []
	for entry in reader.get_files():
		if entry.ends_with("/"):
			continue
		var lower := entry.to_lower()
		if pck_entry.is_empty() and lower.ends_with(".pck"):
			pck_entry = entry
		elif include_dlls and lower.ends_with(".dll"):
			dll_entries.append(entry)

	if pck_entry.is_empty():
		reader.close()
		result.error_message = ERR_INTERRUPTED
		return result

	var pck_bytes := reader.read_file(pck_entry)
	var dll_payloads: Dictionary = {}
	for dll_entry in dll_entries:
		var dll_bytes := reader.read_file(dll_entry)
		if not dll_bytes.is_empty():
			dll_payloads[dll_entry.get_file()] = dll_bytes
	reader.close()

	if pck_bytes.is_empty():
		result.error_message = ERR_INTERRUPTED
		return result
	if not _write_binary_file(out_pck_path, pck_bytes):
		result.error_message = ERR_DISK_WRITE
		return result

	var stage_dir := out_pck_path.get_base_dir()
	for dll_name in dll_payloads.keys():
		var staged_dll := stage_dir.path_join(str(dll_name) + ".tmp")
		if not _write_binary_file(staged_dll, dll_payloads[dll_name]):
			for previous_path in result.staged_dlls.values():
				remove_if_exists(str(previous_path))
			result.staged_dlls.clear()
			result.error_message = ERR_DISK_WRITE
			return result
		result.staged_dlls[str(dll_name)] = staged_dll

	return result


static func _write_binary_file(path: String, bytes: PackedByteArray) -> bool:
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		return false
	file.store_buffer(bytes)
	file.close()
	return true
