# Native ABI

`seiza-cabi.dll` deliberately exposes a small C ABI rather than Rust symbols.
The same API is statically linked by the macOS app and dynamically loaded by
the Windows app.

Rules:

- Opaque image handles own their pixel and metadata buffers.
- A handle remains alive until the host calls its matching free function.
- Owned UTF-8 strings have an explicit Rust free function.
- Every exported operation catches panics and returns a host-readable error.
- High-volume pixels use a contiguous BGRA8 buffer suitable for direct Win2D upload; evolving records use JSON.
- Catalog status is returned as owned JSON; catalog setup runs synchronously on a worker thread and reports borrowed progress JSON through a callback.
- Rust owns manifest resolution, download caching, full SHA-256 verification, and atomic catalog installation.
- ABI additions are backward-compatible. Breaking changes require an ABI version bump.

The Windows interop layer uses source-generated `LibraryImport` declarations,
an unmanaged progress trampoline, and `SafeHandle` wrappers. Raw ownership is
contained in the service boundary and is never exposed to view models or
controls.
