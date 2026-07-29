use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::sync::Mutex;

use windows::Win32::Foundation::{E_FAIL, E_NOTIMPL, E_POINTER, E_UNEXPECTED, HWND, RECT, S_FALSE};
use windows::Win32::Graphics::Gdi::{DeleteObject, HBITMAP, HGDIOBJ};
use windows::Win32::System::Com::IStream;
use windows::Win32::System::Ole::{
    IObjectWithSite, IObjectWithSite_Impl, IOleWindow, IOleWindow_Impl,
};
use windows::Win32::System::SystemServices::{SS_BITMAP, SS_CENTERIMAGE};
use windows::Win32::UI::Input::KeyboardAndMouse::{GetFocus, SetFocus};
use windows::Win32::UI::Shell::PropertiesSystem::{
    IInitializeWithStream, IInitializeWithStream_Impl,
};
use windows::Win32::UI::Shell::{IPreviewHandler, IPreviewHandler_Impl, IPreviewHandlerFrame};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DestroyWindow, IMAGE_BITMAP, MSG, MoveWindow, STM_SETIMAGE, SendMessageW,
    WINDOW_EX_STYLE, WINDOW_STYLE, WS_CHILD, WS_VISIBLE,
};
use windows::core::{BOOL, Error, GUID, IUnknown, Interface, PCWSTR, Ref, Result, implement, w};

use crate::com_server::{DllReference, create_bitmap, read_stream};
use crate::renderer::render_preview;

/// CLSID_SeizaPreviewProvider. Keep this in sync with Product.wxs.
pub const PREVIEW_PROVIDER_CLSID: GUID = GUID::from_u128(0x47b9c88e_38f5_4de8_9a33_25e3989a7c51);

const MAX_PREVIEW_DIMENSION: u32 = 4096;

#[derive(Default)]
struct PreviewState {
    stream: Option<IStream>,
    site: Option<IUnknown>,
    parent: HWND,
    bounds: RECT,
    child: HWND,
    bitmap: HBITMAP,
}

impl PreviewState {
    fn destroy_preview(&mut self) {
        if !self.child.is_invalid() {
            let _ = unsafe { DestroyWindow(self.child) };
            self.child = HWND::default();
        }
        if !self.bitmap.is_invalid() {
            let _ = unsafe { DeleteObject(HGDIOBJ(self.bitmap.0)) };
            self.bitmap = HBITMAP::default();
        }
    }
}

#[implement(IPreviewHandler, IInitializeWithStream, IObjectWithSite, IOleWindow)]
pub struct PreviewProvider {
    state: Mutex<PreviewState>,
    _dll_reference: DllReference,
}

impl PreviewProvider {
    pub fn new() -> Self {
        Self {
            state: Mutex::new(PreviewState::default()),
            _dll_reference: DllReference::new(),
        }
    }

    fn render_preview_inner(&self) -> Result<()> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| Error::new(E_FAIL, "preview state lock was poisoned"))?;
        let stream = state
            .stream
            .clone()
            .ok_or_else(|| Error::from_hresult(E_UNEXPECTED))?;
        if state.parent.is_invalid() {
            return Err(Error::new(E_FAIL, "preview host window is not set"));
        }

        let width = (state.bounds.right - state.bounds.left).max(1);
        let height = (state.bounds.bottom - state.bounds.top).max(1);
        let target_width = u32::try_from(width)
            .unwrap_or(1)
            .clamp(1, MAX_PREVIEW_DIMENSION);
        let target_height = u32::try_from(height)
            .unwrap_or(1)
            .clamp(1, MAX_PREVIEW_DIMENSION);
        let bytes = read_stream(&stream)?;
        let rendered = render_preview(&bytes, target_width, target_height).map_err(|message| {
            Error::new(E_FAIL, format!("could not render preview: {message}"))
        })?;
        let bitmap = create_bitmap(&rendered)?;

        state.destroy_preview();
        let styles = WS_CHILD | WS_VISIBLE | WINDOW_STYLE(SS_BITMAP.0 | SS_CENTERIMAGE.0);
        let child = unsafe {
            CreateWindowExW(
                WINDOW_EX_STYLE::default(),
                w!("STATIC"),
                PCWSTR::null(),
                styles,
                state.bounds.left,
                state.bounds.top,
                width,
                height,
                Some(state.parent),
                None,
                None,
                None,
            )
        };
        let child = match child {
            Ok(child) => child,
            Err(error) => {
                let _ = unsafe { DeleteObject(HGDIOBJ(bitmap.0)) };
                return Err(error);
            }
        };
        unsafe {
            SendMessageW(
                child,
                STM_SETIMAGE,
                Some(windows::Win32::Foundation::WPARAM(IMAGE_BITMAP.0 as usize)),
                Some(windows::Win32::Foundation::LPARAM(bitmap.0 as isize)),
            );
        }
        state.child = child;
        state.bitmap = bitmap;
        Ok(())
    }
}

impl Drop for PreviewProvider {
    fn drop(&mut self) {
        if let Ok(state) = self.state.get_mut() {
            state.destroy_preview();
        }
    }
}

impl IInitializeWithStream_Impl for PreviewProvider_Impl {
    fn Initialize(&self, stream: Ref<IStream>, _mode: u32) -> Result<()> {
        let stream = stream
            .cloned()
            .ok_or_else(|| Error::from_hresult(E_POINTER))?;
        let mut state = self
            .state
            .lock()
            .map_err(|_| Error::new(E_FAIL, "preview state lock was poisoned"))?;
        if state.stream.is_some() {
            return Err(Error::from_hresult(E_UNEXPECTED));
        }
        state.stream = Some(stream);
        Ok(())
    }
}

impl IPreviewHandler_Impl for PreviewProvider_Impl {
    fn SetWindow(&self, hwnd: HWND, bounds: *const RECT) -> Result<()> {
        if bounds.is_null() || hwnd.is_invalid() {
            return Err(Error::from_hresult(E_POINTER));
        }
        let child = {
            let mut state = self
                .state
                .lock()
                .map_err(|_| Error::new(E_FAIL, "preview state lock was poisoned"))?;
            state.parent = hwnd;
            state.bounds = unsafe { *bounds };
            state.child
        };
        if !child.is_invalid() {
            let width = (unsafe { (*bounds).right - (*bounds).left }).max(1);
            let height = (unsafe { (*bounds).bottom - (*bounds).top }).max(1);
            unsafe {
                windows::Win32::UI::WindowsAndMessaging::SetParent(child, Some(hwnd))?;
                MoveWindow(child, (*bounds).left, (*bounds).top, width, height, true)?;
            }
        }
        Ok(())
    }

    fn SetRect(&self, bounds: *const RECT) -> Result<()> {
        if bounds.is_null() {
            return Err(Error::from_hresult(E_POINTER));
        }
        let child = {
            let mut state = self
                .state
                .lock()
                .map_err(|_| Error::new(E_FAIL, "preview state lock was poisoned"))?;
            state.bounds = unsafe { *bounds };
            state.child
        };
        if !child.is_invalid() {
            let width = (unsafe { (*bounds).right - (*bounds).left }).max(1);
            let height = (unsafe { (*bounds).bottom - (*bounds).top }).max(1);
            unsafe {
                MoveWindow(child, (*bounds).left, (*bounds).top, width, height, true)?;
            }
        }
        Ok(())
    }

    fn DoPreview(&self) -> Result<()> {
        catch_unwind(AssertUnwindSafe(|| self.render_preview_inner()))
            .map_err(|_| Error::new(E_FAIL, "preview renderer panicked"))??;
        Ok(())
    }

    fn Unload(&self) -> Result<()> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| Error::new(E_FAIL, "preview state lock was poisoned"))?;
        state.destroy_preview();
        state.stream = None;
        Ok(())
    }

    fn SetFocus(&self) -> Result<()> {
        let state = self
            .state
            .lock()
            .map_err(|_| Error::new(E_FAIL, "preview state lock was poisoned"))?;
        let window = if !state.child.is_invalid() {
            state.child
        } else {
            state.parent
        };
        if window.is_invalid() {
            return Err(Error::new(E_FAIL, "preview window is not available"));
        }
        unsafe { SetFocus(Some(window))? };
        Ok(())
    }

    fn QueryFocus(&self) -> Result<HWND> {
        let window = unsafe { GetFocus() };
        if window.is_invalid() {
            Err(Error::new(E_FAIL, "no preview window has focus"))
        } else {
            Ok(window)
        }
    }

    fn TranslateAccelerator(&self, message: *const MSG) -> Result<()> {
        if message.is_null() {
            return Err(Error::from_hresult(E_POINTER));
        }
        let site = self
            .state
            .lock()
            .map_err(|_| Error::new(E_FAIL, "preview state lock was poisoned"))?
            .site
            .clone();
        let Some(site) = site else {
            return Err(Error::from_hresult(S_FALSE));
        };
        let Ok(frame) = site.cast::<IPreviewHandlerFrame>() else {
            return Err(Error::from_hresult(S_FALSE));
        };
        unsafe { frame.TranslateAccelerator(message) }
    }
}

impl IObjectWithSite_Impl for PreviewProvider_Impl {
    fn SetSite(&self, site: Ref<IUnknown>) -> Result<()> {
        let mut state = self
            .state
            .lock()
            .map_err(|_| Error::new(E_FAIL, "preview state lock was poisoned"))?;
        state.site = site.cloned();
        Ok(())
    }

    fn GetSite(&self, interface_id: *const GUID, object: *mut *mut c_void) -> Result<()> {
        if interface_id.is_null() || object.is_null() {
            return Err(Error::from_hresult(E_POINTER));
        }
        unsafe { object.write(ptr::null_mut()) };
        let state = self
            .state
            .lock()
            .map_err(|_| Error::new(E_FAIL, "preview state lock was poisoned"))?;
        let site = state
            .site
            .as_ref()
            .ok_or_else(|| Error::new(E_FAIL, "preview site is not set"))?;
        unsafe { site.query(interface_id, object).ok() }
    }
}

impl IOleWindow_Impl for PreviewProvider_Impl {
    fn GetWindow(&self) -> Result<HWND> {
        let state = self
            .state
            .lock()
            .map_err(|_| Error::new(E_FAIL, "preview state lock was poisoned"))?;
        if state.child.is_invalid() {
            Err(Error::new(E_FAIL, "preview window is not available"))
        } else {
            Ok(state.child)
        }
    }

    fn ContextSensitiveHelp(&self, _enter_mode: BOOL) -> Result<()> {
        Err(Error::from_hresult(E_NOTIMPL))
    }
}
