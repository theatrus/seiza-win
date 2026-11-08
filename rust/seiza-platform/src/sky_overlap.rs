//! Windows workflow adapter: use the published core's TAN/SIP transforms to
//! compare solved image footprints. This does not register or resample pixels.
use crate::boundary;
use seiza::Wcs;
use seiza_fits::HeaderValue;
use serde::Deserialize;
use serde_json::{Value, json};
use std::ffi::{CStr, CString, c_char};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Frame {
    width: u32,
    height: u32,
    wcs_headers: serde_json::Map<String, Value>,
}

impl Frame {
    fn wcs(&self) -> Result<Wcs, String> {
        if self.width == 0 || self.height == 0 {
            return Err("A solved footprint needs nonzero image dimensions.".into());
        }
        let headers: Vec<_> = self
            .wcs_headers
            .iter()
            .map(|(key, value)| {
                let value = match value {
                    Value::String(text) => HeaderValue::String(text.clone()),
                    Value::Number(number) => {
                        HeaderValue::Float(number.as_f64().unwrap_or(f64::NAN))
                    }
                    _ => HeaderValue::Raw(String::new()),
                };
                (key.clone(), value)
            })
            .collect();
        let wcs = seiza_image_io::solve_prior_from_headers(&headers)
            .wcs
            .ok_or("The solved footprint has no usable WCS.")?;
        if !wcs.scale_arcsec_per_px().is_finite() || wcs.scale_arcsec_per_px() <= 0.0 {
            return Err("The solved footprint has an invalid scale.".into());
        }
        Ok(wcs)
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    frames: Vec<Frame>,
    start_row: usize,
    row_count: usize,
}

type Point = (f64, f64);

// Sample curved SIP edges, then clip in the destination pixel grid. TAN/SIP
// transforms (including RA wrap, poles and parity) remain entirely in Seiza.
fn covered_fraction(source: &Frame, sw: &Wcs, target: &Frame, tw: &Wcs) -> f64 {
    let corners = [
        (-0.5, -0.5),
        (source.width as f64 - 0.5, -0.5),
        (source.width as f64 - 0.5, source.height as f64 - 0.5),
        (-0.5, source.height as f64 - 0.5),
    ];
    let mut polygon = Vec::with_capacity(64);
    for edge in 0..4 {
        let a = corners[edge];
        let b = corners[(edge + 1) % 4];
        for step in 0..16 {
            let t = step as f64 / 16.0;
            let (ra, dec) = sw.pixel_to_world(a.0 + t * (b.0 - a.0), a.1 + t * (b.1 - a.1));
            let Some((x, y)) = tw.world_to_pixel(ra, dec) else {
                return 0.0;
            };
            if !x.is_finite() || !y.is_finite() {
                return 0.0;
            }
            polygon.push((x + 0.5, y + 0.5));
        }
    }
    for (axis, bound, greater) in [
        (0, 0.0, true),
        (0, target.width as f64, false),
        (1, 0.0, true),
        (1, target.height as f64, false),
    ] {
        polygon = clip(&polygon, axis, bound, greater);
    }
    (area(&polygon) / (target.width as f64 * target.height as f64)).clamp(0.0, 1.0)
}

fn clip(input: &[Point], axis: usize, bound: f64, greater: bool) -> Vec<Point> {
    let coordinate = |p: Point| if axis == 0 { p.0 } else { p.1 };
    let inside = |p| {
        if greater {
            coordinate(p) >= bound
        } else {
            coordinate(p) <= bound
        }
    };
    let mut output = Vec::new();
    let Some(&last) = input.last() else {
        return output;
    };
    let mut a = last;
    for &b in input {
        if inside(a) != inside(b) {
            let t = (bound - coordinate(a)) / (coordinate(b) - coordinate(a));
            output.push((a.0 + t * (b.0 - a.0), a.1 + t * (b.1 - a.1)));
        }
        if inside(b) {
            output.push(b);
        }
        a = b;
    }
    output
}

fn area(polygon: &[Point]) -> f64 {
    if polygon.len() < 3 {
        return 0.0;
    }
    (0..polygon.len())
        .map(|i| {
            let a = polygon[i];
            let b = polygon[(i + 1) % polygon.len()];
            a.0 * b.1 - b.0 * a.1
        })
        .sum::<f64>()
        .abs()
        * 0.5
}

fn overlaps(request: Request) -> Result<Value, String> {
    let n = request.frames.len();
    if request.row_count > 8 || request.start_row > n || request.row_count > n - request.start_row {
        return Err(
            "Footprint batches must contain at most eight rows within the selected frames.".into(),
        );
    }
    let wcs: Vec<_> = request
        .frames
        .iter()
        .map(Frame::wcs)
        .collect::<Result<_, _>>()?;
    let rows: Vec<Vec<f64>> = (request.start_row..request.start_row + request.row_count)
        .map(|i| {
            (0..n)
                .map(|j| {
                    if i == j {
                        return 1.0;
                    }
                    // Both images must overlap by the threshold: a tiny image inside a
                    // large one is not mistaken for a repeated exposure of that panel.
                    covered_fraction(&request.frames[i], &wcs[i], &request.frames[j], &wcs[j]).min(
                        covered_fraction(&request.frames[j], &wcs[j], &request.frames[i], &wcs[i]),
                    )
                })
                .collect()
        })
        .collect();
    Ok(json!(rows))
}

/// Return a bounded batch of symmetric sky-footprint overlap fractions.
/// Free response and errors with seiza_win_string_free.
/// # Safety
/// request is NUL-terminated UTF-8; error_out is null or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_sky_overlaps_json(
    request: *const c_char,
    error_out: *mut *mut c_char,
) -> *mut c_char {
    boundary(error_out, || {
        if request.is_null() {
            return Err("A footprint request is required.".into());
        }
        let request = serde_json::from_slice(unsafe { CStr::from_ptr(request) }.to_bytes())
            .map_err(|e| e.to_string())?;
        Ok(CString::new(overlaps(request)?.to_string())
            .expect("JSON escapes NUL")
            .into_raw())
    })
    .unwrap_or(std::ptr::null_mut())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn frame() -> Frame {
        Frame {
            width: 1000,
            height: 800,
            wcs_headers: Default::default(),
        }
    }
    fn wcs(ra: f64, dec: f64, rotation: f64) -> Wcs {
        Wcs::from_center_scale_rotation((ra, dec), (499.5, 399.5), 3.6, rotation, false)
    }
    #[test]
    fn identical_rotated_wrapped_and_polar_footprints() {
        let f = frame();
        for (ra, dec) in [(359.99, 0.0), (0.01, 89.8), (100.0, -89.8)] {
            let a = wcs(ra, dec, 0.0);
            let b = wcs(ra, dec, 180.0);
            assert!((covered_fraction(&f, &a, &f, &a) - 1.0).abs() < 1e-6);
            assert!((covered_fraction(&f, &a, &f, &b) - 1.0).abs() < 1e-6);
        }
        let overlap = covered_fraction(&f, &wcs(359.8, 0.0, 0.0), &f, &wcs(0.2, 0.0, 0.0));
        assert!((overlap - 0.6).abs() < 0.001, "{overlap}");
    }
    #[test]
    fn disjoint_touching_and_oblique_images() {
        let f = frame();
        let a = wcs(0.0, 0.0, 0.0);
        assert_eq!(covered_fraction(&f, &a, &f, &wcs(180.0, 0.0, 0.0)), 0.0);
        assert!(covered_fraction(&f, &a, &f, &wcs(1.1, 0.0, 0.0)) < 1e-6);
        let fraction = covered_fraction(&f, &a, &f, &wcs(0.0, 0.0, 45.0));
        assert!(fraction > 0.7 && fraction < 0.9, "{fraction}");
    }

    #[test]
    fn selections_above_512_keep_every_footprint() {
        let headers = json!({
            "CTYPE1": "RA---TAN", "CTYPE2": "DEC--TAN",
            "CRVAL1": 0.0, "CRVAL2": 0.0, "CRPIX1": 500.5, "CRPIX2": 400.5,
            "CD1_1": -0.001, "CD1_2": 0.0, "CD2_1": 0.0, "CD2_2": -0.001
        });
        let frames = (0..513)
            .map(|_| Frame {
                wcs_headers: serde_json::from_value(headers.clone()).unwrap(),
                ..frame()
            })
            .collect();
        let result = overlaps(Request {
            frames,
            start_row: 512,
            row_count: 1,
        })
        .unwrap();
        let rows = result.as_array().unwrap();
        assert_eq!(rows.len(), 1);
        let row = rows[0].as_array().unwrap();
        assert_eq!(row.len(), 513);
        assert!(row.iter().all(|value| value.as_f64().unwrap() > 0.99999));
    }

    #[test]
    fn invalid_batch_sizes_and_ranges_are_still_rejected() {
        for (start_row, row_count) in [(0, 9), (4, 1), (3, 2), (usize::MAX, 1)] {
            let request = Request {
                frames: (0..4).map(|_| frame()).collect(),
                start_row,
                row_count,
            };
            assert!(
                overlaps(request)
                    .unwrap_err()
                    .contains("at most eight rows")
            );
        }
    }
}
