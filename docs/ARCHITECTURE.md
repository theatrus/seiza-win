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
                                           |-- seiza-stars / sensor tilt
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

## Directory image browser

Explicit folder opening recursively discovers supported images off the UI
thread; single-file opening discovers only its top-level siblings. Discovery
is cancellable and superseded requests cannot replace the active collection.
Descendant junctions/symbolic links are not traversed, and inaccessible
subfolders are counted without hiding the rest of the tree.

The pure grouping model produces Folder Path/Filter/File type/None groups and their
flattened navigation order. A virtualized WinUI ListView presents those groups;
display-only regrouping reuses thumbnail view models and preserves selection
by full path, so same-named files in different folders remain distinct.
Filter grouping reuses the stacker's filename classifier without decoding
source files. Collapsible group headers retain their full counts and remain
keyboard-focusable when empty. Expansion state is independent per grouping
mode. Navigation and thumbnail prefetch use the complete grouped order;
changing selection, regrouping, or reopening the sidebar reveals the selected
image by expanding its group. Collapsing a group does not filter navigation or
close the current document. Deferred reveal scrolling rejects stale targets.
Directory stacking uses every FITS/XISF path in the scanned collection,
independent of display grouping, selection, or collapse state. The Include
subfolders control defines recursive versus top-level scope. The stacking
dialog allows explicit frame selection and labels frames/references relative
to the opened root so repeated filenames remain distinguishable.

## Display scaling

The main viewer uses `HighQualityCubic` when reducing a bitmap and
`NearestNeighbor` for source-resolution pixel inspection at 1:1 or larger.
The choice uses actual bitmap dimensions and physical screen pixels, including
the canvas DPI. Reduced stretch previews stay filtered even when enlarged.
The live-stack pane also uses `HighQualityCubic`.

Explorer thumbnails and the Preview Pane share an area-averaging reducer in
the native provider. Every covered source pixel contributes, with weights for
partial edges. It averages stretched display values without changing source
data. Windows owns the thumbnail cache; existing cached icons may remain until
Explorer requests a new thumbnail.

The provider's Rust render tests run on macOS or Linux too. Only its COM and
window code depend on Windows. WinUI builds and managed tests still run in
Windows CI.

### Scaling audit follow-ups

- The locked crates.io `seiza-cabi` 0.19.0 includes
  [seiza#177](https://github.com/theatrus/seiza/pull/177), which fixes C ABI
  reductions for file renders, interactive linear samples, and live stacks.
  The Explorer reducer does not use the C ABI and has its own area filter.
- Replace the fixed 2048-pixel stretch-preview limit with a zoom- and DPI-aware
  render plan, then follow a bounded preview with a full-size render after edits
  settle. Filtering cannot recover detail absent from a small preview. Keep
  cancellation and stale-result checks for both passes.
- Check a real noise-rich FITS/XISF field at fit-to-window, 1:1, and high zoom
  on 100%, 150%, and 200% displays. Move the window between displays and compare
  live previews with committed renders. Automated tests cover sampling math,
  but do not replace this GPU and monitor check.

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
16. Measured-star analysis is independent of catalogs and plate solving. The
    explicit **Analyze stars** command gives the FITS/XISF source path to Rust,
    which owns decoding, luminance preparation, detection, PSF fitting, the
    3x3 cell summary, and tilt math. Schema-1 JSON returns source dimensions,
    stars, cells, tilt, and an explicit normalized-major-axis capability.
17. Plate-solve detections and measured stars are separate overlay producers.
    A composite scene draws either or both through the same image-pixel
    transform in the viewport and in full-resolution 8- or 16-bit export. The
    optional parallelogram tilt diagram reuses the four native corner-cell HFR
    medians. The triangle tilt diagram consumes native three-sector analysis;
    neither renderer decodes pixels or recomputes detector measurements.
18. Parallax video uses an independent full-resolution RGBA16 PNG snapshot with
    the committed processing configuration and WCS/SIP geometry. Seiza owns
    scene preparation, distance lookup, camera fitting, tours, labels and frame
    rendering. Windows owns native inspectors and bounded streaming MP4
    encoding through MediaStreamSource/MediaTranscoder; no pixel math is ported
    to C#. The separate `seiza-platform` DLL adapts focused catalog
    status/setup and file-based color calibration through published Rust APIs,
    not a fork of the shared C ABI. Core/C ABI 0.42.0 and parallax 0.19.0 come from published
    crates, with the upstream font notice included in the app. See
    [Parallax video](PARALLAX-VIDEO.md).
    Batch mosaics use the C ABI's expanded-canvas planner with all selected
    lights, an explicit memory ceiling and a retained cancellation signal.
    Planning starts empty: the anchor is pushed exactly once with every other
    light. Windows displays native planning/lens notes and retains source
    protection during reintegration/export. Live acquisition keeps a fixed
    canvas. The 0.40 SNR ABI includes the appended bin-factor field; its managed
    allocation must be 80 bytes on x64, not the old 72 bytes.
    Optional coordinate-based grouping solves raw lights through the same bounded
    solver as the viewer. The platform adapter compares sampled sky footprints
    using the published core's TAN/SIP transforms; Windows owns complete-link
    repeated-pointing clusters, connected fields, filter partitions and native
    panel/exposure checkboxes. Unverified solves and calibration frames are excluded.
    Deselection recomputes connectivity, also within each filter; source stamps
    invalidate changed selections before integration. This selects inputs only:
    native star registration still determines actual integration eligibility.
    The persisted stacking GPU preference defaults on and is read when a new
    batch/live run is configured. Native `gpu.enabled=false` is also passed to
    defect learning; reintegration inherits the stack's native GPU options.
    Resume compatibility excludes only GPU execution policy, like the native
    fingerprint, and a restored run adopts its saved GPU mode/options JSON rather
    than changing an existing accumulator. Other processing settings still gate
    compatibility. Settings initialization does not write preferences; failed
    GPU-setting saves restore the persisted control value and show an error.
19. Multi-filter live sessions own one existing live coordinator/native handle
    per canonical filter identity. Windows routes discovery and selects UI
    previews; Rust still performs all calibration, registration and accumulation.
    Startup/ingestion shares a two-operation semaphore; per-handle serialization
    remains mandatory. An exclusively leased, atomic filter registry restores
    groups even when captures have moved and records completed filters. Each
    group retains its own native checkpoint and source ledger; closing saves all
    groups, not just the selected one. A failed filter does not stop discovery
    or ingestion in the others. RC-Astro's executable path is a persistent user
    setting, observed by each open Parallax session.
    The live overview projects every group's current snapshot into stable,
    keyboard-accessible tiles. It reuses bounded native previews without extra
    rendering jobs, uploads only changed images, and releases tile GPU resources
    when returning to the individual view or closing. Layout and presentation
    never alter accumulator state or the selected filter's export scope.
    Live stretch changes use each coordinator's operation gate and redraw even
    when paused; newly discovered groups inherit the session's current recipe.
    A serialized registry publication is flushed before a completed child's
    checkpoint is retired. Failed publication leaves that checkpoint recoverable.
    Export preflight and final publication check all groups' inputs/calibration,
    the complete capture/checkpoint trees, resolved paths and hard-link aliases.
    Calibration preparation errors stop just the affected filter without counting
    readable lights as unreadable or automatically repeating a declined review.
    RC-Astro changes coalesce until the receiving Parallax window has no active
    native operations, frame renders or refits, preserving those operations' inputs.
20. RGB/LRGB file composition uses the shared C ABI; Windows requires explicit
    confirmation of a common alignment grid. The small platform color adapter
    reads linear FITS/XISF, calls `seiza-stacking` Gaia calibration, and writes
    linear output with the native WCS and `SEIZAPCC=GAIA` marker. It owns no
    photometry or pixel algorithms. Full-resolution pixels stay in Rust.
    Windows owns optional solve verification, serialized jobs, same-volume
    staging, source identity checks, final publication and result presentation.
    Skips and failures never replace the destination. Marked results convert
    unlinked display stages to linked stages for both BGRA8 and RGBA16 renders.
    Platform-returned strings use `seiza_win_string_free`, never the core DLL's
    allocator. Optional photometry installation requires an explicit action.
21. Learned sensor defects use the native detection ABI with at least three
    compatible dithered lights, never a master dark. A live filter waits until
    enough stable lights exist, then fixes the map for its entire accumulator.
    Native checkpoint restore retains that map independently of source files;
    no resumed stack relearns it. The manifest separately records the learning
    policy, and learned stacks reject calibration swaps to prevent applying a
    learned correction on top of a new dark or different bias/flat. Master-cache fingerprints use the native
    stacking-output version (the legacy manifest field remains `CoreVersion`).
    Output-version pointers are static and must not be freed.
22. Optional flat quality review passes the native metadata planner's exact
    selected raw paths to `seiza_calibration_measure_flat_json` and sends complete
    typed measurements back to `seiza_calibration_screen_flats_json`. No quality
    thresholds, pixel measurements, dust geometry or selection decisions are
    implemented in C#. Review runs independently of master-cache hits, one native
    frame at a time behind a shared gate, with identity and cancellation checks
    before publication. Failure produces an explicit warning, not a partial
    passing report. Quality options do not change master cache fingerprints.
    Windows owns report presentation, expandable details, complete clipboard
    reports and explicit continue/cancel. New live filter groups await review
    separately; dialogs are serialized and cancellation/close releases waits.
    Checkpoint resume retains its prior calibration rather than requiring raw
    frames again. Native master predictions remain distinct from actual built
    master findings and existing preparation warnings.

## Performance rules

- Never perform per-pixel work in C#.
- Analyze stars from the original FITS/XISF path in the native core; never
  derive detector input from the stretched BGRA viewport or construct a
  full-frame luminance copy in C#.
- Start the interactive action with two-pixel detection binning, sensitivity
  30, and Moffat-4 fits. Request a target of 200 measured stars so the native
  core can retry a pass below that total with its bounded adaptive ladder: first
  a relaxed SNR gate, then, only if still below target, native-resolution
  unblurred detection. Consume one complete returned pass; never merge
  measurements from different rungs. The
  core still derives wide-field, standard, or long-focal policy from source
  headers and reports every coordinate and PSF size in source pixels.
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
- Key measured-star results by normalized absolute source path, file length,
  last-write timestamp, Windows file identity when available, native-core
  version, and serialized detector options.
  Share identical in-flight requests, keep the cache bounded, and admit only
  one native detector job at a time.
- A canceled or superseded analysis may finish inside the synchronous native
  call, but source identity and document generation must be checked before and
  after it; navigation discards the stale result and never attaches its
  overlays to the next image.
- Draw at most the 1,000 sharpest usable measured-star markers and 100 HFR
  labels so zooming and panning remain responsive. Treat a tilt cell with
  fewer than three stars as a low sample, withhold arbitrary
  best/worst emphasis when the reliable spread is negligible, and draw mean
  elongation only when a cell has at least three stars, the response advertises
  normalized major-axis angles, and directional coherence exceeds the
  threshold. The UI must remind users to confirm tilt across multiple frames.
- Draw the parallelogram HFR diagram only when all four corner cells meet that
  same three-star reliability rule. Normalize its four radial vertices to the
  softest measured corner and 40% of the source image's shorter dimension,
  label values explicitly as HFR, and keep the layer off by default so it does
  not obscure the sensor grid or star markers.
- Draw the triangle HFR diagram only from the native three-sector result, with
  all sectors meeting the core-reported minimum sample count. Preserve the
  returned image-coordinate angle, sector medians, and readiness verdict; do
  not substitute the unrelated four-corner tilt percentage.

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
10. **Complete:** prepare matched bias/dark/dark-flat/flat masters, report
    stack-depth SNR, and run resumable live stacks from reconciled capture
    folders with bounded previews and crash-safe checkpoints. Real telescope
    data has exercised preview rendering, SNR sampling, snapshot export, pause,
    relaunch, exact resume, continued ingestion, and completed-session
    retirement.
11. **Complete:** expose an explicit,
    solve-independent star-analysis action, inspector results, measured-star
    overlay, nine-cell sensor-tilt grid, and parallelogram/triangle HFR diagrams
    through one composited viewport and export scene. The published Seiza
    0.18.8 path-analysis, adaptive-detection, and triangle-sector C ABI is
    locked. Its registry-built DLL increased a real C925 FITS image from 71
    strict-pass stars to a 146-star adaptive pass with all nine cells reliable;
    a real XISF image remained bit-for-bit identical at 468 because its first
    pass met the target. Both triangles were ready, exact native fields and
    formulas matched, and the sources remained unchanged. An isolated
    Release-built x64 app then exercised viewport analysis and three
    full-resolution composited exports of the 3,840 x 2,160 C925 source: a
    32-bpp Bgra32 measured-stars/grid PNG, a 64-bpp Rgba64 parallelogram-only
    PNG, and a 64-bpp Rgba64 triangle-only TIFF. WIC decoding and visual
    inspection confirmed the requested layers, source registration, complete
    labels, smooth gradients, and no chrome, corruption, seams, or rescaling.
12. **Next:** add cached previews during full-resolution loads.

Overlay geometry and WCS calculations currently implemented in the macOS view
should move into shared Rust rather than be independently reimplemented in C#.
The platform shells should draw a common overlay scene using native graphics.
