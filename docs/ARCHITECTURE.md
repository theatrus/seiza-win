# Architecture

## Product boundary

Seiza for Windows uses the same native-shell/shared-Rust split as Seiza for
macOS. Windows-specific behavior remains in the Windows shell; astronomy and
pixel-domain behavior remains in Rust.

```text
Seiza.App (WinUI 3 / C#)
    |-- Windows lifecycle, file activation, multi-window sessions
    |-- native controls, accessibility, settings, drag and drop
    |-- Win2D image viewport and overlay presentation
    `-- generated P/Invoke bindings with SafeHandle ownership
                         |
                         `-- C ABI -- seiza-cabi.dll (Rust)
                                           |-- built from the pinned upstream seiza-cabi crate
                                           |-- seiza-fits
                                           |-- image
                                           `-- seiza
```

A later, separately hosted Rust COM DLL will provide Explorer FITS thumbnails
and Preview Pane integration. It must stay independent of WinUI, .NET, catalog
loading, and plate solving because Explorer loads it out of process.

## Locked decisions

1. The supported first release is Windows 11 x64. ARM64 follows after parity.
2. WinUI 3 owns application chrome and standard interactions.
3. Win2D owns interactive image and vector-overlay presentation.
4. Rust owns decoding, stretching, statistics, WCS, solving, and catalog data.
5. No Rust layout, allocator-owned string, or panic crosses the C ABI.
6. Pixel buffers cross through opaque handles; versioned JSON carries metadata and solve records.
7. The process hosts multiple document windows and redirects new file activations into the existing process.
8. Distribution is an all-users, self-contained WiX 4 MSI with Windows Default
   Apps registration for FITS files. The MSI carries .NET and Windows App SDK
   runtimes; production releases must be code-signed.
9. The Windows app builds the unified upstream `seiza-cabi` crate directly from one reviewed Seiza Git commit; no C ABI implementation is forked in this repository.
10. The native build emits its Cargo-resolved Seiza version and commit as packaged build metadata, and the About dialog reports both values.

## Performance rules

- Never perform per-pixel work in C#.
- Upload a rendered image once; pan, zoom, and overlay visibility changes must not rerender pixels.
- Prioritize the visible image over adjacent thumbnails and cache maintenance.
- Bound background concurrency and memory use.
- Add a tiled rendering API only after measurements show full-image upload is a bottleneck.
- Keep cached previews visible while full-resolution work is in flight.
- Render interactive processing drafts through the shared JSON C ABI at a bounded 2,048-pixel dimension, cancel stale UI results, and retain the committed full-resolution bitmap until Save succeeds.
- Keep the shared pixel pipeline ordered as background correction, optional light deconvolution, then display stretch; the Windows shell only edits and serializes configuration.

## Porting sequence

The detailed status and acceptance criteria live in
[FEATURE_PARITY.md](FEATURE_PARITY.md). The current delivery order is:

1. **Complete:** render FITS and raster files through the Rust DLL into a Win2D canvas, with file/folder opening, navigation, fit, pan, and zoom.
2. **Complete:** catalog status/setup in the Windows ABI plus native Settings for location, readiness, presets, durable progress, verification, and repair.
3. **Complete:** bind the solve response, add the explicit Solve workflow, and present solution quality.
4. **Complete:** draw the solved overlay scene in Win2D with layer and catalog controls.
5. **Complete:** match the current macOS FITS processing interactions with the stackable editor, GHS image sampling, input/display histograms, and live light deconvolution.
6. **Complete:** build an all-users, self-contained WiX MSI, include both
   runtimes, register FITS files, and exercise install/launch/uninstall in CI.
7. **Next:** add thumbnails/cache, multi-window activation, Explorer preview
   integration, signing, and tagged release automation.

Overlay geometry and WCS calculations currently implemented in the macOS view
should move into shared Rust rather than be independently reimplemented in C#.
The platform shells should draw a common overlay scene using native graphics.
