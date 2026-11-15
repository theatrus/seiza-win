//! File and lifetime adapter for the Windows learned restoration workspace.
use crate::{Progress, boundary, send};
use seiza_fits::{HeaderValue, WriteHeaderCard};
use seiza_model::{
    Backend, BackendOptions, DenoiseOptions, DenoiseReport, Denoised, ImageView, ModelBundle,
    NoiseBands, NoiseModel, runtime_info,
};
use seiza_stacking::{FitsFrame, LinearImage, write_processed_image_fits_f32};
use seiza_stretch::{SampleDomain, StretchConfig, StretchParams};
use serde::{Deserialize, de::DeserializeOwned};
use serde_json::json;
use std::ffi::{CStr, CString, c_char, c_void};
use std::path::{Path, PathBuf};
use std::ptr;

type Cancel = Option<unsafe extern "C" fn(*mut c_void) -> u8>;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    input: PathBuf,
    model: PathBuf,
    library: PathBuf,
    backend: Option<String>,
    precision: Option<String>,
    tile: Option<usize>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Blend {
    output: PathBuf,
    original_preview: Option<PathBuf>,
    high: f32,
    low: f32,
    scale_px: f32,
}

/// A completed inference and the metadata needed to blend and save it.
pub struct Restoration {
    headers: Vec<(String, HeaderValue)>,
    original_preview: LinearImage,
    result: Denoised,
}

fn parse<T: DeserializeOwned>(request: &CStr) -> Result<T, String> {
    serde_json::from_slice(request.to_bytes()).map_err(|error| error.to_string())
}

fn owned(value: serde_json::Value) -> *mut c_char {
    CString::new(value.to_string())
        .expect("JSON escapes NUL")
        .into_raw()
}

fn run(
    request: Request,
    progress: Progress,
    cancel: Cancel,
    context: *mut c_void,
) -> Result<Restoration, String> {
    let context = context as usize;
    let stopped = || cancel.is_some_and(|test| unsafe { test(context as *mut c_void) != 0 });
    let mut frame = FitsFrame::open(&request.input).map_err(|error| error.to_string())?;
    if frame.bayer.is_some() || !matches!(frame.image.channels, 1 | 3) {
        return Err("Choose a prepared linear mono or RGB image. Debayer raw images first.".into());
    }
    if frame.headers.iter().any(|(key, value)| {
        key.eq_ignore_ascii_case("SEIZATRF") && value.as_str() == Some("DISPLAY")
    }) {
        return Err("Choose a linear FITS or XISF image for restoration.".into());
    }
    if stopped() {
        return Err("Restoration cancelled.".into());
    }
    let bundle = ModelBundle::open(&request.model).map_err(|error| error.to_string())?;
    let options = BackendOptions {
        library: Some(request.library),
        precision: request
            .precision
            .as_deref()
            .map(str::parse)
            .transpose()
            .map_err(|error: seiza_model::UnknownPrecision| error.to_string())?,
        tile: request.tile,
        ..Default::default()
    };
    let backend: Backend = match request.backend {
        Some(name) => name
            .parse()
            .map_err(|error: seiza_model::UnknownBackend| error.to_string())?,
        None => Backend::detect(&bundle, &options).map_err(|error| error.to_string())?,
    };
    send(
        progress,
        context,
        json!({"message": format!("Opening model on {backend}…")}),
    );
    let mut model =
        NoiseModel::open(&bundle, backend, &options).map_err(|error| error.to_string())?;
    for sample in &mut frame.image.data {
        if !sample.is_finite() {
            *sample = f32::NAN;
        }
    }
    let image = &frame.image;
    let input = ImageView::new(&image.data, image.width, image.height, image.channels)
        .map_err(|error| error.to_string())?;
    let report = |step: seiza_model::Progress| {
        send(
            progress,
            context,
            json!({
                "message": format!("Channel {} of {} · tile {} of {}", step.channel + 1, step.channels, step.tile, step.tiles),
                "fraction": (step.channel as f64 + step.tile as f64 / step.tiles as f64) / step.channels as f64,
            }),
        );
    };
    let result = model
        .denoise(
            input,
            &DenoiseOptions {
                progress: Some(&report),
                cancel: Some(&stopped),
            },
        )
        .map_err(|error| error.to_string())?;
    let original_preview = crop(&frame.image)?;
    Ok(Restoration {
        headers: frame.headers,
        original_preview,
        result,
    })
}

fn cards(report: &DenoiseReport, bands: NoiseBands) -> Vec<WriteHeaderCard> {
    let text = |key: &str, value: String| WriteHeaderCard::new(key, HeaderValue::String(value));
    let number =
        |key: &str, value: f32| WriteHeaderCard::new(key, HeaderValue::Float(f64::from(value)));
    let mut cards = vec![
        text("SEIZATRF", "LINEAR".into()),
        text("SEIZAMDL", report.model.clone()),
        text("MDHASH", report.sha256.chars().take(16).collect()),
        text("MDCARD", report.card_sha256.chars().take(16).collect()),
        text("MDBACKND", report.backend.to_string()),
        text("MDPREC", report.precision.to_string()),
        text("MDNOISE", report.noise_map.to_string()),
        text("MDVERS", seiza_model::OUTPUT_VERSION.into()),
        WriteHeaderCard::new("MDTILE", HeaderValue::Integer(report.tile as i64)),
        number("MDHIGH", bands.high),
        number("MDLOW", bands.low),
        number("MDSCALE", bands.scale_px),
    ];
    if let Some(guard) = &report.holes {
        cards.push(WriteHeaderCard::new(
            "MDHOLES",
            HeaderValue::Float(guard.depth),
        ));
    }
    cards
}

fn crop(image: &LinearImage) -> Result<LinearImage, String> {
    const SIDE: usize = 768;
    let width = image.width.min(SIDE);
    let height = image.height.min(SIDE);
    let left = (image.width - width) / 2;
    let top = (image.height - height) / 2;
    let channels = image.channels;
    let mut data = Vec::with_capacity(width * height * channels);
    for row in top..top + height {
        let start = (row * image.width + left) * channels;
        data.extend_from_slice(&image.data[start..start + width * channels]);
    }
    LinearImage::new(width, height, channels, data).map_err(|error| error.to_string())
}

fn write_comparison(
    original: &LinearImage,
    restored: &LinearImage,
    original_path: &Path,
    restored_path: &Path,
) -> Result<(), String> {
    let channels = original.channels;
    let domain = SampleDomain::PhysicalLinear {
        normalization: Default::default(),
    }
    .resolve(&original.data, channels)
    .map_err(|error| error.to_string())?;
    let original_data = domain
        .apply(&original.data, channels)
        .map_err(|error| error.to_string())?;
    let restored_data = domain
        .apply(&restored.data, channels)
        .map_err(|error| error.to_string())?;
    let plan = StretchConfig::auto_mtf(StretchParams::default(), 200_000)
        .resolve_for(&original_data, channels)
        .map_err(|error| error.to_string())?;
    let cards = [WriteHeaderCard::new(
        "SEIZATRF",
        HeaderValue::String("DISPLAY".into()),
    )];
    for (data, path) in [
        (original_data, original_path),
        (restored_data, restored_path),
    ] {
        let pixels = plan
            .apply_f32(&data, channels)
            .map_err(|error| error.to_string())?;
        let display = LinearImage::new(original.width, original.height, channels, pixels)
            .map_err(|error| error.to_string())?;
        write_processed_image_fits_f32(path, &display, &[], &cards)
            .map_err(|error| error.to_string())?;
    }
    Ok(())
}

/// List the available backends for the installed runtime.
/// # Safety
/// `library` is a valid UTF-8 C string. `error` is null or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_restoration_backends(
    library: *const c_char,
    error: *mut *mut c_char,
) -> *mut c_char {
    boundary(error, || {
        let library = unsafe { CStr::from_ptr(library) }.to_str().map_err(|error| error.to_string())?;
        let options = BackendOptions { library: Some(library.into()), ..Default::default() };
        let runtime = runtime_info(&options).map_err(|error| error.to_string())?;
        let backends: Vec<_> = Backend::available(&options).into_iter()
            .map(|status| json!({"name": status.backend.to_string(), "unavailable": status.unavailable}))
            .collect();
        Ok(owned(json!({"version": runtime.version, "backends": backends})))
    }).unwrap_or(ptr::null_mut())
}

/// Run restoration and return the computed residual as an owned handle.
/// # Safety
/// Request is a valid UTF-8 C string. Callback context is valid through this call.
/// Error is null or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_restoration_run(
    request: *const c_char,
    progress: Progress,
    cancel: Cancel,
    context: *mut c_void,
    error: *mut *mut c_char,
) -> *mut Restoration {
    boundary(error, || {
        let request = parse(unsafe { CStr::from_ptr(request) })?;
        Ok(Box::into_raw(Box::new(run(
            request, progress, cancel, context,
        )?)))
    })
    .unwrap_or(ptr::null_mut())
}

/// Blend the residual and write a full output or a pair of preview crops.
/// # Safety
/// `result` is a live handle. Request is a valid UTF-8 C string. Error is null or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_restoration_blend(
    result: *const Restoration,
    request: *const c_char,
    error: *mut *mut c_char,
) -> *mut c_char {
    boundary(error, || {
        let result = unsafe { &*result };
        let request: Blend = parse(unsafe { CStr::from_ptr(request) })?;
        let bands = NoiseBands { high: request.high, low: request.low, scale_px: request.scale_px };
        let processed = &result.result;
        let data = processed.blend(&bands).map_err(|error| error.to_string())?;
        let image = LinearImage::new(processed.width(), processed.height(), processed.channels(), data)
            .map_err(|error| error.to_string())?;
        let report = &result.result.report;
        if let Some(original) = &request.original_preview {
            write_comparison(&result.original_preview, &crop(&image)?, original, &request.output)?;
        } else {
            write_processed_image_fits_f32(&request.output, &image, &result.headers, &cards(report, bands))
                .map_err(|error| error.to_string())?;
        }
        Ok(owned(json!({"backend": report.backend.to_string(), "precision": report.precision.to_string(), "tile": report.tile})))
    }).unwrap_or(ptr::null_mut())
}

/// Release an owned restoration result.
/// # Safety
/// The pointer is null or an owned handle returned by `seiza_win_restoration_run`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_restoration_free(result: *mut Restoration) {
    if !result.is_null() {
        unsafe { drop(Box::from_raw(result)) };
    }
}
