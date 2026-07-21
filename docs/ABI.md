# Native ABI

`seiza-cabi.dll` deliberately exposes a small C ABI rather than Rust symbols.
The same API is statically linked by the macOS app and dynamically loaded by
the Windows app. Its implementation lives in the upstream Seiza workspace;
this repository pins that crate by Git commit and builds it directly rather
than maintaining a Windows fork.

Rules:

- Opaque image handles own their pixel and metadata buffers.
- A handle remains alive until the host calls its matching free function.
- Owned UTF-8 strings have an explicit Rust free function.
- Every exported operation catches panics and returns a host-readable error.
- High-volume pixels use a contiguous BGRA8 buffer suitable for direct Win2D upload; evolving records use JSON.
- FITS processing uses the shared `seiza_rendered_image_open_with_stretch_config` JSON contract so ordered stretch stages, color strategy, background subtraction, and interactive-preview intent stay platform-neutral.
- Catalog status is returned as owned JSON; catalog setup runs synchronously on a worker thread and reports borrowed progress JSON through a callback.
- Rust owns manifest resolution, download caching, full SHA-256 verification, and atomic catalog installation.
- ABI additions are backward-compatible. Breaking changes require an ABI version bump.

The Windows interop layer uses source-generated `LibraryImport` declarations,
an unmanaged progress trampoline, and `SafeHandle` wrappers. Raw ownership is
contained in the service boundary and is never exposed to view models or
controls.

Interactive edits are debounced in the WinUI shell and rendered at a maximum
2,048-pixel dimension. Save submits the same processing stack at full
resolution; no per-pixel processing or stretch math is duplicated in C#.

`scripts/build-rust.ps1` resolves the pinned package with `cargo metadata`,
builds the upstream `seiza-cabi` workspace member, and emits
`seiza-build-info.json` beside the DLL. The app packages that file so About can
show the exact native crate version and 40-character source commit.
