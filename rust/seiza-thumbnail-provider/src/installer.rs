//! Recovery for older MSIs whose post-commit Shell action can fail without VC++.

use std::panic::catch_unwind;

use windows::Win32::System::ApplicationInstallationAndServicing::{
    INSTALLMESSAGE_ERROR, INSTALLSTATE_UNKNOWN, MSIHANDLE, MsiCloseHandle, MsiCreateRecord,
    MsiGetPropertyW, MsiProcessMessage, MsiQueryProductStateW, MsiRecordSetStringW,
};
use windows::core::{PCWSTR, PWSTR, w};

const INSTALL_FAILURE: u32 = 1603;

fn is_product_code(text: &str) -> bool {
    // MSI product codes include braces; windows-core parses the inner GUID.
    text.strip_prefix('{')
        .and_then(|s| s.strip_suffix('}'))
        .is_some_and(|guid| windows::core::GUID::try_from(guid).is_ok())
}

fn previous_products_removed(install: MSIHANDLE) -> bool {
    // Bound the property size and fail closed on MSI errors or malformed input.
    let mut products = [0u16; 4096];
    let mut length = products.len() as u32;
    let status = unsafe {
        MsiGetPropertyW(
            install,
            w!("WIX_UPGRADE_DETECTED"),
            Some(PWSTR(products.as_mut_ptr())),
            Some(&mut length),
        )
    };
    if status != 0 || length == 0 || length as usize >= products.len() {
        return false;
    }
    products[..length as usize]
        .split(|&c| c == b';' as u16)
        .all(|code| {
            let Ok(text) = String::from_utf16(code) else {
                return false;
            };
            if !is_product_code(&text) {
                return false;
            }
            let mut terminated = code.to_vec();
            terminated.push(0);
            unsafe { MsiQueryProductStateW(PCWSTR(terminated.as_ptr())) == INSTALLSTATE_UNKNOWN }
        })
}

/// IgnoreRemoveFailure is safe only when the old products really disappeared.
/// Old Seiza MSIs can report failure *after* committing their uninstall, when
/// their embedded /MD Shell action cannot load. Never mask a rollback or a
/// still-registered product: stop before installing any new files in that case.
#[unsafe(no_mangle)]
pub extern "system" fn VerifyPreviousProductsRemoved(install_handle: u32) -> u32 {
    let install = MSIHANDLE(install_handle);
    if catch_unwind(|| previous_products_removed(install)).unwrap_or(false) {
        return 0;
    }
    unsafe {
        let record = MsiCreateRecord(0);
        if record.0 != 0 {
            MsiRecordSetStringW(
                record,
                0,
                w!(
                    "Seiza could not remove the previous installation. Repair or uninstall it, then run this installer again."
                ),
            );
            MsiProcessMessage(install, INSTALLMESSAGE_ERROR, record);
            MsiCloseHandle(record);
        }
    }
    INSTALL_FAILURE
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn invalid_installer_handle_fails_closed() {
        assert_eq!(VerifyPreviousProductsRemoved(0), INSTALL_FAILURE);
    }

    #[test]
    fn accepts_msi_braced_product_codes_and_rejects_malformed_codes() {
        assert!(is_product_code("{AD1A4DA0-DAF4-42B5-B989-332A9AB182DC}"));
        for code in ["", "AD1A4DA0-DAF4-42B5-B989-332A9AB182DC", "{invalid}"] {
            assert!(!is_product_code(code));
        }
    }
}
