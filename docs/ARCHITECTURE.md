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
                                           |-- built from the crates.io seiza-cabi release
                                           |-- seiza-fits
                                           |-- seiza-xisf
                                           |-- image
                                           `-- seiza

Windows Explorer / dllhost.exe
    `-- IThumbnailProvider -- SeizaThumbnailProvider.dll (Rust)
                              |-- bounded IStream input
                              |-- seiza-fits / seiza-xisf decode
                              `-- autostretched top-down BGRA HBITMAP

Windows Explorer / prevhost.exe
    `-- IPreviewHandler ---- SeizaThumbnailProvider.dll (Rust)
                            |-- bounded IStream input
                            |-- isolated, low-integrity preview host
                            `-- child HWND with autostretched BGRA HBITMAP
```

The Explorer thumbnail and Preview Pane providers are separate COM classes in
one native Rust DLL, independent of WinUI, .NET, catalog loading, and plate
solving. Windows loads thumbnails through an isolated `dllhost.exe` and previews
through the x64 low-integrity `prevhost.exe`; the MSI intentionally does not
disable process isolation.

## Locked decisions

1. The supported first release is Windows 11 x64. ARM64 follows after parity.
2. WinUI 3 owns application chrome and standard interactions.
3. Win2D owns interactive image and vector-overlay presentation.
4. Rust owns FITS/XISF decoding, stretching, statistics, registration/stacking,
   WCS, solving, and catalog data.
5. No Rust layout, allocator-owned string, or panic crosses the C ABI.
6. Pixel buffers cross through opaque handles; versioned JSON carries metadata and solve records.
7. The process hosts multiple document windows and redirects new file activations
   into the existing process. A custom entry point registers Windows App SDK
   `AppInstance` before XAML initialization and carries native file activations;
   a current-user named pipe keyed by the registered process ID carries ordinary
   quoted `%1` paths from the unpackaged MSI association.
8. Distribution is an all-users, self-contained WiX 4 MSI with Windows Default
   Apps registration for FITS and XISF files. The MSI carries .NET and Windows App SDK
   runtimes; production releases must be code-signed.
9. The Windows app builds the unified upstream `seiza-cabi` crate from
   crates.io; no C ABI implementation is forked in this repository and no Cargo
   dependency uses a Git source.
10. `Cargo.lock` fixes the complete Seiza dependency graph. The native build
    emits the resolved C ABI version and Cargo-packaged VCS commit as application
    metadata, and the About dialog reports both values.
11. Explorer content thumbnails use `IThumbnailProvider` plus
    `IInitializeWithStream` in a native Rust DLL. The handler accepts bounded
    stream input, never loads app/runtime/catalog state, and remains in Windows'
    default out-of-process shell-extension host.
12. Explorer Preview Pane images use a second COM class implementing
    `IPreviewHandler`, `IInitializeWithStream`, `IObjectWithSite`, and
    `IOleWindow`. It defers decoding until `DoPreview`, renders into a child
    window owned by `prevhost.exe`, forwards accelerators to the host, and
    releases the stream, bitmap, and window during `Unload`.
13. Full-resolution PNG/TIFF export requests RGBA16 directly from `seiza-cabi`.
    The managed buffer remains 16-bit through optional overlay compositing and
    is passed directly to the Windows Imaging Component encoder without a
    second full-frame conversion copy.

## Performance rules

- Never perform per-pixel work in C#.
- Upload a rendered image once; pan, zoom, and overlay visibility changes must not rerender pixels.
- Prioritize the visible image over adjacent thumbnails and cache maintenance.
- Bound background concurrency and memory use.
- Add a tiled rendering API only after measurements show full-image upload is a bottleneck.
- Keep cached previews visible while full-resolution work is in flight.
- Preserve Explorer thumbnail aspect ratio, never upscale source pixels, cap
  requested output dimensions, and rely on Explorer's thumbnail cache rather
  than adding process-global catalog or app caches to the shell extension.
- Apply the same bounded-input and decode preflight rules to Preview Pane
  rendering, cap either shell surface at 4,096 pixels, and release all stream,
  bitmap, and window state from `Unload`.
- Preflight FITS/XISF dimensions, sample storage, compression, and RGB/Bayer
  expansion before decoding. Reject any request whose conservative peak
  working-set estimate exceeds 1.5 GiB; process isolation is not a substitute
  for bounding attacker-controlled allocations.
- Render interactive processing drafts through the shared JSON C ABI at a bounded 2,048-pixel dimension, cancel stale UI results, and retain the committed full-resolution bitmap until Save succeeds.
- Keep the shared pixel pipeline ordered as background correction, optional
  light deconvolution, then display stretch; the Windows shell only edits and
  serializes configuration. Seiza 0.14 owns background sampling, automatic
  held-out model selection, polynomial and radial-basis fitting, correction,
  and diagnostics.
- Keep image registration, normalization, rejection, calibration, and stack
  accumulation in Rust. The Windows shell may group filenames and schedule
  batches, but it only crosses the C ABI once per input frame and cancels at
  frame boundaries.

## Porting sequence

The detailed status and acceptance criteria live in
[FEATURE_PARITY.md](FEATURE_PARITY.md). The current delivery order is:

1. **Complete:** render FITS, XISF, and raster files through the Rust DLL into a Win2D canvas, with file/folder opening, navigation, fit, pan, and zoom.
2. **Complete:** catalog status/setup in the Windows ABI plus native Settings for location, readiness, presets, durable progress, verification, and repair.
3. **Complete:** bind the solve response, add the explicit Solve workflow, and present solution quality.
4. **Complete:** draw the solved overlay scene in Win2D with layer and catalog controls.
5. **Complete:** match the current macOS astronomy processing interactions with
   the stackable editor, GHS image sampling, automatic/polynomial/radial-basis
   background controls, input/display histograms, and live light deconvolution.
6. **Complete:** register and stack directory FITS/XISF light frames with
   filter grouping, calibration, progress, cancellation, and 32-bit FITS output.
7. **Complete:** build an all-users, self-contained WiX MSI, include both
   runtimes, register FITS and XISF files, and exercise install/launch/uninstall in CI.
8. **Complete:** render native FITS/XISF content thumbnails through a bounded,
   stream-based Rust COM provider registered by the MSI and isolated in
   `dllhost.exe`.
9. **Complete:** route launches and file activations into one app process with
   independent document windows, export true RGBA16 PNG/TIFF files, and render
   FITS/XISF images in Explorer's Preview Pane through an isolated native COM
   handler.
10. **Next:** add cached previews during full-resolution loads and Authenticode
    signing.

Overlay geometry and WCS calculations currently implemented in the macOS view
should move into shared Rust rather than be independently reimplemented in C#.
The platform shells should draw a common overlay scene using native graphics.
