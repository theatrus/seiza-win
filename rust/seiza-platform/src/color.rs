//! File/workflow adapter only: all photometry, WCS and pixel operations are
//! implemented by the published Seiza crates, not by the Windows host.
use crate::{Progress, boundary, directory, progress_json, send};
use seiza_fits::{HeaderValue, WriteHeaderCard};
use seiza_stacking::{ColorCalibrationOptions, ColorCalibrationOutcome, FitsFrame, GaiaColors};
use serde::Deserialize;
use serde_json::json;
use std::ffi::{CStr, CString, c_char, c_void};
use std::path::PathBuf;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    input: PathBuf,
    output: PathBuf,
    catalog: Option<PathBuf>,
    wcs_headers: Option<serde_json::Map<String, serde_json::Value>>,
    #[serde(default = "yes")]
    neutralize_background: bool,
}
fn yes() -> bool {
    true
}

fn calibrate(request: Request) -> Result<serde_json::Value, String> {
    if seiza_stacking::paths_refer_to_same_file(&request.input, &request.output) {
        return Err(
            "Color calibration must write a separate output, never replace its input.".into(),
        );
    }
    if !matches!(
        request
            .output
            .extension()
            .and_then(|s| s.to_str())
            .map(str::to_ascii_lowercase)
            .as_deref(),
        Some("fits" | "fit" | "fts" | "xisf")
    ) {
        return Err("Choose a FITS or XISF output for linear calibrated data.".into());
    }
    let mut frame = FitsFrame::open(&request.input).map_err(|e| e.to_string())?;
    if frame
        .headers
        .iter()
        .any(|(key, value)| key == "SEIZAPCC" && value.as_str() == Some("GAIA"))
    {
        return Err(
            "This image is already Gaia color calibrated. Use the original linear stack.".into(),
        );
    }
    let supplied: Option<Vec<(String, HeaderValue)>> = request.wcs_headers.map(|cards| {
        cards
            .into_iter()
            .map(|(key, value)| {
                let value = match value {
                    serde_json::Value::String(text) => HeaderValue::String(text),
                    serde_json::Value::Number(number) => {
                        HeaderValue::Float(number.as_f64().unwrap_or(f64::NAN))
                    }
                    _ => HeaderValue::Raw(String::new()),
                };
                (key, value)
            })
            .collect()
    });
    let wcs =
        seiza_image_io::solve_prior_from_headers(supplied.as_ref().unwrap_or(&frame.headers)).wcs;
    let outcome = seiza_stacking::calibrate_color_image(
        &mut frame.image,
        wcs.as_ref(),
        GaiaColors::CatalogPath(request.catalog.as_deref()),
        &ColorCalibrationOptions {
            neutralize_background: request.neutralize_background,
            ..Default::default()
        },
    );
    let result = match &outcome {
        ColorCalibrationOutcome::Skipped(skip) => {
            json!({"schemaVersion":1, "status":"skipped", "reason":skip.reason, "detail":skip.detail})
        }
        ColorCalibrationOutcome::Calibrated(calibration) => {
            // Linear output, with a persistent marker telling the shell to link
            // RGB stretches on preview, reopen and display export.
            let mut cards = vec![WriteHeaderCard::new(
                "SEIZAPCC",
                HeaderValue::String("GAIA".into()),
            )];
            if let Some(wcs) = &wcs {
                cards.extend(wcs.fits_header_cards().into_iter().map(|(key, value)| {
                    WriteHeaderCard::new(
                        key,
                        match value {
                            seiza::FitsCardValue::Text(text) => HeaderValue::String(text.into()),
                            seiza::FitsCardValue::Integer(number) => {
                                HeaderValue::Integer(number.into())
                            }
                            seiza::FitsCardValue::Number(number) => HeaderValue::Float(number),
                        },
                    )
                }));
            }
            seiza_stacking::write_processed_image_fits_f32(
                &request.output,
                &frame.image,
                &frame.headers,
                &cards,
            )
            .map_err(|e| e.to_string())?;
            json!({"schemaVersion":1, "status":"calibrated", "calibration":calibration})
        }
    };
    Ok(result)
}

/// Calibrate a solved linear RGB FITS/XISF image into a separate linear file.
/// Skipped outcomes leave the destination untouched. Free all returned strings
/// with seiza_win_string_free. No online queries are made.
/// # Safety
/// request is NUL-terminated UTF-8 JSON; error_out is null or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_color_calibrate_file_json(
    request: *const c_char,
    error_out: *mut *mut c_char,
) -> *mut c_char {
    boundary(error_out, || {
        if request.is_null() {
            return Err("A color calibration request is required.".into());
        }
        let request: Request =
            serde_json::from_slice(unsafe { CStr::from_ptr(request) }.to_bytes())
                .map_err(|e| e.to_string())?;
        Ok(CString::new(calibrate(request)?.to_string())
            .expect("JSON escapes NUL")
            .into_raw())
    })
    .unwrap_or(std::ptr::null_mut())
}

/// Install the optional offline Gaia photometry catalogue on explicit request.
/// # Safety
/// Same path/callback lifetime contract as the distance-catalog installer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_catalog_setup_photometry(
    path: *const c_char,
    callback: Progress,
    context: *mut c_void,
    error_out: *mut *mut c_char,
) -> bool {
    let context = context as usize;
    boundary(error_out, || {
        let path = unsafe { directory(path) }?;
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .map_err(|e| e.to_string())?;
        runtime.block_on(async {
            let manager = seiza_download::CatalogManager::builder()
                .build()
                .map_err(|e| e.to_string())?;
            let bundle = manager
                .ensure_with(&seiza_download::CatalogSet::gaia_photometry(), |event| {
                    send(callback, context, photometry_progress(event))
                })
                .await
                .map_err(|e| e.to_string())?;
            bundle
                .verify_with(|event| send(callback, context, photometry_progress(event)))
                .await
                .map_err(|e| e.to_string())?;
            bundle
                .materialize_with(&path, |event| {
                    send(callback, context, photometry_progress(event))
                })
                .await
                .map_err(|e| e.to_string())?;
            Ok(())
        })
    })
    .is_some()
}

fn photometry_progress(event: seiza_download::DownloadEvent) -> serde_json::Value {
    let completed = usize::from(matches!(
        event,
        seiza_download::DownloadEvent::InstallComplete { .. }
    ));
    let manifest = matches!(
        event,
        seiza_download::DownloadEvent::FetchingManifest { .. }
    );
    let mut value = progress_json(event, completed);
    value["filesTotal"] = json!(1);
    if manifest {
        value["message"] = json!("Checking the mirror for offline Gaia photometry…");
    }
    value
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn file_workflow_recovers_gaia_color_and_preserves_wcs_and_marker() {
        let dir = tempfile::tempdir().unwrap();
        let (width, height) = (1200, 1000);
        let wcs =
            seiza::Wcs::from_center_scale_rotation((120., 30.), (599.5, 499.5), 2., 0., false);
        let mut data: Vec<f32> = (0..width * height * 3)
            .map(|i| [120., 100., 90.][i % 3] + ((i * 7919) % 13) as f32 * 0.3)
            .collect();
        let mut catalog =
            seiza::catalog::PhotometryCatalogBuilder::new(90, 2016., "color workflow test");
        for i in 0..140 {
            let x = 30. + ((i * 7919) % 1140) as f64 + (i % 7) as f64 * 0.13;
            let y = 30. + ((i * 6271) % 940) as f64 + (i % 5) as f64 * 0.17;
            let color = 0.1 + ((i * 37) % 160) as f32 / 100.;
            let g = 9. + ((i * 13) % 40) as f32 / 10.;
            let delta = f64::from(color - seiza_stacking::SOLAR_BP_RP);
            let ratios = [
                0.6 * 10f64.powf(0.2 * delta),
                1.,
                1.4 * 10f64.powf(-0.32 * delta),
            ];
            let flux = 2e5 * 10f64.powf(-0.4 * f64::from(g - 9.));
            for py in y as usize - 10..y as usize + 11 {
                for px in x as usize - 10..x as usize + 11 {
                    let r2 = (px as f64 - x).powi(2) + (py as f64 - y).powi(2);
                    let profile = (-r2 / 4.5).exp() / (4.5 * std::f64::consts::PI);
                    for channel in 0..3 {
                        data[(py * width + px) * 3 + channel] +=
                            (flux * ratios[channel] * profile) as f32;
                    }
                }
            }
            let (ra, dec) = wcs.pixel_to_world(x, y);
            catalog.add(seiza::catalog::PhotometricStar {
                ra,
                dec,
                g,
                bp_rp: Some(color),
                reliable: true,
            });
        }
        catalog
            .write_to(&dir.path().join("stars-gaia-photometry.bin"))
            .unwrap();
        let input = dir.path().join("source.fits");
        let image = seiza_stacking::LinearImage::new(width, height, 3, data).unwrap();
        seiza_stacking::write_linear_image_fits_f32(&input, &image, &[], &[]).unwrap();
        let original = std::fs::read(&input).unwrap();
        let headers: serde_json::Map<String, serde_json::Value> = wcs
            .fits_header_cards()
            .into_iter()
            .map(|(key, value)| {
                (
                    key,
                    match value {
                        seiza::FitsCardValue::Text(text) => json!(text),
                        seiza::FitsCardValue::Integer(number) => json!(number),
                        seiza::FitsCardValue::Number(number) => json!(number),
                    },
                )
            })
            .collect();
        for extension in ["fits", "xisf"] {
            let output = dir.path().join(format!("calibrated.{extension}"));
            let result = calibrate(Request {
                input: input.clone(),
                output: output.clone(),
                catalog: Some(dir.path().to_owned()),
                wcs_headers: Some(headers.clone()),
                neutralize_background: true,
            })
            .unwrap();
            assert_eq!(result["status"], "calibrated", "{result}");
            let gains = result["calibration"]["gains"].as_array().unwrap();
            assert!((gains[0].as_f64().unwrap() - 1. / 0.6).abs() < 0.03);
            assert!((gains[2].as_f64().unwrap() - 1. / 1.4).abs() < 0.03);
            assert!(
                seiza_image_io::read_header(&output)
                    .unwrap()
                    .cards
                    .iter()
                    .any(|(key, value)| key == "SEIZAPCC" && value.as_str() == Some("GAIA"))
            );
            let frame = FitsFrame::open(&output).unwrap();
            let restored = seiza_image_io::solve_prior_from_headers(&frame.headers)
                .wcs
                .unwrap();
            assert!((restored.crpix.0 - wcs.crpix.0).abs() < 1e-9);
            assert_eq!(frame.image.channels, 3);
            assert!(
                calibrate(Request {
                    input: output,
                    output: dir.path().join("twice.fits"),
                    catalog: None,
                    wcs_headers: None,
                    neutralize_background: true
                })
                .is_err()
            );
        }
        assert_eq!(std::fs::read(&input).unwrap(), original);
    }
    #[test]
    fn skipped_calibration_does_not_publish_or_replace_an_output() {
        let dir = tempfile::tempdir().unwrap();
        let input = dir.path().join("rgb.fits");
        let output = dir.path().join("output.fits");
        let image = seiza_stacking::LinearImage::new(8, 8, 3, vec![100.; 192]).unwrap();
        seiza_stacking::write_linear_image_fits_f32(&input, &image, &[], &[]).unwrap();
        std::fs::write(&output, b"keep existing output").unwrap();
        let result = calibrate(Request {
            input: input.clone(),
            output: output.clone(),
            catalog: None,
            wcs_headers: None,
            neutralize_background: true,
        })
        .unwrap();
        assert_eq!(result["reason"], "no_solution");
        assert_eq!(std::fs::read(&output).unwrap(), b"keep existing output");
        assert!(
            calibrate(Request {
                input: input.clone(),
                output: input,
                catalog: None,
                wcs_headers: None,
                neutralize_background: true
            })
            .is_err()
        );
    }
}
