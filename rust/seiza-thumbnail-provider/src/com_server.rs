use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU32, Ordering};

use windows::Win32::Foundation::{
    CLASS_E_CLASSNOTAVAILABLE, CLASS_E_NOAGGREGATION, E_FAIL, E_POINTER, E_UNEXPECTED, S_FALSE,
    S_OK,
};
use windows::Win32::Graphics::Gdi::{
    BI_RGB, BITMAPINFO, BITMAPINFOHEADER, CreateDIBSection, DIB_RGB_COLORS, HBITMAP,
};
use windows::Win32::System::Com::{
    IClassFactory, IClassFactory_Impl, IStream, STATFLAG_NONAME, STATSTG, STREAM_SEEK_SET,
};
use windows::Win32::UI::Shell::PropertiesSystem::{
    IInitializeWithStream, IInitializeWithStream_Impl,
};
use windows::Win32::UI::Shell::{
    IThumbnailProvider, IThumbnailProvider_Impl, SHCNE_ASSOCCHANGED, SHCNF_FLUSH, SHCNF_IDLIST,
    SHChangeNotify, WTS_ALPHATYPE, WTSAT_ARGB,
};
use windows::core::{BOOL, Error, GUID, HRESULT, IUnknown, Interface, Ref, Result, implement};

use crate::{RenderedThumbnail, render_thumbnail};

/// CLSID_SeizaThumbnailProvider. Keep this in sync with Product.wxs.
pub const THUMBNAIL_PROVIDER_CLSID: GUID = GUID::from_u128(0xe8d56c6c_4e30_4c89_889a_d022180b710a);

const MAX_INPUT_BYTES: u64 = 1024 * 1024 * 1024;
const MAX_THUMBNAIL_DIMENSION: u32 = 4096;
const READ_CHUNK_BYTES: usize = 1024 * 1024;

static DLL_REFERENCES: AtomicU32 = AtomicU32::new(0);

struct DllReference;

impl DllReference {
    fn new() -> Self {
        DLL_REFERENCES.fetch_add(1, Ordering::Relaxed);
        Self
    }
}

impl Drop for DllReference {
    fn drop(&mut self) {
        DLL_REFERENCES.fetch_sub(1, Ordering::Release);
    }
}

#[implement(IThumbnailProvider, IInitializeWithStream)]
struct ThumbnailProvider {
    stream: Mutex<Option<IStream>>,
    _dll_reference: DllReference,
}

impl ThumbnailProvider {
    fn new() -> Self {
        Self {
            stream: Mutex::new(None),
            _dll_reference: DllReference::new(),
        }
    }

    fn get_thumbnail_inner(&self, size: u32) -> Result<HBITMAP> {
        let stream = self
            .stream
            .lock()
            .map_err(|_| Error::new(E_FAIL, "thumbnail stream lock was poisoned"))?
            .clone()
            .ok_or_else(|| Error::from_hresult(E_UNEXPECTED))?;
        let bytes = read_stream(&stream)?;
        let thumbnail = render_thumbnail(&bytes, size.min(MAX_THUMBNAIL_DIMENSION))
            .map_err(|message| Error::new(E_FAIL, format!("could not render image: {message}")))?;
        create_bitmap(&thumbnail)
    }
}

impl IInitializeWithStream_Impl for ThumbnailProvider_Impl {
    fn Initialize(&self, stream: Ref<IStream>, _mode: u32) -> Result<()> {
        let stream = stream
            .cloned()
            .ok_or_else(|| Error::from_hresult(E_POINTER))?;
        let mut slot = self
            .stream
            .lock()
            .map_err(|_| Error::new(E_FAIL, "thumbnail stream lock was poisoned"))?;
        if slot.is_some() {
            return Err(Error::from_hresult(E_UNEXPECTED));
        }
        *slot = Some(stream);
        Ok(())
    }
}

impl IThumbnailProvider_Impl for ThumbnailProvider_Impl {
    fn GetThumbnail(
        &self,
        size: u32,
        bitmap: *mut HBITMAP,
        alpha_type: *mut WTS_ALPHATYPE,
    ) -> Result<()> {
        if bitmap.is_null() || alpha_type.is_null() {
            return Err(Error::from_hresult(E_POINTER));
        }
        unsafe {
            bitmap.write(HBITMAP::default());
            alpha_type.write(WTS_ALPHATYPE::default());
        }

        // No panic may cross a COM ABI boundary, including one from a malformed
        // third-party decoder input.
        let rendered = catch_unwind(AssertUnwindSafe(|| self.get_thumbnail_inner(size)))
            .map_err(|_| Error::new(E_FAIL, "thumbnail renderer panicked"))??;
        unsafe {
            bitmap.write(rendered);
            alpha_type.write(WTSAT_ARGB);
        }
        Ok(())
    }
}

#[implement(IClassFactory)]
struct ThumbnailClassFactory {
    _dll_reference: DllReference,
}

impl ThumbnailClassFactory {
    fn new() -> Self {
        Self {
            _dll_reference: DllReference::new(),
        }
    }
}

impl IClassFactory_Impl for ThumbnailClassFactory_Impl {
    fn CreateInstance(
        &self,
        outer: Ref<IUnknown>,
        interface_id: *const GUID,
        object: *mut *mut c_void,
    ) -> Result<()> {
        if interface_id.is_null() || object.is_null() {
            return Err(Error::from_hresult(E_POINTER));
        }
        unsafe { object.write(ptr::null_mut()) };
        if !outer.is_null() {
            return Err(Error::from_hresult(CLASS_E_NOAGGREGATION));
        }

        let provider: IUnknown = ThumbnailProvider::new().into();
        unsafe { provider.query(interface_id, object).ok() }
    }

    fn LockServer(&self, lock: BOOL) -> Result<()> {
        if lock.as_bool() {
            DLL_REFERENCES.fetch_add(1, Ordering::Relaxed);
        } else {
            DLL_REFERENCES.fetch_sub(1, Ordering::Release);
        }
        Ok(())
    }
}

fn read_stream(stream: &IStream) -> Result<Vec<u8>> {
    let mut stat = STATSTG::default();
    unsafe {
        stream.Stat(&mut stat, STATFLAG_NONAME)?;
        stream.Seek(0, STREAM_SEEK_SET, None)?;
    }
    if stat.cbSize > MAX_INPUT_BYTES {
        return Err(Error::new(
            E_FAIL,
            "image is larger than the 1 GiB thumbnail limit",
        ));
    }

    let capacity = usize::try_from(stat.cbSize).unwrap_or(READ_CHUNK_BYTES);
    let mut bytes = Vec::with_capacity(capacity.min(READ_CHUNK_BYTES * 16));
    while (bytes.len() as u64) < stat.cbSize {
        let remaining = stat.cbSize - bytes.len() as u64;
        let request = remaining.min(READ_CHUNK_BYTES as u64) as usize;
        let start = bytes.len();
        bytes.resize(start + request, 0);
        let mut read = 0u32;
        unsafe {
            stream
                .Read(
                    bytes[start..].as_mut_ptr().cast(),
                    request as u32,
                    Some(&mut read),
                )
                .ok()?;
        }
        bytes.truncate(start + read as usize);
        if read == 0 {
            break;
        }
    }
    Ok(bytes)
}

fn create_bitmap(thumbnail: &RenderedThumbnail) -> Result<HBITMAP> {
    let expected_length = thumbnail.width as usize * thumbnail.height as usize * 4;
    if thumbnail.bgra.len() != expected_length {
        return Err(Error::new(E_FAIL, "invalid thumbnail pixel buffer"));
    }

    let info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: thumbnail.width as i32,
            // A negative height creates a top-down DIB, matching our buffer.
            biHeight: -(thumbnail.height as i32),
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            biSizeImage: expected_length as u32,
            ..Default::default()
        },
        ..Default::default()
    };
    let mut bits = ptr::null_mut();
    let bitmap = unsafe { CreateDIBSection(None, &info, DIB_RGB_COLORS, &mut bits, None, 0)? };
    if bits.is_null() {
        return Err(Error::new(
            E_FAIL,
            "CreateDIBSection returned no pixel buffer",
        ));
    }
    unsafe {
        ptr::copy_nonoverlapping(thumbnail.bgra.as_ptr(), bits.cast(), expected_length);
    }
    Ok(bitmap)
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn DllGetClassObject(
    class_id: *const GUID,
    interface_id: *const GUID,
    object: *mut *mut c_void,
) -> HRESULT {
    if class_id.is_null() || interface_id.is_null() || object.is_null() {
        return E_POINTER;
    }
    unsafe { object.write(ptr::null_mut()) };
    if unsafe { *class_id } != THUMBNAIL_PROVIDER_CLSID {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    let factory: IClassFactory = ThumbnailClassFactory::new().into();
    unsafe { factory.query(interface_id, object) }
}

#[unsafe(no_mangle)]
pub extern "system" fn DllCanUnloadNow() -> HRESULT {
    if DLL_REFERENCES.load(Ordering::Acquire) == 0 {
        S_OK
    } else {
        S_FALSE
    }
}

/// WiX custom-action entry point used after install, repair, and uninstall.
///
/// Windows requires installers that change Shell handlers to invalidate the
/// association and thumbnail caches. The MSI embeds this native DLL in its
/// Binary table, so the notification remains callable after the installed copy
/// has been removed during uninstall.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn NotifyShellAssociations(_install_handle: u32) -> u32 {
    unsafe {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, None, None);
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows::Win32::Graphics::Gdi::{DeleteObject, HGDIOBJ};
    use windows::Win32::UI::Shell::SHCreateMemStream;

    #[test]
    fn exported_class_factory_returns_a_working_thumbnail_provider() {
        assert_eq!(DllCanUnloadNow(), S_OK);
        let mut factory_pointer = ptr::null_mut();
        unsafe {
            DllGetClassObject(
                &THUMBNAIL_PROVIDER_CLSID,
                &IClassFactory::IID,
                &mut factory_pointer,
            )
            .ok()
        }
        .expect("activate class factory");
        let factory = unsafe { IClassFactory::from_raw(factory_pointer) };
        let initialize: IInitializeWithStream =
            unsafe { factory.CreateInstance(None::<&IUnknown>) }.expect("create provider");

        let bytes = crate::test_fits(8, 4);
        let stream = unsafe { SHCreateMemStream(Some(&bytes)) }.expect("memory stream");
        unsafe { initialize.Initialize(&stream, 0) }.expect("initialize provider");
        let provider: IThumbnailProvider = initialize.cast().expect("thumbnail interface");

        let mut bitmap = HBITMAP::default();
        let mut alpha_type = WTS_ALPHATYPE::default();
        unsafe { provider.GetThumbnail(4, &mut bitmap, &mut alpha_type) }
            .expect("render through COM");
        assert!(!bitmap.is_invalid());
        assert_eq!(alpha_type, WTSAT_ARGB);
        assert!(unsafe { DeleteObject(HGDIOBJ(bitmap.0)) }.as_bool());

        drop(provider);
        drop(initialize);
        drop(factory);
        assert_eq!(DllCanUnloadNow(), S_OK);
    }
}
