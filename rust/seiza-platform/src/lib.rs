//! Windows-only adapter for a focused parallax download. The upstream C preset
//! currently offers distances only as part of Everything; transfer, cache,
//! hashing and atomic installation still come from seiza-download.
use seiza::catalog::{ObjectDistances, StarDistanceCatalog};
use seiza_download::{
    CachePolicy, CatalogBundle, CatalogManager, CatalogSet, Dataset, DownloadEvent,
};
use serde_json::{Value, json};
use std::collections::BTreeSet;
use std::ffi::{CStr, CString, c_char, c_void};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

type Progress = Option<unsafe extern "C" fn(*const c_char, *mut c_void)>;

fn selection() -> CatalogSet {
    CatalogSet::star_distances()
        .with(Dataset::ObjectDistances)
        .with(Dataset::Objects)
}

unsafe fn directory(pointer: *const c_char) -> Result<PathBuf, String> {
    if pointer.is_null() {
        return Ok(seiza::data_paths::default_catalog_dir());
    }
    let value = unsafe { CStr::from_ptr(pointer) }
        .to_str()
        .map_err(|error| error.to_string())?;
    if value.is_empty() {
        return Err("The catalogue directory must not be empty".into());
    }
    Ok(PathBuf::from(value))
}

fn status(directory: Option<&Path>) -> Value {
    // The directory is the setup destination, not necessarily where the core
    // finds each default component. Preserve explicitly configured directory
    // checks, but let upstream resolve environment/executable/legacy defaults.
    // This describes global catalog settings, not per-scene editor overrides.
    let destination = directory
        .map(Path::to_path_buf)
        .unwrap_or_else(seiza::data_paths::default_catalog_dir);
    let stars = match directory {
        Some(directory) => Ok(Some(directory.join("star-distances.bin"))),
        None => seiza::data_paths::star_distances(None),
    };
    let objects = match directory {
        Some(directory) => Ok(Some(directory.join("object-distances.bin"))),
        None => match seiza::data_paths::objects(None) {
            Ok(objects) => seiza::data_paths::object_distances_beside(None, &objects),
            Err(_) => seiza::data_paths::object_distances(None),
        },
    };
    json!({"directory": destination,
    "stars": component_status(stars, destination.join("star-distances.bin"), |path| {
        StarDistanceCatalog::open(path)
            .map(|catalog| json!({"maxMagnitude": catalog.max_mag(), "starCount": catalog.star_count()}))
            .map_err(|error| error.to_string())
    }),
    "objects": component_status(objects, destination.join("object-distances.bin"), |path| {
        ObjectDistances::open_unpaired(path)
            .map(|_| json!({}))
            .map_err(|error| error.to_string())
    })})
}

fn component_status(
    resolved: Result<Option<PathBuf>, seiza::data_paths::DataPathError>,
    missing_path: PathBuf,
    read: impl FnOnce(&Path) -> Result<Value, String>,
) -> Value {
    match resolved {
        Ok(path) => {
            let path = path.unwrap_or(missing_path);
            match read(&path) {
                Ok(mut value) => {
                    value["available"] = true.into();
                    value["path"] = path.to_string_lossy().as_ref().into();
                    value
                }
                Err(error) => json!({"available": false, "path": path,
                "error": if path.exists() { Some(error) } else { None }}),
            }
        }
        // A pinned but missing environment choice is a resolution failure, not
        // corrupt file contents. Do not silently fall back or mislabel it.
        Err(error) => json!({"available": false, "path": missing_path,
            "error": null, "resolutionError": error.to_string()}),
    }
}

fn progress_json(event: DownloadEvent, completed: usize) -> Value {
    let (phase, message, name, bytes) = match event {
        DownloadEvent::FetchingManifest { .. } => (
            "manifest",
            "Checking the mirror for offline distances…".into(),
            None,
            None,
        ),
        DownloadEvent::UsingCachedManifest { version, .. } => (
            "manifest",
            format!("Using catalogue manifest {version}"),
            None,
            None,
        ),
        DownloadEvent::CacheHit { name, .. } => (
            "preparing",
            format!("Using cached {name}"),
            Some(name),
            None,
        ),
        DownloadEvent::DownloadStarted { name, bytes } => (
            "downloading",
            format!("Downloading {name}"),
            Some(name),
            Some((0, bytes, 0)),
        ),
        DownloadEvent::DownloadProgress {
            name,
            downloaded,
            total,
            written,
        } => (
            "downloading",
            format!("Downloading {name}"),
            Some(name),
            Some((downloaded, total, written)),
        ),
        DownloadEvent::DownloadComplete { name, .. } => {
            ("preparing", format!("Downloaded {name}"), Some(name), None)
        }
        DownloadEvent::Verifying { name } => (
            "verifying",
            format!("Verifying SHA-256 for {name}"),
            Some(name),
            None,
        ),
        DownloadEvent::Installing { name, .. } => {
            ("installing", format!("Installing {name}"), Some(name), None)
        }
        DownloadEvent::InstallComplete { name, .. } => {
            ("installing", format!("Installed {name}"), Some(name), None)
        }
    };
    json!({"phase": phase, "message": message, "fileName": name,
        "filesCompleted": completed, "filesTotal": 3,
        "bytesCompleted": bytes.map(|b| b.0), "bytesTotal": bytes.map(|b| b.1), "writtenBytes": bytes.map(|b| b.2)})
}

fn send(callback: Progress, context: usize, value: Value) {
    if let Some(callback) = callback {
        let text = CString::new(value.to_string()).expect("JSON escapes NULs");
        unsafe { callback(text.as_ptr(), context as *mut c_void) };
    }
}

fn download_error(error: seiza_download::Error) -> String {
    if let seiza_download::Error::Manifest(message) = &error
        && let Some(missing) = message.strip_prefix("requested file(s) unavailable: ")
        && missing
            .split(';')
            .next()
            .is_some_and(|names| names.contains("star-distances.bin"))
    {
        return "The stellar distance database is not published on the Seiza mirror yet. Try Download Offline Distances again after the upload finishes.".into();
    }
    error.to_string()
}

/// Remove only a corrupt object returned by this manager's selected bundle.
/// The public downloader supplies both the exact path and expected integrity;
/// never derive an eviction target from an error string or delete a directory.
async fn evict_corrupt_artifact(
    manager: &CatalogManager,
    bundle: &CatalogBundle,
    failure: &seiza_download::Error,
) -> Result<bool, String> {
    use seiza_download::Error;
    let name = match failure {
        Error::Checksum { name, .. } | Error::Size { name, .. } => name,
        _ => return Ok(false),
    };
    if !["star-distances.bin", "object-distances.bin", "objects.bin"].contains(&name.as_str()) {
        return Err("Refusing to repair an artifact outside the parallax selection".into());
    }
    let artifact = bundle
        .artifacts()
        .find(|artifact| &artifact.name == name)
        .ok_or_else(|| "The corrupt artifact is not part of this download".to_string())?;
    match failure {
        Error::Checksum { expected, .. } if expected == &artifact.sha256 => {}
        Error::Size { expected, .. } if *expected == artifact.bytes => {}
        _ => return Err("The corrupt artifact does not match its download metadata".into()),
    }
    // Another setup may already have repaired it while verification completed.
    match seiza_download::bundle::verify_file(
        &artifact.path,
        artifact.bytes,
        &artifact.sha256,
        &artifact.name,
    )
    .await
    {
        Ok(()) => return Ok(true),
        Err(Error::Checksum { .. } | Error::Size { .. }) => {}
        Err(Error::Io { source, .. }) if source.kind() == std::io::ErrorKind::NotFound => {
            return Ok(true);
        }
        Err(error) => return Err(error.to_string()),
    }
    let root = manager
        .cache_dir()
        .canonicalize()
        .map_err(|e| e.to_string())?;
    let target = artifact.path.canonicalize().map_err(|e| e.to_string())?;
    let metadata = std::fs::symlink_metadata(&artifact.path).map_err(|e| e.to_string())?;
    if !metadata.file_type().is_file()
        || !target.starts_with(&root)
        || target == root
        || artifact.path.file_name() != Some(std::ffi::OsStr::new(name))
    {
        return Err(
            "Refusing to repair a cache artifact outside the verified cache directory".into(),
        );
    }
    match std::fs::remove_file(&artifact.path) {
        Ok(()) => Ok(true),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(true),
        Err(error) => Err(format!("Could not remove corrupt cached {name}: {error}")),
    }
}

async fn verified_bundle<F>(manager: &CatalogManager, report: F) -> Result<CatalogBundle, String>
where
    F: Fn(DownloadEvent) + Send + Sync,
{
    let mut repaired = BTreeSet::new();
    loop {
        let bundle = manager
            .ensure_with(&selection(), &report)
            .await
            .map_err(download_error)?;
        match bundle.verify_with(&report).await {
            Ok(()) => return Ok(bundle),
            Err(error) => {
                let name = match &error {
                    seiza_download::Error::Checksum { name, .. }
                    | seiza_download::Error::Size { name, .. } => name,
                    _ => return Err(error.to_string()),
                };
                // At most one retry per selected artifact. Persistent corruption or
                // a broken mirror must fail rather than enter an endless retry loop.
                if !repaired.insert(name.clone())
                    || !evict_corrupt_artifact(manager, &bundle, &error).await?
                {
                    return Err(error.to_string());
                }
            }
        }
    }
}

fn install(directory: &Path, callback: Progress, context: usize) -> Result<(), String> {
    let manager = CatalogManager::builder()
        .policy(CachePolicy::ForceRefresh)
        .build()
        .map_err(|e| e.to_string())?;
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| e.to_string())?;
    runtime.block_on(async {
        // Explicit verification re-hashes cache hits. A same-size corrupt object
        // is evicted narrowly and re-fetched through the official downloader.
        let bundle = verified_bundle(&manager, |event| {
            send(callback, context, progress_json(event, 0))
        })
        .await?;
        let completed = AtomicUsize::new(0);
        bundle
            .materialize_with(directory, |event| {
                if matches!(event, DownloadEvent::InstallComplete { .. }) {
                    completed.fetch_add(1, Ordering::Relaxed);
                }
                send(
                    callback,
                    context,
                    progress_json(event, completed.load(Ordering::Relaxed)),
                );
            })
            .await
            .map_err(|error| error.to_string())?;
        Ok::<_, String>(())
    })?;
    send(
        callback,
        context,
        json!({"phase": "complete", "message": "Offline parallax catalogues installed and verified.", "filesCompleted": 3, "filesTotal": 3}),
    );
    Ok(())
}

fn boundary<T>(
    error_out: *mut *mut c_char,
    operation: impl FnOnce() -> Result<T, String>,
) -> Option<T> {
    if !error_out.is_null() {
        unsafe { *error_out = std::ptr::null_mut() };
    }
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(operation))
        .unwrap_or_else(|_| Err("Catalogue operation failed unexpectedly".into()));
    match result {
        Ok(value) => Some(value),
        Err(error) => {
            if !error_out.is_null() {
                let text = CString::new(error.replace('\0', " ")).expect("NULs removed");
                unsafe { *error_out = text.into_raw() };
            }
            None
        }
    }
}

/// Inspect local distance databases using their native catalogue readers.
/// Returned JSON/error strings are released with seiza_win_string_free.
/// # Safety
/// Directory is null or a NUL-terminated UTF-8 path. error_out is null or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_distance_catalog_status_json(
    directory_pointer: *const c_char,
    error_out: *mut *mut c_char,
) -> *mut c_char {
    boundary(error_out, || {
        let directory = unsafe { directory(directory_pointer) }?;
        let explicit = (!directory_pointer.is_null()).then_some(directory.as_path());
        Ok(CString::new(status(explicit).to_string())
            .expect("JSON escapes NULs")
            .into_raw())
    })
    .unwrap_or(std::ptr::null_mut())
}

/// Install only stellar distances, object distances, and the object catalogue.
/// Synchronous: run on a worker thread outside any Tokio runtime. Callback JSON
/// is borrowed for the callback duration. Failed setup is safe to retry.
/// # Safety
/// Directory is null or a NUL-terminated UTF-8 path; error_out is null or writable.
/// Callback/context must remain valid until this call returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_catalog_setup_parallax(
    directory_pointer: *const c_char,
    callback: Progress,
    context: *mut c_void,
    error_out: *mut *mut c_char,
) -> bool {
    boundary(error_out, || {
        let directory = unsafe { directory(directory_pointer) }?;
        install(&directory, callback, context as usize)
    })
    .is_some()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn parallax_selection_and_compressed_progress_are_precise() {
        let hash = "a".repeat(64);
        let files: Vec<_> = seiza_download::REQUIRED_BUNDLE_FILES
            .iter()
            .copied()
            .chain([
                "star-distances.bin",
                "object-distances.bin",
                "stars-deep-gaia20.bin",
            ])
            .map(|name| {
                json!({"name": name, "bytes": 1, "sha256": hash,
                "key": format!("artifacts/{hash}/{name}")})
            })
            .collect();
        let document = json!({"version": "catalog-bundle-v4-test", "files": files});
        let manifest =
            seiza_download::BundleManifest::parse(&serde_json::to_vec(&document).unwrap()).unwrap();
        let files = manifest.plan(&selection()).unwrap();
        let mut names = files
            .iter()
            .map(|file| file.name.as_str())
            .collect::<Vec<_>>();
        names.sort();
        assert_eq!(
            names,
            ["object-distances.bin", "objects.bin", "star-distances.bin"]
        );
        let mut unpublished = manifest.clone();
        unpublished
            .files
            .retain(|file| file.name != "star-distances.bin");
        let error = unpublished.plan(&selection()).unwrap_err();
        assert!(download_error(error).contains("not published on the Seiza mirror yet"));
        let event = progress_json(
            DownloadEvent::DownloadProgress {
                name: "star-distances.bin".into(),
                downloaded: 20,
                total: 100,
                written: 60,
            },
            0,
        );
        assert_eq!(event["bytesCompleted"], 20);
        assert_eq!(event["writtenBytes"], 60);
        assert_eq!(event["filesTotal"], 3);
    }
    #[test]
    fn status_rejects_corruption_and_reads_star_depth() {
        let temp = tempfile::tempdir().unwrap();
        assert_eq!(status(Some(temp.path()))["stars"]["available"], false);
        std::fs::write(temp.path().join("star-distances.bin"), b"bad").unwrap();
        assert!(status(Some(temp.path()))["stars"]["error"].is_string());
        seiza::catalog::StarDistanceCatalogBuilder::new(16, 2016.0, 17.0, "test")
            .write_to(&temp.path().join("star-distances.bin"))
            .unwrap();
        let result = status(Some(temp.path()));
        assert_eq!(result["stars"]["available"], true);
        assert_eq!(result["stars"]["maxMagnitude"], 17.0);
        assert_eq!(result["stars"]["starCount"], 0);
        assert_eq!(result["objects"]["available"], false);
        std::fs::write(temp.path().join("object-distances.bin"), b"bad").unwrap();
        assert!(status(Some(temp.path()))["objects"]["error"].is_string());
        seiza::catalog::ObjectDistancesBuilder::new("test", [0; 32])
            .write_to(&temp.path().join("object-distances.bin"))
            .unwrap();
        assert_eq!(status(Some(temp.path()))["objects"]["available"], true);
    }

    #[test]
    fn default_status_uses_upstream_paths_without_changing_install_destination() {
        const FIXTURE_ENV: &str = "SEIZA_PLATFORM_STATUS_FIXTURE";
        const MODE_ENV: &str = "SEIZA_PLATFORM_STATUS_MODE";
        if let Some(root) = std::env::var_os(FIXTURE_ENV).map(PathBuf::from) {
            // This branch runs only in an isolated child with its own complete
            // environment and executable directory: no set_var/global races.
            let destination = root.join("destination");
            let mode = std::env::var(MODE_ENV).unwrap();
            let result = status(None);
            let mut error = std::ptr::null_mut();
            let pointer =
                unsafe { seiza_win_distance_catalog_status_json(std::ptr::null(), &mut error) };
            assert!(error.is_null());
            assert!(!pointer.is_null());
            let exported: Value =
                serde_json::from_str(unsafe { CStr::from_ptr(pointer) }.to_str().unwrap()).unwrap();
            unsafe { seiza_win_string_free(pointer) };
            assert_eq!(exported, result);
            assert_eq!(result["directory"], json!(destination));
            assert_eq!(unsafe { directory(std::ptr::null()) }.unwrap(), destination);
            if mode == "missing-override" {
                for key in ["stars", "objects"] {
                    assert_eq!(result[key]["available"], false);
                    assert!(result[key]["resolutionError"].is_string());
                    assert!(result[key]["error"].is_null());
                }
            } else {
                let selected = if mode == "legacy-fallback" {
                    root.join("local-data").join("seiza")
                } else {
                    root.join("overrides")
                };
                assert_eq!(
                    result["stars"]["path"],
                    json!(selected.join("star-distances.bin"))
                );
                assert_eq!(
                    result["objects"]["path"],
                    json!(selected.join("object-distances.bin"))
                );
                for key in ["stars", "objects"] {
                    assert_eq!(result[key]["available"], mode != "corrupt-override");
                    if mode == "corrupt-override" {
                        assert!(result[key]["error"].is_string());
                    }
                }
                if mode != "corrupt-override" {
                    assert_eq!(
                        result["stars"]["maxMagnitude"],
                        if mode == "legacy-fallback" {
                            17.0
                        } else {
                            18.0
                        }
                    );
                }
            }
            let custom = root.join("custom");
            let explicit = status(Some(&custom));
            assert_eq!(explicit["directory"], json!(custom));
            assert_eq!(explicit["stars"]["maxMagnitude"], 19.0);
            assert_eq!(
                explicit["stars"]["path"],
                json!(custom.join("star-distances.bin"))
            );
            assert_eq!(
                explicit["objects"]["path"],
                json!(custom.join("object-distances.bin"))
            );
            assert_eq!(explicit["objects"]["available"], true);
            return;
        }

        fn write_distances(directory: &Path, depth: f32) {
            std::fs::create_dir_all(directory).unwrap();
            seiza::catalog::StarDistanceCatalogBuilder::new(16, 2016.0, depth, "status fixture")
                .write_to(&directory.join("star-distances.bin"))
                .unwrap();
            seiza::catalog::ObjectDistancesBuilder::new("status fixture", [0; 32])
                .write_to(&directory.join("object-distances.bin"))
                .unwrap();
            // Only its location is inspected; status does not parse objects.bin.
            std::fs::write(directory.join("objects.bin"), b"path fixture").unwrap();
        }

        for mode in [
            "legacy-fallback",
            "override",
            "corrupt-override",
            "missing-override",
        ] {
            let root = tempfile::tempdir().unwrap();
            let destination = root.path().join("destination");
            let legacy = root.path().join("local-data").join("seiza");
            let overrides = root.path().join("overrides");
            let custom = root.path().join("custom");
            std::fs::create_dir(&destination).unwrap();
            write_distances(&legacy, 17.0);
            write_distances(&custom, 19.0);
            if mode != "legacy-fallback" {
                write_distances(&destination, 16.0);
                if mode != "missing-override" {
                    write_distances(&overrides, 18.0);
                }
                if mode == "corrupt-override" {
                    std::fs::write(overrides.join("star-distances.bin"), b"bad").unwrap();
                    std::fs::write(overrides.join("object-distances.bin"), b"bad").unwrap();
                }
            }
            let executable = std::env::current_exe().unwrap();
            let bin = root.path().join("bin");
            std::fs::create_dir(&bin).unwrap();
            let isolated_executable = bin.join(executable.file_name().unwrap());
            std::fs::copy(executable, &isolated_executable).unwrap();
            let mut command = std::process::Command::new(isolated_executable);
            command
                .args(["--exact", "tests::default_status_uses_upstream_paths_without_changing_install_destination", "--nocapture"])
                .current_dir(root.path())
                .env(FIXTURE_ENV, root.path())
                .env(MODE_ENV, mode)
                .env("SEIZA_CATALOG_DIR", &destination)
                .env("LOCALAPPDATA", root.path().join("local-data"))
                .env("APPDATA", root.path().join("roaming-data"))
                .env_remove("SEIZA_STAR_DISTANCES")
                .env_remove("SEIZA_OBJECT_DISTANCES");
            if mode != "legacy-fallback" {
                command
                    .env("SEIZA_STAR_DISTANCES", overrides.join("star-distances.bin"))
                    .env(
                        "SEIZA_OBJECT_DISTANCES",
                        overrides.join("object-distances.bin"),
                    );
            }
            let output = command.output().unwrap();
            assert!(
                output.status.success(),
                "{mode}:\n{}\n{}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
        }
    }

    struct OfflineFixture {
        directory: tempfile::TempDir,
        manager: CatalogManager,
    }

    impl OfflineFixture {
        fn new() -> Self {
            let directory = tempfile::tempdir().unwrap();
            let files: Vec<_> = seiza_download::REQUIRED_BUNDLE_FILES
                .iter()
                .copied()
                .chain(["star-distances.bin", "object-distances.bin"])
                .map(|name| {
                    let body = name.as_bytes();
                    let hash = seiza_download::bundle::sha256_hex(body);
                    // Fixture population follows the pinned downloader's v4 layout.
                    // Production eviction uses only paths returned by its public API.
                    let path = directory.path().join("objects").join(&hash).join(name);
                    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
                    std::fs::write(path, body).unwrap();
                    json!({"name": name, "bytes": body.len(), "sha256": hash,
                        "key": format!("artifacts/{hash}/{name}")})
                })
                .collect();
            let manifests = directory.path().join("manifests");
            std::fs::create_dir(&manifests).unwrap();
            std::fs::write(
                manifests.join("catalog-bundle-v4.json"),
                serde_json::to_vec(&json!({"version": "catalog-bundle-v4-test", "files": files}))
                    .unwrap(),
            )
            .unwrap();
            let manager = CatalogManager::builder()
                .cache_dir(directory.path())
                .policy(CachePolicy::OfflineOnly)
                .build()
                .unwrap();
            Self { directory, manager }
        }
    }

    fn run_async(future: impl std::future::Future<Output = ()>) {
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap()
            .block_on(future);
    }

    #[test]
    fn cached_bundle_is_verified_and_installs_exactly_the_selected_files() {
        run_async(async {
            let fixture = OfflineFixture::new();
            let bundle = verified_bundle(&fixture.manager, |_| {}).await.unwrap();
            assert_eq!(bundle.artifacts().len(), 3);
            let output = fixture.directory.path().join("installed");
            bundle.materialize(&output).await.unwrap();
            let mut names = std::fs::read_dir(&output)
                .unwrap()
                .map(|entry| entry.unwrap().file_name().to_str().unwrap().to_string())
                .collect::<Vec<_>>();
            names.sort();
            assert_eq!(
                names,
                ["object-distances.bin", "objects.bin", "star-distances.bin"]
            );
            for artifact in bundle.artifacts() {
                assert_eq!(
                    std::fs::read(output.join(&artifact.name)).unwrap(),
                    artifact.name.as_bytes()
                );
            }
        });
    }

    #[test]
    fn same_size_corrupt_cache_is_evicted_narrowly_and_can_recover() {
        run_async(async {
            let fixture = OfflineFixture::new();
            let bundle = fixture.manager.ensure(&selection()).await.unwrap();
            let bad = bundle.path(Dataset::StarDistances).unwrap().to_path_buf();
            let unrelated = fixture.directory.path().join("keep-this-cache.txt");
            std::fs::write(&unrelated, b"keep").unwrap();
            std::fs::write(&bad, vec![b'x'; "star-distances.bin".len()]).unwrap();
            let error = bundle.verify().await.unwrap_err();
            assert!(matches!(error, seiza_download::Error::Checksum { .. }));
            assert!(
                evict_corrupt_artifact(&fixture.manager, &bundle, &error)
                    .await
                    .unwrap()
            );
            assert!(!bad.exists());
            assert_eq!(std::fs::read(&unrelated).unwrap(), b"keep");
            for artifact in bundle
                .artifacts()
                .filter(|artifact| artifact.name != "star-distances.bin")
            {
                assert_eq!(
                    std::fs::read(&artifact.path).unwrap(),
                    artifact.name.as_bytes()
                );
            }
            // Emulate a subsequent verified transfer without using the network.
            std::fs::write(&bad, b"star-distances.bin").unwrap();
            verified_bundle(&fixture.manager, |_| {}).await.unwrap();
        });
    }

    #[test]
    fn truncated_cache_is_evicted_and_non_integrity_errors_never_evict() {
        run_async(async {
            let fixture = OfflineFixture::new();
            let bundle = fixture.manager.ensure(&selection()).await.unwrap();
            let bad = bundle.path(Dataset::ObjectDistances).unwrap();
            std::fs::write(bad, b"short").unwrap();
            let error = bundle.verify().await.unwrap_err();
            assert!(matches!(error, seiza_download::Error::Size { .. }));
            assert!(
                evict_corrupt_artifact(&fixture.manager, &bundle, &error)
                    .await
                    .unwrap()
            );
            assert!(!bad.exists());
            let good = bundle.path(Dataset::Objects).unwrap();
            assert!(
                !evict_corrupt_artifact(
                    &fixture.manager,
                    &bundle,
                    &seiza_download::Error::Manifest("unavailable".into())
                )
                .await
                .unwrap()
            );
            assert_eq!(std::fs::read(good).unwrap(), b"objects.bin");
        });
    }

    #[test]
    fn concurrent_repair_is_preserved_and_unrelated_names_are_rejected() {
        run_async(async {
            let fixture = OfflineFixture::new();
            let bundle = fixture.manager.ensure(&selection()).await.unwrap();
            let name = "star-distances.bin";
            let artifact = bundle
                .artifacts()
                .find(|artifact| artifact.name == name)
                .unwrap();
            let obsolete_error = seiza_download::Error::Checksum {
                name: name.into(),
                expected: artifact.sha256.clone(),
                actual: "b".repeat(64),
            };
            assert!(
                evict_corrupt_artifact(&fixture.manager, &bundle, &obsolete_error)
                    .await
                    .unwrap()
            );
            assert_eq!(std::fs::read(&artifact.path).unwrap(), name.as_bytes());
            let unrelated = seiza_download::Error::Checksum {
                name: "stars-deep-gaia20.bin".into(),
                expected: "a".repeat(64),
                actual: "b".repeat(64),
            };
            assert!(
                evict_corrupt_artifact(&fixture.manager, &bundle, &unrelated)
                    .await
                    .is_err()
            );
            assert_eq!(std::fs::read(&artifact.path).unwrap(), name.as_bytes());
        });
    }

    #[test]
    fn repair_rejects_mismatched_metadata_and_a_different_cache_root() {
        run_async(async {
            let fixture = OfflineFixture::new();
            let other = OfflineFixture::new();
            let bundle = fixture.manager.ensure(&selection()).await.unwrap();
            let artifact = bundle
                .artifacts()
                .find(|artifact| artifact.name == "star-distances.bin")
                .unwrap();
            let corrupt = vec![b'x'; artifact.bytes as usize];
            std::fs::write(&artifact.path, &corrupt).unwrap();
            let error = bundle.verify().await.unwrap_err();
            assert!(
                evict_corrupt_artifact(&other.manager, &bundle, &error)
                    .await
                    .is_err()
            );
            assert_eq!(std::fs::read(&artifact.path).unwrap(), corrupt);
            let mismatched = seiza_download::Error::Checksum {
                name: artifact.name.clone(),
                expected: "b".repeat(64),
                actual: "c".repeat(64),
            };
            assert!(
                evict_corrupt_artifact(&fixture.manager, &bundle, &mismatched)
                    .await
                    .is_err()
            );
            assert_eq!(std::fs::read(&artifact.path).unwrap(), corrupt);
        });
    }
}

/// Releases strings allocated by this Windows adapter (never by seiza-cabi).
/// # Safety
/// `value` is null or a pointer returned by this adapter, released exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_string_free(value: *mut c_char) {
    if !value.is_null() {
        drop(unsafe { CString::from_raw(value) });
    }
}
