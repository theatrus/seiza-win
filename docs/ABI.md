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
- ABI additions are backward-compatible. Breaking changes require an ABI version bump.

The first Windows interop layer will use source-generated `LibraryImport`
declarations and `SafeHandle` wrappers. It must not expose raw ownership to
view models or controls.
