use std::{
    env,
    os::windows::ffi::OsStrExt,
    os::windows::fs::MetadataExt,
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
    time::{SystemTime, UNIX_EPOCH},
};
use windows_sys::Win32::Storage::FileSystem::MoveFileExW;

use idevice::{
    IdeviceService,
    provider::IdeviceProvider,
    services::{
        afc::{AfcClient, FileInfo, opcode::AfcFopenMode},
        house_arrest::HouseArrestClient,
        installation_proxy::InstallationProxyClient,
    },
    usbmuxd::{Connection, UsbmuxdAddr, UsbmuxdConnection},
};
use plist::Value;
use serde::Serialize;
use serde_json::{Value as JsonValue, json};
use tokio::{
    fs,
    io::{AsyncReadExt, AsyncWriteExt},
};

#[derive(Serialize)]
struct DeviceRow {
    id: String,
    name: String,
}

#[derive(Serialize)]
struct AppRow {
    id: String,
    name: String,
}

#[derive(Serialize)]
struct FileRow {
    name: String,
    path: String,
    is_directory: bool,
    size: u64,
}

#[derive(Clone)]
struct TransferEntry {
    local: PathBuf,
    remote: String,
    is_directory: bool,
    size: u64,
}

static TEMP_COUNTER: AtomicU64 = AtomicU64::new(0);

#[tokio::main]
async fn main() {
    let result = run().await;
    match result {
        Ok(value) => println!("{}", value),
        Err(error) => println!("{}", json!({"ok": false, "error": error})),
    }
}

async fn run() -> Result<JsonValue, String> {
    let args: Vec<String> = env::args().skip(1).collect();
    let Some(command) = args.first().map(String::as_str) else {
        return Err(usage());
    };

    match command {
        "devices" => list_devices().await,
        "apps" if args.len() == 2 => list_apps(&args[1]).await,
        "list" if args.len() == 4 => list_remote(&args[1], &args[2], &args[3]).await,
        "verify-transfer" if args.len() == 3 => verify_transfer(&args[1], &args[2]).await,
        "download" if (args.len() == 5 || args.len() == 6) => {
            let overwrite = args.get(5).is_some_and(|value| value == "--overwrite");
            download(&args[1], &args[2], &args[3], Path::new(&args[4]), overwrite).await
        }
        "upload" if (args.len() == 5 || args.len() == 6) => {
            let overwrite = args.get(5).is_some_and(|value| value == "--overwrite");
            upload(&args[1], &args[2], Path::new(&args[3]), &args[4], overwrite).await
        }
        _ => Err(usage()),
    }
}

fn usage() -> String {
    "使い方: devices | apps <端末ID> | list <端末ID> <アプリID> <フォルダー> | verify-transfer <端末ID> <アプリID> | download <端末ID> <アプリID> <端末パス> <PCパス> [--overwrite] | upload <端末ID> <アプリID> <PCパス> <端末パス> [--overwrite]".to_string()
}

async fn usbmux() -> Result<UsbmuxdConnection, String> {
    UsbmuxdConnection::default()
        .await
        .map_err(|error| {
            format!(
                "Apple Mobile Device Service に接続できません。既存のApple接続サービスが起動しているか確認してください。 ({error})"
            )
        })
}

async fn list_devices() -> Result<JsonValue, String> {
    let mut mux = usbmux().await?;
    let devices = mux.get_devices().await.map_err(|error| error.to_string())?;
    let rows: Vec<DeviceRow> = devices
        .into_iter()
        .filter(|device| device.connection_type == Connection::Usb)
        .map(|device| DeviceRow {
            id: device.device_id.to_string(),
            name: "iPhone / iPad (USB)".to_string(),
        })
        .collect();
    Ok(json!({"ok": true, "items": rows}))
}

async fn provider_for(device_id: &str) -> Result<impl IdeviceProvider, String> {
    let mut mux = usbmux().await?;
    let devices = mux.get_devices().await.map_err(|error| error.to_string())?;
    let target = devices
        .into_iter()
        .find(|device| {
            device.device_id.to_string() == device_id && device.connection_type == Connection::Usb
        })
        .ok_or_else(|| {
            "端末が見つかりません。USB接続と端末のロック解除を確認してください。".to_string()
        })?;
    Ok(target.to_provider(UsbmuxdAddr::default(), "AppleDeviceTransfer"))
}

async fn list_apps(device_id: &str) -> Result<JsonValue, String> {
    let provider = provider_for(device_id).await?;
    let mut client = InstallationProxyClient::connect(&provider)
        .await
        .map_err(|error| explain_connection_error(error.to_string()))?;
    let applications = client
        .get_apps(Some("User"), None)
        .await
        .map_err(|error| explain_connection_error(error.to_string()))?;

    let mut rows = Vec::new();
    for (bundle_id, info) in applications {
        let Some(dictionary) = info.as_dictionary() else {
            continue;
        };
        if !is_true(dictionary.get("UIFileSharingEnabled")) {
            continue;
        }

        let name = dictionary
            .get("CFBundleDisplayName")
            .and_then(Value::as_string)
            .or_else(|| dictionary.get("CFBundleName").and_then(Value::as_string))
            .unwrap_or(&bundle_id)
            .to_string();
        rows.push(AppRow {
            id: bundle_id,
            name,
        });
    }
    rows.sort_by(|left, right| left.name.to_lowercase().cmp(&right.name.to_lowercase()));
    Ok(json!({"ok": true, "items": rows}))
}

fn is_true(value: Option<&Value>) -> bool {
    matches!(value, Some(Value::Boolean(true)))
}

async fn connect_documents(device_id: &str, app_id: &str) -> Result<(AfcClient, String), String> {
    let provider = provider_for(device_id).await?;
    let house_arrest = HouseArrestClient::connect(&provider)
        .await
        .map_err(|error| explain_connection_error(error.to_string()))?;
    let afc = house_arrest
        .vend_documents(app_id)
        .await
        .map_err(|error| {
            format!(
                "このアプリの書類領域を開けません。アプリがファイル共有に対応しているか確認してください。 ({error})"
            )
        })?;

    // House Arrest keeps the app container root but limits VendDocuments access
    // to this directory. Root listing is denied, so use the documented path.
    Ok((afc, "/Documents".to_string()))
}

async fn list_remote(
    device_id: &str,
    app_id: &str,
    relative_path: &str,
) -> Result<JsonValue, String> {
    let (mut afc, root) = connect_documents(device_id, app_id).await?;
    let path = remote_path(&root, relative_path)?;
    let names = afc
        .list_dir(&path)
        .await
        .map_err(|error| error.to_string())?;
    let mut rows = Vec::new();

    for name in names.into_iter().filter(|name| name != "." && name != "..") {
        let child_path = join_remote(&path, &name);
        let info = match afc.get_file_info(&child_path).await {
            Ok(info) => info,
            Err(_) => continue,
        };
        if info.st_ifmt != "S_IFDIR" && info.st_ifmt != "S_IFREG" {
            continue;
        }
        rows.push(FileRow {
            name,
            path: relative_to_root(&root, &child_path),
            is_directory: info.st_ifmt == "S_IFDIR",
            size: info.size as u64,
        });
    }

    rows.sort_by(|left, right| {
        right
            .is_directory
            .cmp(&left.is_directory)
            .then_with(|| left.name.to_lowercase().cmp(&right.name.to_lowercase()))
    });
    Ok(json!({"ok": true, "items": rows, "path": relative_to_root(&root, &path)}))
}

async fn download(
    device_id: &str,
    app_id: &str,
    remote_relative: &str,
    local_path: &Path,
    overwrite: bool,
) -> Result<JsonValue, String> {
    validate_windows_relative_path(remote_relative)?;
    let (mut afc, root) = connect_documents(device_id, app_id).await?;
    let remote = remote_path(&root, remote_relative)?;
    let info = afc
        .get_file_info(&remote)
        .await
        .map_err(|error| error.to_string())?;
    let mut entries = Vec::new();
    collect_download_entries(&mut afc, &remote, local_path, &info, &mut entries).await?;
    if entries.is_empty() {
        return Ok(json!({"ok": true, "copied": 0, "bytes": 0}));
    }

    let total: u64 = entries
        .iter()
        .filter(|entry| !entry.is_directory)
        .map(|entry| entry.size)
        .sum();
    if let Some(entry) = entries
        .iter()
        .find(|entry| !entry.is_directory && entry.local.is_dir())
    {
        return Err(format!(
            "保存先に同名のフォルダーがあります。ファイルでは置き換えられません: {}",
            entry.local.display()
        ));
    }
    if !overwrite {
        if let Some(entry) = entries
            .iter()
            .find(|entry| !entry.is_directory && entry.local.exists())
        {
            return Err(format!(
                "保存先に同名ファイルがあります: {}",
                entry.local.display()
            ));
        }
    }
    for entry in entries.iter().filter(|entry| entry.is_directory) {
        fs::create_dir_all(&entry.local)
            .await
            .map_err(|error| error.to_string())?;
    }
    let mut done = 0u64;
    let mut copied = 0usize;
    for entry in entries.iter().filter(|entry| !entry.is_directory) {
        if let Some(parent) = entry.local.parent() {
            fs::create_dir_all(parent)
                .await
                .map_err(|error| error.to_string())?;
        }
        let mut remote_file = afc
            .open(&entry.remote, AfcFopenMode::RdOnly)
            .await
            .map_err(|error| error.to_string())?;
        let (temporary_path, mut local_file) = create_temporary_local_file(&entry.local).await?;
        let mut item_bytes = 0u64;
        let transfer_result = async {
            loop {
                let chunk = remote_file.read_n(1024 * 1024).await.map_err(|error| error.to_string())?;
                if chunk.is_empty() {
                    break;
                }
                local_file.write_all(&chunk).await.map_err(|error| error.to_string())?;
                item_bytes += chunk.len() as u64;
                done += chunk.len() as u64;
                println!("{}", json!({"type":"progress","direction":"download","done":done,"total":total,"file":entry.local.file_name().map(|name| name.to_string_lossy().to_string()).unwrap_or_default()}));
            }
            remote_file.close().await.map_err(|error| error.to_string())?;
            local_file.flush().await.map_err(|error| error.to_string())?;
            if item_bytes != entry.size {
                return Err(format!("転送サイズが一致しません: {} ({} / {} bytes)", entry.local.display(), item_bytes, entry.size));
            }
            Ok::<(), String>(())
        }.await;
        drop(local_file);
        if let Err(error) = transfer_result {
            let _ = fs::remove_file(&temporary_path).await;
            return Err(error);
        }
        if !overwrite && entry.local.exists() {
            let _ = fs::remove_file(&temporary_path).await;
            return Err(format!(
                "保存先に同名ファイルがあります: {}",
                entry.local.display()
            ));
        }
        let commit = if overwrite {
            fs::rename(&temporary_path, &entry.local).await
        } else {
            rename_noreplace(&temporary_path, &entry.local)
        };
        if let Err(error) = commit {
            let _ = fs::remove_file(&temporary_path).await;
            if error.kind() == std::io::ErrorKind::AlreadyExists {
                return Err(format!(
                    "保存先に同名ファイルがあります: {}",
                    entry.local.display()
                ));
            }
            return Err(format!(
                "保存先へファイルを確定できませんでした: {} ({error})",
                entry.local.display()
            ));
        }
        copied += 1;
    }
    Ok(
        json!({"ok": true, "copied": copied, "bytes": done, "path": local_path.display().to_string()}),
    )
}

async fn verify_transfer(device_id: &str, app_id: &str) -> Result<JsonValue, String> {
    let suffix = unique_suffix();
    let file_name = format!(".AppleDeviceTransfer-verify-{suffix}.bin");
    let local_source = env::temp_dir().join(format!("AppleDeviceTransfer-verify-{suffix}.src"));
    let local_copy = env::temp_dir().join(format!("AppleDeviceTransfer-verify-{suffix}.copy"));
    let mut expected = Vec::with_capacity(8192);
    expected.extend_from_slice(b"AppleDeviceTransfer USB verification\r\n");
    expected.extend((0..8192).map(|index| (index % 256) as u8));

    fs::write(&local_source, &expected)
        .await
        .map_err(|error| format!("PC側の一時ファイルを作成できません: {error}"))?;

    let upload_result = upload(device_id, app_id, &local_source, &file_name, false).await;
    let operation_result = match upload_result {
        Ok(_) => match download(device_id, app_id, &file_name, &local_copy, false).await {
            Ok(_) => {
                let actual = fs::read(&local_copy)
                    .await
                    .map_err(|error| format!("PC側の検証コピーを読めません: {error}"));
                match actual {
                    Ok(actual) if actual == expected => Ok(()),
                    Ok(_) => Err("USB往復後のファイル内容が一致しません。".to_string()),
                    Err(error) => Err(error),
                }
            }
            Err(error) => Err(error),
        },
        Err(error) => Err(error),
    };

    let cleanup_result = async {
        let (mut afc, root) = connect_documents(device_id, app_id).await?;
        let remote = remote_path(&root, &file_name)?;
        if afc.get_file_info(&remote).await.is_ok() {
            afc.remove(&remote)
                .await
                .map_err(|error| format!("iPad側の検証ファイルを削除できません: {error}"))?;
        }
        Ok::<(), String>(())
    }
    .await;
    let _ = fs::remove_file(&local_source).await;
    let _ = fs::remove_file(&local_copy).await;

    match (operation_result, cleanup_result) {
        (Ok(()), Ok(())) => Ok(
            json!({"ok": true, "verified_bytes": expected.len(), "temporary_files_removed": true}),
        ),
        (Err(operation_error), Ok(())) => Err(operation_error),
        (Ok(()), Err(cleanup_error)) => Err(format!(
            "転送確認は成功しましたが、検証用ファイルを片付けられませんでした。iPad側の書類に `{file_name}` が残っています。 ({cleanup_error})"
        )),
        (Err(operation_error), Err(cleanup_error)) => Err(format!(
            "転送確認に失敗しました: {operation_error}。検証用ファイルの片付けにも失敗しました。iPad側の書類に `{file_name}` が残っている可能性があります。 ({cleanup_error})"
        )),
    }
}

async fn collect_download_entries(
    afc: &mut AfcClient,
    remote: &str,
    local: &Path,
    info: &FileInfo,
    output: &mut Vec<TransferEntry>,
) -> Result<(), String> {
    if info.st_ifmt == "S_IFDIR" {
        output.push(TransferEntry {
            local: local.to_path_buf(),
            remote: remote.to_string(),
            is_directory: true,
            size: 0,
        });
        let children = afc
            .list_dir(remote)
            .await
            .map_err(|error| error.to_string())?;
        for name in children
            .into_iter()
            .filter(|name| name != "." && name != "..")
        {
            if !is_safe_windows_component(&name) {
                return Err(format!("PCで保存できないiPad側のファイル名です: {name}"));
            }
            let remote_child = join_remote(remote, &name);
            let child_info = afc
                .get_file_info(&remote_child)
                .await
                .map_err(|error| error.to_string())?;
            let local_child = local.join(&name);
            Box::pin(collect_download_entries(
                afc,
                &remote_child,
                &local_child,
                &child_info,
                output,
            ))
            .await?;
        }
    } else if info.st_ifmt == "S_IFREG" {
        let name = local
            .file_name()
            .and_then(|value| value.to_str())
            .unwrap_or_default();
        if !is_safe_windows_component(name) {
            return Err(format!("PCで保存できないファイル名です: {name}"));
        }
        output.push(TransferEntry {
            local: local.to_path_buf(),
            remote: remote.to_string(),
            is_directory: false,
            size: info.size as u64,
        });
    } else {
        return Err(format!("対応していないiPad側の項目です: {remote}"));
    }
    Ok(())
}

async fn upload(
    device_id: &str,
    app_id: &str,
    local_path: &Path,
    remote_relative: &str,
    overwrite: bool,
) -> Result<JsonValue, String> {
    let (mut afc, root) = connect_documents(device_id, app_id).await?;
    let remote = remote_path(&root, remote_relative)?;
    if !local_path.exists() {
        return Err(format!(
            "PC側のファイルが見つかりません: {}",
            local_path.display()
        ));
    }

    let mut entries = Vec::new();
    collect_upload_entries(local_path, &remote, &mut entries).await?;
    let total: u64 = entries
        .iter()
        .filter(|entry| !entry.is_directory)
        .map(|entry| entry.size)
        .sum();
    if !overwrite {
        for entry in entries.iter().filter(|entry| !entry.is_directory) {
            if afc.get_file_info(&entry.remote).await.is_ok() {
                return Err(format!("iPad側に同名ファイルがあります: {}", entry.remote));
            }
        }
    }
    let mut done = 0u64;
    let mut copied = 0usize;

    for entry in &entries {
        if entry.is_directory {
            ensure_remote_dir(&mut afc, &entry.remote).await?;
            continue;
        }

        let exists = afc.get_file_info(&entry.remote).await.is_ok();
        if exists && !overwrite {
            return Err(format!("iPad側に同名ファイルがあります: {}", entry.remote));
        }
        if let Some(parent) = remote_parent(&entry.remote) {
            ensure_remote_dir(&mut afc, &parent).await?;
        }

        let temporary_remote =
            create_temporary_remote_path(&mut afc, &entry.remote, "part").await?;
        let mut local_file = fs::File::open(&entry.local)
            .await
            .map_err(|error| error.to_string())?;
        let mut remote_file = afc
            .open(&temporary_remote, AfcFopenMode::WrOnly)
            .await
            .map_err(|error| error.to_string())?;
        let mut item_bytes = 0u64;
        let mut buffer = vec![0u8; 1024 * 1024];
        let transfer_result = async {
            loop {
                let bytes = local_file.read(&mut buffer).await.map_err(|error| error.to_string())?;
                if bytes == 0 {
                    break;
                }
                remote_file
                    .write_entire(&buffer[..bytes])
                    .await
                    .map_err(|error| error.to_string())?;
                item_bytes += bytes as u64;
                done += bytes as u64;
                println!("{}", json!({"type":"progress","direction":"upload","done":done,"total":total,"file":entry.local.file_name().map(|name| name.to_string_lossy().to_string()).unwrap_or_default()}));
            }
            remote_file.close().await.map_err(|error| error.to_string())?;
            if item_bytes != entry.size {
                return Err(format!("転送サイズが一致しません: {} ({} / {} bytes)", entry.local.display(), item_bytes, entry.size));
            }
            Ok::<(), String>(())
        }.await;
        if let Err(error) = transfer_result {
            let _ = afc.remove(&temporary_remote).await;
            return Err(error);
        }
        if let Err(error) =
            commit_remote_file(&mut afc, &temporary_remote, &entry.remote, overwrite).await
        {
            let _ = afc.remove(&temporary_remote).await;
            return Err(error);
        }
        copied += 1;
    }

    Ok(json!({"ok": true, "copied": copied, "bytes": done, "path": remote}))
}

async fn collect_upload_entries(
    local: &Path,
    remote: &str,
    output: &mut Vec<TransferEntry>,
) -> Result<(), String> {
    let metadata = fs::symlink_metadata(local)
        .await
        .map_err(|error| error.to_string())?;
    if metadata.file_type().is_symlink() || metadata.file_attributes() & 0x400 != 0 {
        return Err(format!(
            "シンボリックリンクやジャンクションは転送できません: {}",
            local.display()
        ));
    }
    if metadata.is_dir() {
        output.push(TransferEntry {
            local: local.to_path_buf(),
            remote: remote.to_string(),
            is_directory: true,
            size: 0,
        });
        let mut children = fs::read_dir(local)
            .await
            .map_err(|error| error.to_string())?;
        while let Some(child) = children
            .next_entry()
            .await
            .map_err(|error| error.to_string())?
        {
            let child_path = child.path();
            let name = child
                .file_name()
                .to_str()
                .ok_or_else(|| format!("ファイル名を読み取れません: {}", child_path.display()))?
                .to_string();
            let remote_child = join_remote(remote, &name);
            Box::pin(collect_upload_entries(&child_path, &remote_child, output)).await?;
        }
    } else if metadata.is_file() {
        output.push(TransferEntry {
            local: local.to_path_buf(),
            remote: remote.to_string(),
            is_directory: false,
            size: metadata.len(),
        });
    } else {
        return Err(format!(
            "対応していないファイル種別です: {}",
            local.display()
        ));
    }
    Ok(())
}

async fn ensure_remote_dir(afc: &mut AfcClient, path: &str) -> Result<(), String> {
    if let Ok(info) = afc.get_file_info(path).await {
        if info.st_ifmt == "S_IFDIR" {
            return Ok(());
        }
        return Err(format!("iPad側に同名のファイルがあります: {path}"));
    }
    if let Some(parent) = remote_parent(path) {
        if parent != path {
            Box::pin(ensure_remote_dir(afc, &parent)).await?;
        }
    }
    afc.mk_dir(path).await.map_err(|error| error.to_string())
}

fn remote_path(root: &str, relative: &str) -> Result<String, String> {
    if relative.is_empty() {
        return Ok(root.to_string());
    }

    if relative.starts_with('/') || relative.contains("//") {
        return Err("iPad側のパスが不正です。".to_string());
    }
    let pieces: Vec<&str> = relative.split('/').collect();
    if pieces
        .iter()
        .any(|piece| piece.is_empty() || *piece == "." || *piece == "..")
    {
        return Err("上位フォルダーには移動できません。".to_string());
    }

    let mut path = root.trim_end_matches('/').to_string();
    for piece in pieces {
        path.push('/');
        path.push_str(piece);
    }
    if path.is_empty() {
        path = "/".to_string();
    }
    Ok(path)
}

fn is_safe_windows_component(name: &str) -> bool {
    if name.is_empty()
        || name == "."
        || name == ".."
        || name.ends_with('.')
        || name.ends_with(' ')
        || name.chars().any(|character| {
            character.is_control()
                || matches!(
                    character,
                    '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*'
                )
        })
    {
        return false;
    }

    let base_name = name
        .split('.')
        .next()
        .unwrap_or_default()
        .to_ascii_uppercase();
    !matches!(base_name.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        && !(base_name.len() == 4
            && (base_name.starts_with("COM") || base_name.starts_with("LPT"))
            && matches!(base_name.as_bytes()[3], b'1'..=b'9'))
}

fn validate_windows_relative_path(relative: &str) -> Result<(), String> {
    if relative.is_empty() || relative.starts_with('/') {
        return Err("iPad側のパスが不正です。".to_string());
    }
    for component in relative.split('/') {
        if !is_safe_windows_component(component) {
            return Err(format!("PCで扱えないiPad側のパス名です: {component}"));
        }
    }
    Ok(())
}

fn rename_noreplace(source: &Path, destination: &Path) -> std::io::Result<()> {
    let source: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
    let destination: Vec<u16> = destination
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect();
    let success = unsafe { MoveFileExW(source.as_ptr(), destination.as_ptr(), 0) };
    if success == 0 {
        Err(std::io::Error::last_os_error())
    } else {
        Ok(())
    }
}

fn unique_suffix() -> String {
    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    let count = TEMP_COUNTER.fetch_add(1, Ordering::Relaxed);
    format!("{}-{}-{count}", std::process::id(), timestamp)
}

async fn create_temporary_local_file(target: &Path) -> Result<(PathBuf, fs::File), String> {
    let parent = target.parent().unwrap_or_else(|| Path::new("."));
    for _ in 0..100 {
        let path = parent.join(format!(".AppleDeviceTransfer-part-{}", unique_suffix()));
        match fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)
            .await
        {
            Ok(file) => return Ok((path, file)),
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(error) => {
                return Err(format!(
                    "一時ファイルを作成できません: {} ({error})",
                    path.display()
                ));
            }
        }
    }
    Err("一時ファイル名を確保できませんでした。".to_string())
}

async fn create_temporary_remote_path(
    afc: &mut AfcClient,
    target: &str,
    marker: &str,
) -> Result<String, String> {
    let parent = remote_parent(target).unwrap_or_else(|| "/".to_string());
    for _ in 0..100 {
        let candidate = join_remote(
            &parent,
            &format!(".AppleDeviceTransfer-{marker}-{}", unique_suffix()),
        );
        if afc.get_file_info(&candidate).await.is_err() {
            return Ok(candidate);
        }
    }
    Err("iPad側の一時ファイル名を確保できませんでした。".to_string())
}

async fn commit_remote_file(
    afc: &mut AfcClient,
    temporary: &str,
    target: &str,
    overwrite: bool,
) -> Result<(), String> {
    let target_info = afc.get_file_info(target).await.ok();
    let Some(target_info) = target_info else {
        return afc
            .rename(temporary, target)
            .await
            .map_err(|error| error.to_string());
    };

    if !overwrite {
        return Err(format!("iPad側に同名ファイルがあります: {target}"));
    }
    if target_info.st_ifmt != "S_IFREG" {
        return Err(format!(
            "iPad側の同名項目はファイルではないため置き換えられません: {target}"
        ));
    }

    let backup = create_temporary_remote_path(afc, target, "backup").await?;

    afc.rename(target, &backup)
        .await
        .map_err(|error| error.to_string())?;
    if let Err(error) = afc.rename(temporary, target).await {
        if let Err(restore_error) = afc.rename(&backup, target).await {
            return Err(format!(
                "新しいファイルを置けず、元のファイルも戻せませんでした。バックアップは {backup} に残っています。転送エラー: {error}; 復元エラー: {restore_error}"
            ));
        }
        return Err(error.to_string());
    }
    afc.remove(&backup).await.map_err(|error| {
        format!("転送は完了しましたが、一時バックアップを削除できませんでした: {backup} ({error})")
    })?;
    Ok(())
}

fn join_remote(parent: &str, name: &str) -> String {
    if parent == "/" {
        format!("/{name}")
    } else {
        format!("{}/{name}", parent.trim_end_matches('/'))
    }
}

fn relative_to_root(root: &str, full_path: &str) -> String {
    let relative = if root == "/" {
        full_path.strip_prefix('/').unwrap_or(full_path)
    } else {
        full_path
            .strip_prefix(root)
            .unwrap_or(full_path)
            .trim_start_matches('/')
    };
    relative.to_string()
}

fn remote_parent(path: &str) -> Option<String> {
    let normalized = path.trim_end_matches('/');
    match normalized.rsplit_once('/') {
        Some(("", _)) => Some("/".to_string()),
        Some((parent, _)) => Some(parent.to_string()),
        None => None,
    }
}

fn explain_connection_error(error: String) -> String {
    if error.to_lowercase().contains("pair") || error.to_lowercase().contains("lockdown") {
        format!("端末のロックを解除し、「このコンピュータを信頼」を選んでください。 ({error})")
    } else {
        error
    }
}
