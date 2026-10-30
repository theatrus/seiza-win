//! File orchestration for the published color operations; all pixel mathematics
//! and fit validation stay in Seiza. The managed host publishes staged outputs.
use seiza_fits::{HeaderValue, WriteHeaderCard};
use seiza_stacking::{
    ColorCrop, ColorNormalization, ColorOptions, ColorTransfer, ContinuumFitOptions,
    ContinuumOrigin, ContinuumPhotometryOptions, ContinuumPlan, ContinuumPsfOptions,
    ContinuumSource, ContinuumSources, ContinuumTransfer, FitsFrame, ForaxxOptions, LinearImage,
    NarrowbandBalanceOptions, NarrowbandPalette,
};
use seiza_stretch::{SampleDomain, StretchConfig, StretchParams, StretchStack};
use serde::Deserialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::ffi::{CStr, CString, c_char};
use std::io::Read;
use std::path::{Path, PathBuf};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    operation: String,
    inputs: Vec<PathBuf>,
    output: Option<PathBuf>,
    #[serde(default)]
    input_display: bool,
    #[serde(default = "yes")]
    nebula_display: bool,
    #[serde(default = "hoo")]
    palette: String,
    #[serde(default = "median")]
    target_median: f64,
    #[serde(default = "yes")]
    balance: bool,
    #[serde(default)]
    balance_options: NarrowbandBalanceOptions,
    #[serde(default)]
    green_reduction: f64,
    structure: Option<PathBuf>,
    #[serde(default)]
    structure_amount: f64,
    #[serde(default)]
    saturations: Vec<Option<f64>>,
    #[serde(default)]
    clipping_masks: Vec<Option<PathBuf>>,
    #[serde(default)]
    psf_matching: bool,
    manual_coefficients: Option<Vec<f64>>,
    plan: Option<ContinuumPlan>,
    #[serde(default = "star_amount")]
    star_amount: f64,
    #[serde(default)]
    star_reduction: f64,
}
fn yes() -> bool {
    true
}
fn hoo() -> String {
    "hoo".into()
}
fn median() -> f64 {
    0.2
}
fn star_amount() -> f64 {
    5.0
}

fn hash(path: &Path) -> Result<String, String> {
    let mut input = std::fs::File::open(path).map_err(|e| e.to_string())?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 65_536];
    loop {
        let n = input.read(&mut buffer).map_err(|e| e.to_string())?;
        if n == 0 {
            break;
        }
        digest.update(&buffer[..n]);
    }
    let mut text = String::with_capacity(64);
    for byte in digest.finalize() {
        use std::fmt::Write;
        write!(text, "{byte:02x}").expect("writing to a string");
    }
    Ok(text)
}

struct Input {
    path: PathBuf,
    revision: String,
    frame: FitsFrame,
}
fn load(path: &Path) -> Result<Input, String> {
    let revision = hash(path)?;
    let frame = FitsFrame::open(path).map_err(|e| e.to_string())?;
    if frame.bayer.is_some() {
        return Err("Use prepared mono or RGB stacks, not raw Bayer frames.".into());
    }
    if hash(path)? != revision {
        return Err("An input changed while it was read. Use stable prepared images.".into());
    }
    Ok(Input {
        path: path.to_owned(),
        revision,
        frame,
    })
}
fn display_marker(frame: &FitsFrame) -> bool {
    frame
        .headers
        .iter()
        .any(|(key, value)| key == "SEIZATRF" && value.as_str() == Some("DISPLAY"))
}
fn require_linear(input: &Input) -> Result<(), String> {
    if display_marker(&input.frame) {
        return Err("This operation needs linear sources; an input is marked DISPLAY.".into());
    }
    Ok(())
}
fn require_display(input: &Input) -> Result<(), String> {
    if input
        .frame
        .headers
        .iter()
        .any(|(key, value)| key == "SEIZATRF" && value.as_str() == Some("LINEAR"))
    {
        return Err(
            "An input marked LINEAR cannot be treated as an already stretched display image."
                .into(),
        );
    }
    bounded(&input.frame.image)
}
fn grid(inputs: &[Input], channels: usize) -> Result<(), String> {
    let first = inputs.first().ok_or("Choose input images.")?;
    if inputs.iter().any(|input| {
        input.frame.image.channels != channels
            || (input.frame.image.width, input.frame.image.height)
                != (first.frame.image.width, first.frame.image.height)
    }) {
        return Err(format!(
            "Inputs must have {channels} channel(s) on one aligned pixel grid."
        ));
    }
    Ok(())
}
fn bounded(image: &LinearImage) -> Result<(), String> {
    if image
        .data
        .iter()
        .any(|v| v.is_finite() && !(0.0..=1.0).contains(v))
    {
        return Err("Display inputs must be stretched values in [0, 1].".into());
    }
    Ok(())
}
fn stretch(image: &LinearImage, target: f64) -> Result<(LinearImage, Value), String> {
    if !target.is_finite() || !(0.0..1.0).contains(&target) || target == 0.0 {
        return Err("Target median must be between zero and one.".into());
    }
    let domain = SampleDomain::PhysicalLinear {
        normalization: Default::default(),
    }
    .resolve(&image.data, image.channels)
    .map_err(|e| e.to_string())?;
    let normalized = domain
        .apply(&image.data, image.channels)
        .map_err(|e| e.to_string())?;
    let stack = StretchStack::single(StretchConfig::auto_mtf(
        StretchParams {
            target_median: target,
            ..Default::default()
        },
        200_000,
    ));
    let mut data = stack
        .apply_f32(&normalized, image.channels)
        .map_err(|e| e.to_string())?
        .data;
    // Preserve whole-pixel missing coverage if a display curve maps NaNs.
    for (out, original) in data
        .chunks_mut(image.channels)
        .zip(image.data.chunks(image.channels))
    {
        if original.iter().any(|v| !v.is_finite()) {
            out.fill(f32::NAN);
        }
    }
    Ok((
        LinearImage::new(image.width, image.height, image.channels, data)
            .map_err(|e| e.to_string())?,
        json!({"sampleDomain":domain, "targetMedian":target, "model":"auto-mtf", "colorStrategy":"linked"}),
    ))
}

fn write(
    path: &Path,
    image: &LinearImage,
    source: &Input,
    display: bool,
    operation: &str,
) -> Result<(), String> {
    let mut cards = vec![
        WriteHeaderCard::new(
            "SEIZATRF",
            HeaderValue::String(if display { "DISPLAY" } else { "LINEAR" }.into()),
        ),
        WriteHeaderCard::new("SEIZACLR", HeaderValue::String(operation.into())),
        WriteHeaderCard::new("SEIZASHA", HeaderValue::String(source.revision.clone())),
        WriteHeaderCard::new(
            "SEIZAOUT",
            HeaderValue::String(seiza_stacking::OUTPUT_VERSION.into()),
        ),
    ];
    if display {
        cards.push(WriteHeaderCard::new(
            "BUNIT",
            HeaderValue::String("relative display".into()),
        ));
    }
    seiza_stacking::write_processed_image_fits_f32(path, image, &source.frame.headers, &cards)
        .map_err(|e| e.to_string())
}

fn narrowband(request: &Request, inputs: &[Input]) -> Result<(LinearImage, Value), String> {
    let palette = match request.palette.as_str() {
        "hoo" if inputs.len() == 2 => NarrowbandPalette::Hoo,
        "sho" if inputs.len() == 3 => NarrowbandPalette::Sho,
        _ => return Err("HOO needs H-alpha/OIII; SHO needs H-alpha/OIII/SII.".into()),
    };
    grid(inputs, 1)?;
    let first = &inputs[0].frame.image;
    let mut data = Vec::with_capacity(first.data.len() * 3);
    for index in 0..first.data.len() {
        data.extend([
            first.data[index],
            inputs[1].frame.image.data[index],
            inputs.get(2).unwrap_or(&inputs[1]).frame.image.data[index],
        ]);
    }
    let packed = LinearImage::new(first.width, first.height, 3, data).map_err(|e| e.to_string())?;
    let (mut packed, preparation) = if request.input_display {
        for input in inputs {
            require_display(input)?;
        }
        bounded(&packed)?;
        (packed, json!({"transfer":"already-display"}))
    } else {
        for input in inputs {
            require_linear(input)?;
        }
        stretch(&packed, request.target_median)?
    };
    let mut balance = request.balance_options;
    balance.has_sii = palette.requires_sii();
    let report = if request.balance {
        Some(seiza_stacking::balance_narrowband(&mut packed, &balance).map_err(|e| e.to_string())?)
    } else {
        None
    };
    let plane = |channel| {
        LinearImage::new(
            first.width,
            first.height,
            1,
            packed
                .data
                .as_chunks::<3>()
                .0
                .iter()
                .map(|pixel| pixel[channel])
                .collect(),
        )
    };
    let ha = plane(0).map_err(|e| e.to_string())?;
    let oiii = plane(1).map_err(|e| e.to_string())?;
    let sii = plane(2).map_err(|e| e.to_string())?;
    let mut image = seiza_stacking::combine_narrowband(
        &ha,
        &oiii,
        palette.requires_sii().then_some(&sii),
        palette,
        &ColorOptions {
            normalization: ColorNormalization::None,
            input_transfer: ColorTransfer::DisplayReferred,
            crop: ColorCrop::None,
        },
        &ForaxxOptions::default(),
    )
    .map_err(|e| e.to_string())?
    .image;
    if let Some(path) = &request.structure {
        let structure = load(path)?;
        require_display(&structure)?;
        image = seiza_stacking::blend_display_structure(
            &image,
            &structure.frame.image,
            request.structure_amount,
        )
        .map_err(|e| e.to_string())?;
    }
    seiza_stacking::reduce_green_excess(&mut image, request.green_reduction)
        .map_err(|e| e.to_string())?;
    Ok((
        image,
        json!({"preparation":preparation,"palette":palette.name(),"balance":report,"balanceOptions":balance,
        "greenReduction":request.green_reduction,"structureAmount":request.structure_amount}),
    ))
}

fn continuum(
    request: &Request,
    inputs: &[Input],
    analyze: bool,
) -> Result<(Option<LinearImage>, Value), String> {
    if !(2..=4).contains(&inputs.len()) {
        return Err(
            "Continuum fitting needs a mono narrowband image and one to three mono references."
                .into(),
        );
    }
    grid(inputs, 1)?;
    if !request.saturations.is_empty() && request.saturations.len() != inputs.len() {
        return Err("Provide one saturation value per continuum input.".into());
    }
    if !request.clipping_masks.is_empty() && request.clipping_masks.len() != inputs.len() {
        return Err("Provide one optional clipping mask per continuum input.".into());
    }
    let mut sources = Vec::new();
    let mut masks = Vec::new();
    let mut has_masks = false;
    for (index, input) in inputs.iter().enumerate() {
        require_linear(input)?;
        let mut source = ContinuumSource {
            name: format!("plane-{index}"),
            revision: input.revision.clone(),
            transfer: ContinuumTransfer::Linear,
            saturation: request.saturations.get(index).copied().flatten(),
            clipping_revision: None,
        };
        let mask = if let Some(Some(path)) = request.clipping_masks.get(index) {
            has_masks = true;
            let loaded = load(path)?;
            if loaded.frame.image.channels != 1
                || (loaded.frame.image.width, loaded.frame.image.height)
                    != (input.frame.image.width, input.frame.image.height)
                || loaded
                    .frame
                    .image
                    .data
                    .iter()
                    .any(|v| *v != 0.0 && *v != 1.0)
            {
                return Err("Clipping masks must be aligned mono images with exactly 0 (usable) or 1 (clipped).".into());
            }
            source.clipping_revision = Some(loaded.revision);
            loaded
                .frame
                .image
                .data
                .iter()
                .map(|v| u8::from(*v == 1.0))
                .collect()
        } else {
            vec![0_u8; input.frame.image.data.len()]
        };
        masks.push(mask);
        sources.push(source);
    }
    let sources = ContinuumSources {
        width: inputs[0].frame.image.width,
        height: inputs[0].frame.image.height,
        narrowband: sources.remove(0),
        references: sources,
    };
    let reference_images: Vec<_> = inputs[1..].iter().map(|input| &input.frame.image).collect();
    if analyze {
        if sources.narrowband.saturation.is_none() && sources.narrowband.clipping_revision.is_none()
            || sources
                .references
                .iter()
                .any(|source| source.saturation.is_none() && source.clipping_revision.is_none())
        {
            return Ok((
                None,
                json!({"status":"skipped","reason":"unknown-saturation",
                "detail":"Automatic stellar fitting requires a producer saturation ceiling or clipping mask for every source; no ceiling is inferred from image maxima."}),
            ));
        }
        let photometry = ContinuumPhotometryOptions {
            psf_matching: request.psf_matching.then(ContinuumPsfOptions::default),
            ..Default::default()
        };
        let outcome = if has_masks {
            seiza_stacking::measure_and_fit_continuum_masked(
                &inputs[0].frame.image,
                &reference_images,
                &sources,
                &masks.iter().map(Vec::as_slice).collect::<Vec<_>>(),
                &[],
                &ContinuumFitOptions::default(),
                &photometry,
            )
        } else {
            seiza_stacking::measure_and_fit_continuum(
                &inputs[0].frame.image,
                &reference_images,
                &sources,
                &[],
                &ContinuumFitOptions::default(),
                &photometry,
            )
        }
        .map_err(|e| e.to_string())?;
        return Ok((
            None,
            serde_json::to_value(outcome).map_err(|e| e.to_string())?,
        ));
    }
    let plan = match (&request.plan, &request.manual_coefficients) {
        (Some(plan), None) => plan.clone(),
        (None, Some(coefficients)) => ContinuumPlan { schema_version: 1, sources: sources.clone(), coefficients: coefficients.clone(),
            reference_flux_ratios: Vec::new(), origin: ContinuumOrigin::Manual, diagnostics: None, psf_matching: None },
        _ => return Err("Analyze and review a fit, load an exact-source plan, or explicitly choose manual coefficients.".into()),
    };
    let image = seiza_stacking::subtract_continuum(
        &inputs[0].frame.image,
        &reference_images,
        &sources,
        &plan,
    )
    .map_err(|e| e.to_string())?;
    Ok((
        Some(image),
        json!({"status":"success","plan":plan,"signedResidual":true}),
    ))
}

fn stars(request: &Request, inputs: &[Input]) -> Result<(LinearImage, Value), String> {
    grid(inputs, 3)?;
    let prepared = request.operation == "restore-prepared-stars";
    let restore = request.operation != "stretch-stars";
    let expected = if restore && !prepared { 3 } else { 2 };
    if inputs.len() != expected {
        return Err(
            "Choose the required RGB original, starless, nebula or prepared stars images.".into(),
        );
    }
    let layer = if prepared {
        require_display(&inputs[0])?;
        inputs[0].frame.image.clone()
    } else {
        require_linear(&inputs[0])?;
        require_linear(&inputs[1])?;
        seiza_stacking::stretch_rgb_stars(
            &inputs[0].frame.image,
            &inputs[1].frame.image,
            request.star_amount,
        )
        .map_err(|e| e.to_string())?
    };
    let mut preparation = Value::Null;
    let image = if restore {
        let nebula = inputs.last().unwrap();
        let mut image = if request.nebula_display {
            require_display(nebula)?;
            preparation = json!({"transfer":"already-display"});
            nebula.frame.image.clone()
        } else {
            require_linear(nebula)?;
            let (image, report) = stretch(&nebula.frame.image, request.target_median)?;
            preparation = report;
            image
        };
        seiza_stacking::reduce_green_excess(&mut image, request.green_reduction)
            .map_err(|e| e.to_string())?;
        seiza_stacking::screen_rgb_star_layer(&mut image, &layer, request.star_reduction)
            .map_err(|e| e.to_string())?;
        image
    } else {
        layer
    };
    Ok((
        image,
        json!({"amount":(!prepared).then_some(request.star_amount),"reduction":if restore { request.star_reduction } else { 0.0 },
        "preparedLayer":prepared,"nebulaPreparation":preparation,"greenReductionBeforeStars":if restore { request.green_reduction } else { 0.0 }}),
    ))
}

fn run(request: Request) -> Result<Value, String> {
    let analyze = request.operation == "analyze-continuum";
    let protected: Vec<_> = request
        .inputs
        .iter()
        .chain(request.structure.iter())
        .chain(request.clipping_masks.iter().flatten())
        .collect();
    if !analyze {
        let output = request
            .output
            .as_ref()
            .ok_or("Choose a separate FITS or XISF output.")?;
        if !matches!(
            output
                .extension()
                .and_then(|s| s.to_str())
                .map(str::to_ascii_lowercase)
                .as_deref(),
            Some("fits" | "fit" | "fts" | "xisf")
        ) {
            return Err(
                "Use FITS or XISF to preserve floating-point samples and their transfer semantics."
                    .into(),
            );
        }
        if protected
            .iter()
            .any(|path| seiza_stacking::paths_refer_to_same_file(path, output))
        {
            return Err("The output must never replace an input or clipping mask.".into());
        }
    }
    let inputs = request
        .inputs
        .iter()
        .map(|path| load(path))
        .collect::<Result<Vec<_>, _>>()?;
    let auxiliary = request
        .structure
        .iter()
        .chain(request.clipping_masks.iter().flatten())
        .map(|path| Ok((path, hash(path)?)))
        .collect::<Result<Vec<_>, String>>()?;
    if request.operation == "copy-result" {
        if inputs.len() != 1 {
            return Err("Choose one built preview to save.".into());
        }
        if hash(&inputs[0].path)? != inputs[0].revision {
            return Err("The built preview changed before saving.".into());
        }
        // The processed-image writer intentionally copies only observation/WCS
        // metadata. A format-only save must explicitly carry our transfer and
        // provenance cards too, or reopening a display result stretches again.
        let provenance: Vec<_> = inputs[0]
            .frame
            .headers
            .iter()
            .filter(|(key, _)| key.starts_with("SEIZA"))
            .map(|(key, value)| WriteHeaderCard::new(key.clone(), value.clone()))
            .collect();
        seiza_stacking::write_processed_image_fits_f32(
            request.output.as_ref().unwrap(),
            &inputs[0].frame.image,
            &inputs[0].frame.headers,
            &provenance,
        )
        .map_err(|e| e.to_string())?;
        return Ok(
            json!({"status":"success","operation":"copy-result","sourceSha256":inputs[0].revision}),
        );
    }
    let (image, mut report, display) = match request.operation.as_str() {
        "narrowband" => {
            let (image, report) = narrowband(&request, &inputs)?;
            (Some(image), report, true)
        }
        "analyze-continuum" | "subtract-continuum" => {
            let (image, report) = continuum(&request, &inputs, analyze)?;
            (image, report, false)
        }
        "stretch-stars" | "restore-stars" | "restore-prepared-stars" => {
            let (image, report) = stars(&request, &inputs)?;
            (Some(image), report, true)
        }
        _ => return Err("Choose a supported color operation.".into()),
    };
    // Recheck exact bytes after the computation before writing even a staging file.
    for input in &inputs {
        if hash(&input.path)? != input.revision {
            return Err("An input changed during processing. No output was written.".into());
        }
    }
    for (path, revision) in &auxiliary {
        if hash(path)? != *revision {
            return Err(
                "A structure image or clipping mask changed. No output was written.".into(),
            );
        }
    }
    if let Some(image) = image {
        write(
            request.output.as_ref().unwrap(),
            &image,
            &inputs[0],
            display,
            &request.operation,
        )?;
        report["status"] = json!("success");
        report["transfer"] = json!(if display { "display" } else { "linear" });
    }
    report["operation"] = json!(request.operation);
    report["stackingOutputVersion"] = json!(seiza_stacking::OUTPUT_VERSION);
    report["stretchOutputVersion"] = json!(seiza_stretch::OUTPUT_VERSION);
    report["auxiliarySources"] = json!(
        auxiliary
            .iter()
            .map(|(path, revision)| json!({"path":path,"sha256":revision}))
            .collect::<Vec<_>>()
    );
    report["sources"] = json!(
        inputs
            .iter()
            .map(|input| json!({"path":input.path,"sha256":input.revision}))
            .collect::<Vec<_>>()
    );
    Ok(report)
}

/// Run a color workflow on stable prepared files into a separate staging output.
/// Returned reports and skip outcomes are owned strings freed with seiza_win_string_free.
/// # Safety
/// request is a NUL-terminated UTF-8 JSON object; error_out is null or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_color_tools_json(
    request: *const c_char,
    error_out: *mut *mut c_char,
) -> *mut c_char {
    crate::boundary(error_out, || {
        if request.is_null() {
            return Err("A color-tools request is required.".into());
        }
        let request: Request =
            serde_json::from_slice(unsafe { CStr::from_ptr(request) }.to_bytes())
                .map_err(|e| e.to_string())?;
        Ok(CString::new(run(request)?.to_string())
            .expect("JSON escapes NUL")
            .into_raw())
    })
    .unwrap_or(std::ptr::null_mut())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn image(dir: &Path, name: &str, channels: usize, data: Vec<f32>) -> PathBuf {
        let path = dir.join(name);
        let image = LinearImage::new(data.len() / channels, 1, channels, data).unwrap();
        seiza_stacking::write_processed_image_fits_f32(&path, &image, &[], &[]).unwrap();
        path
    }
    fn execute(value: Value) -> Result<Value, String> {
        run(serde_json::from_value(value).unwrap())
    }
    #[test]
    fn manual_continuum_preserves_signed_nan_and_exact_source_plan() {
        let dir = tempfile::tempdir().unwrap();
        let nb = image(dir.path(), "nb.fits", 1, vec![1., 3., f32::NAN, -2.]);
        let reference = image(dir.path(), "reference.fits", 1, vec![2., 1., 1., 2.]);
        let output = dir.path().join("residual.xisf");
        let before = hash(&nb).unwrap();
        let report = execute(
            json!({"operation":"subtract-continuum","inputs":[nb,reference],
            "output":output,"manualCoefficients":[0.5]}),
        )
        .unwrap();
        let result = load(&output).unwrap();
        assert_eq!(result.frame.image.data[0..2], [0., 2.5]);
        assert!(result.frame.image.data[2].is_nan());
        assert_eq!(result.frame.image.data[3], -3.);
        assert!(!display_marker(&result.frame));
        assert_eq!(hash(&nb).unwrap(), before);
        let plan = report["plan"].clone();
        execute(
            json!({"operation":"subtract-continuum","inputs":[nb,reference],
            "output":output,"plan":plan}),
        )
        .unwrap();
        let saved = hash(&output).unwrap();
        image(dir.path(), "reference.fits", 1, vec![2., 2., 1., 2.]);
        assert!(
            execute(
                json!({"operation":"subtract-continuum","inputs":[nb,reference],
            "output":output,"plan":plan})
            )
            .is_err()
        );
        assert_eq!(hash(&output).unwrap(), saved);
    }
    #[test]
    fn unknown_saturation_skips_without_writing_or_manual_fallback() {
        let dir = tempfile::tempdir().unwrap();
        let nb = image(dir.path(), "nb.fits", 1, vec![100.; 100]);
        let reference = image(dir.path(), "reference.fits", 1, vec![100.; 100]);
        let output = dir.path().join("keep.fits");
        std::fs::write(&output, b"keep").unwrap();
        let report = execute(
            json!({"operation":"analyze-continuum","inputs":[nb,reference],"output":output}),
        )
        .unwrap();
        assert_eq!(report["status"], "skipped");
        assert_eq!(std::fs::read(&output).unwrap(), b"keep");
        assert!(
            execute(
                json!({"operation":"subtract-continuum","inputs":[nb,reference],"output":output})
            )
            .is_err()
        );
        assert_eq!(std::fs::read(&output).unwrap(), b"keep");
    }
    #[test]
    fn prepared_stars_are_screened_once_without_a_second_stretch() {
        let dir = tempfile::tempdir().unwrap();
        let stars = image(
            dir.path(),
            "stars.fits",
            3,
            vec![0.2, 0.4, 0.6, f32::NAN, 0., 0.],
        );
        let nebula = image(
            dir.path(),
            "nebula.fits",
            3,
            vec![0.1, 0.2, 0.3, 0.1, 0.2, 0.3],
        );
        let output = dir.path().join("screen.fits");
        let report = execute(
            json!({"operation":"restore-prepared-stars","inputs":[stars,nebula],
            "output":output,"starAmount":8.0}),
        )
        .unwrap();
        let result = load(&output).unwrap();
        for (actual, expected) in result.frame.image.data.iter().zip([0.28, 0.52, 0.72]) {
            assert!((actual - expected).abs() < 1e-6);
        }
        assert!(result.frame.image.data[3..].iter().all(|v| v.is_nan()));
        assert!(display_marker(&result.frame));
        assert!(report["amount"].is_null());
    }
    #[test]
    fn narrowband_preserves_palette_and_rejects_a_wrong_transfer_declaration() {
        let dir = tempfile::tempdir().unwrap();
        let ha = image(dir.path(), "ha.fits", 1, vec![0.2, 0.3]);
        let oiii = image(dir.path(), "oiii.fits", 1, vec![0.4, 0.5]);
        let sii = image(dir.path(), "sii.fits", 1, vec![0.6, 0.7]);
        let output = dir.path().join("sho.xisf");
        execute(
            json!({"operation":"narrowband","inputs":[ha,oiii,sii],"output":output,
            "inputDisplay":true,"balance":false,"palette":"sho"}),
        )
        .unwrap();
        assert_eq!(
            load(&output).unwrap().frame.image.data,
            [0.6, 0.2, 0.4, 0.7, 0.3, 0.5]
        );
        assert!(
            execute(json!({"operation":"stretch-stars","inputs":[output,output],
            "output":dir.path().join("bad.fits")}))
            .unwrap_err()
            .contains("DISPLAY")
        );
    }
    #[test]
    fn rejects_input_and_mask_output_aliases_and_invalid_clipping_masks() {
        let dir = tempfile::tempdir().unwrap();
        let nb = image(dir.path(), "nb.fits", 1, vec![1., 2.]);
        let reference = image(dir.path(), "reference.fits", 1, vec![1., 2.]);
        let mask = image(dir.path(), "mask.fits", 1, vec![0., 0.5]);
        for output in [&nb, &mask] {
            assert!(
                execute(
                    json!({"operation":"subtract-continuum","inputs":[nb,reference],
                "output":output,"clippingMasks":[mask,null],"manualCoefficients":[1.]})
                )
                .unwrap_err()
                .contains("replace")
            );
        }
        assert!(
            execute(
                json!({"operation":"analyze-continuum","inputs":[nb,reference],
            "clippingMasks":[mask,null]})
            )
            .unwrap_err()
            .contains("exactly 0")
        );
    }

    #[test]
    fn stellar_fit_is_reviewable_and_replays_its_validated_coefficient() {
        let dir = tempfile::tempdir().unwrap();
        let side = 720;
        let mut samples = vec![0_f32; side * side];
        for y in 0..side {
            for x in 0..side {
                let dx = (x % 48) as f64 - 24.;
                let dy = (y % 48) as f64 - 24.;
                let peak = 500. + ((x / 48 * 13 + y / 48 * 7) % 20) as f64 * 60.;
                samples[y * side + x] = (100.
                    + peak * (-(dx * dx + dy * dy) / 8.).exp()
                    + ((x * 11 + y * 17) % 19) as f64 * 0.01)
                    as f32;
            }
        }
        let reference = dir.path().join("reference.fits");
        let nb = dir.path().join("nb.fits");
        for (path, scale) in [(&reference, 1.), (&nb, 0.7)] {
            let image =
                LinearImage::new(side, side, 1, samples.iter().map(|v| v * scale).collect())
                    .unwrap();
            seiza_stacking::write_processed_image_fits_f32(path, &image, &[], &[]).unwrap();
        }
        let report = execute(
            json!({"operation":"analyze-continuum","inputs":[nb,reference],
            "saturations":[65535.,65535.]}),
        )
        .unwrap();
        assert_eq!(report["status"], "fitted", "{report}");
        assert!((report["plan"]["coefficients"][0].as_f64().unwrap() - 0.7).abs() < 0.001);
        assert!(report["plan"]["diagnostics"].is_object());
        let matched = execute(
            json!({"operation":"analyze-continuum","inputs":[nb,reference],
            "saturations":[65535.,65535.],"psfMatching":true}),
        )
        .unwrap();
        assert_eq!(matched["status"], "fitted", "{matched}");
        assert!(matched["plan"]["psfMatching"].is_object());
        let output = dir.path().join("subtracted.fits");
        execute(
            json!({"operation":"subtract-continuum","inputs":[nb,reference],
            "saturations":[65535.,65535.],"plan":report["plan"],"output":output}),
        )
        .unwrap();
        assert!(
            load(&output)
                .unwrap()
                .frame
                .image
                .data
                .iter()
                .all(|v| v.abs() < 0.01)
        );
    }

    #[test]
    fn linear_star_extraction_uses_common_scale_and_linear_narrowband_stretches() {
        let dir = tempfile::tempdir().unwrap();
        let original = image(
            dir.path(),
            "original.fits",
            3,
            vec![10., 20., 30., 5., 10., 15.],
        );
        let starless = image(
            dir.path(),
            "starless.fits",
            3,
            vec![8., 15., 20., 5., 10., 15.],
        );
        let output = dir.path().join("stars.fits");
        execute(json!({"operation":"stretch-stars","inputs":[original,starless],"output":output,"starAmount":0.})).unwrap();
        let data = load(&output).unwrap().frame.image.data;
        for (actual, expected) in data.iter().zip([2. / 30., 5. / 30., 10. / 30., 0., 0., 0.]) {
            assert!((actual - expected).abs() < 1e-6);
        }
        let ha = image(
            dir.path(),
            "ha.fits",
            1,
            (0..100).map(|i| 100. + i as f32).collect(),
        );
        let oiii = image(
            dir.path(),
            "oiii.fits",
            1,
            (0..100).map(|i| 60. + i as f32).collect(),
        );
        let report = execute(
            json!({"operation":"narrowband","inputs":[ha,oiii],"output":output,"balance":false}),
        )
        .unwrap();
        assert_eq!(report["preparation"]["colorStrategy"], "linked");
        assert!(
            load(&output)
                .unwrap()
                .frame
                .image
                .data
                .iter()
                .all(|v| v.is_finite() && (0.0..=1.0).contains(v))
        );
    }
}
