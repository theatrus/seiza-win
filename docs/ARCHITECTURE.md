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
    |-- watched-folder scheduling, checkpoint manifests, and session UI
    `-- generated P/Invoke bindings with SafeHandle ownership
                         |
                         `-- C ABI -- seiza-cabi.dll (Rust)
                                           |-- built from the crates.io seiza-cabi release
                                           |-- seiza-fits
                                           |-- seiza-xisf
                                           |-- seiza-stacking live contexts / SNR
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
14. Calibration classification, compatibility matching, coherent-set
    selection, master construction, live-stack registration, online rejection,
    and SNR measurement remain shared Rust policy. Windows owns only file
    discovery, workflow choices, progress, and presentation.
15. A resumable live stack is one opaque, versioned Rust context paired with a
    Windows manifest. Publication advances an atomic generation pointer only
    after both files are durable, retains the preceding complete generation,
    and accepts a restore only when native identity and manifest agree.

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
- Keep the shared display pipeline ordered as background correction, optional
  light deconvolution, optional linked robust `sample_domain` mapping, then
  display stretch; the Windows shell only edits and serializes configuration.
  Seiza owns range sampling and resolution as well as background sampling,
  automatic held-out model selection, polynomial and radial-basis fitting,
  correction, and diagnostics.
- Keep image registration, normalization, rejection, calibration, and stack
  accumulation in Rust. The Windows shell may discover/group filenames and
  schedule batches, but header classification, calibration planning, and frame
  disposition cross the C ABI as versioned JSON rather than being
  reimplemented in C#.
- Treat calibration matching as layered admission: every target light is
  checked during native planning, candidate sets are proximity-ordered and
  reduced through alternate-anchor plus coherent-session passes, and the
  native master builder checks the actual reread headers once more. A corrected
  schema-2 master report partitions requested paths into accepted `inputs` and
  reasoned `skippedInputs`; schema-1 partial reports are ambiguous and must fail
  closed. Windows uses Seiza's exported matching functions while preparing a
  flat dependency chain; the native live stacker remains authoritative for
  per-light calibration admission, including scalable darks and restored
  calibration state.
- Cache built calibration masters by a core-versioned input fingerprint and
  publish them atomically. Bound the shared library cache by age and size,
  protect every master still needed by a multi-group preparation run, and use
  process plus file leases so cleanup cannot race a build.
- Render live stacks directly from the native accumulator at a bounded output
  dimension. The accumulator mean remains physical linear `f32`; only the
  render buffer passes through the configured `sample_domain`. Robust mapping
  resolves one linked percentile range across mono or RGB before display
  stretch, avoiding an implicit per-channel color balance. Never copy the full
  mean merely to refresh a preview; uncovered samples remain masked through
  background fitting, deconvolution, sample-domain mapping, and stretch.
- Export non-destructive live snapshots through a mean-only native snapshot.
  Do not clone the accumulator's variance, coverage, or rejection maps merely
  to write FITS; this keeps peak memory bounded for very large mono frames.
- Keep display sample-domain mapping separate from frame-to-frame stack
  normalization. SNR analysis, resumable checkpoints, snapshots, and final FITS
  output use the physical accumulator and never persist or measure the mapped
  preview pixels.
- Serialize all operations on an opaque live-stack handle. Copy any borrowed
  native view before releasing that gate, checkpoint calibration changes before
  accepting more inputs, and treat the native ordered path ledger as the
  authoritative resume/deduplication record.
- Measure SNR from the accumulator at doubling frame depths and the final
  depth. Recompute comparisons with one common deepest-stack signal so noisy
  early-percentile estimates do not exaggerate improvement.

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
10. **Implemented:** prepare matched bias/dark/dark-flat/flat masters, report
    stack-depth SNR, and run resumable live stacks from reconciled capture
    folders with bounded previews and crash-safe checkpoints. Real telescope
    data has exercised preview rendering, SNR sampling, snapshot export, pause,
    relaunch, exact resume, continued ingestion, and completed-session
    retirement.
11. **Next:** add cached previews during full-resolution loads.

Overlay geometry and WCS calculations currently implemented in the macOS view
should move into shared Rust rather than be independently reimplemented in C#.
The platform shells should draw a common overlay scene using native graphics.
