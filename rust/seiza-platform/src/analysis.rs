//! Windows progress/cancellation binding; scientific calculations live in seiza-analysis.
use serde::Deserialize;
use serde_json::json;
use std::ffi::{CStr, CString, c_char, c_void};
use std::path::PathBuf;

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Request {
    inputs: Vec<PathBuf>,
}

/// Callback returns zero to cancel. Its JSON is borrowed only during the call.
/// Result/error strings must be freed with seiza_win_string_free.
/// # Safety
/// request is NUL-terminated UTF-8; callback/context live until return;
/// error_out is null or writable. Callback must not unwind across the ABI.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn seiza_win_analyze_project_json(
    request: *const c_char,
    callback: Option<unsafe extern "C" fn(*const c_char, *mut c_void) -> i32>,
    context: *mut c_void,
    error_out: *mut *mut c_char,
) -> *mut c_char {
    super::boundary(error_out, || {
        if request.is_null() {
            return Err("Missing analysis request".into());
        }
        let request: Request =
            serde_json::from_slice(unsafe { CStr::from_ptr(request) }.to_bytes())
                .map_err(|e| e.to_string())?;
        let mut observer = |event: &seiza_analysis::AnalysisProgress| {
            let Some(callback) = callback else {
                return true;
            };
            let json = CString::new(serde_json::to_string(event).expect("serializable progress"))
                .expect("JSON escapes NULs");
            unsafe { callback(json.as_ptr(), context) != 0 }
        };
        let report = seiza_analysis::analyze_with_progress(
            &request.inputs,
            &Default::default(),
            &mut observer,
        )
        .map_err(|e| e.to_string())?;
        if !observer(&seiza_analysis::AnalysisProgress {
            phase: "report",
            completed: 0,
            total: 0,
            detail: "Writing HTML report",
            path: None,
        }) {
            return Err("Analysis cancelled".into());
        }
        let html = seiza_analysis::render_html(&report).map_err(|e| e.to_string())?;
        let result = json!({
            "html": html,
            "measured": report.frames.iter().filter(|f| f.measurement_status == "measured").count(),
            "total": report.frames.len()
        });
        Ok(CString::new(result.to_string())
            .expect("JSON escapes NULs")
            .into_raw())
    })
    .unwrap_or(std::ptr::null_mut())
}
