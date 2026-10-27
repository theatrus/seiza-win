# Expanded mosaic stacking

Expanded stacking shipped in Windows 0.8.6. This guide describes the Windows
0.8.8 layout, including parity updates from Mac PR #91. The published
Seiza/C ABI 0.44.2 and stacking 0.51.2 dependencies are unchanged; no Git or
vendored core dependency is used.

## GPU preference

In **Settings → Stacking performance**, **Use GPU for stacking** defaults on
(automatic). Turn it off for CPU-only processing. This applies to new fixed and
expanded batch stacks and new live stacks, including defect learning and final
reintegration. Seiza uses Direct3D 12 or another available native backend and
falls back to CPU processing if a GPU operation is unavailable. Small frames may
remain on the CPU even when enabled. GPU rounding can differ from CPU results;
byte-identical outputs are not promised.

**GPU planning budget (MiB)** defaults to **0 / automatic**, which uses the
device's capacity. Set a smaller positive budget to leave room for other
programs: a large accumulator moves onto the CPU sooner, and reintegration uses
smaller bands. This is a planning budget, not a strict ceiling on allocations;
other frame kernels, staging buffers and the idle pool can use more memory.
Values above device capacity are clamped by the core. This is separate from the
maximum canvas limit below.

Active live stacks retain their settings. When reopening a saved stack, the app
uses the **current GPU mode and budget** through the published GPU-resume API.
An otherwise compatible checkpoint keeps its accumulator, frame counts and
configuration fingerprint; a notice explains a policy change. New checkpoints
record the new policy. Processing-option changes still require a compatible
session. Both preferences persist immediately and across app restarts.

![Native Windows GPU mode and planning-budget controls](images/stacking-gpu-budget-settings.png)

## Stack selected lights on an expanding canvas

1. Open images, a directory, or a TXT/CSV image list and choose **Stack**.
2. At the top of the dialog, choose **Stacking mode → Expanded mosaic / dithered edges**.
3. Select the lights, review filter splitting, calibration and the canvas memory
   ceiling, then choose **Choose output and stack…**.

Each output registers its selected lights onto one expanding canvas. Inputs can
come from different folders, and panels can connect through other panels. No
sky-panel analysis or pre-solving catalogs are required. Star registration
decides which lights join; disconnected images may be rejected. Filter splitting
creates one canvas for each output. Canvas size and available resources still
bound processing; there is no 512-light selection cap.

![Stacking mode and expanded-canvas ceiling before light selection](images/mosaic-stacking/stacking-mode-first.png)

Windows 0.8.8 controls shown with a synthetic 513-light selection. This
checks selection admission and layout; it is not an integration benchmark.

## Optional sky-panel analysis

1. Open a collection and choose **Stack**. Choose the stacking mode explicitly.
2. Expand **Optional sky-panel analysis** and enable **Group lights by solved sky position**. In **Light frames —
   select inputs**, select the FITS/XISF lights to analyze, then choose **Solve
   and group selected lights**. The standard blind-solving catalogs must already
   be installed through catalog setup. At least two lights are required.
3. Review **Select panels to include**. Confirmed sky coordinates group repeated
   pointings into panels and connect overlapping panels into sky fields. Check
   a panel to include all its exposures, or expand it to select individual lights.
   **All** includes only successfully solved lights in this mode; **None** clears
   the selection. Calibration, unsolved and unverified-header inputs are listed
   under **Excluded**, never silently included.
4. Review the selected-light/output summary. Unrelated sky fields produce separate
   outputs; filename-filter splitting remains available. Removing a connecting
   panel splits disconnected regions into separate outputs, including within
   each filter. Each output needs at least two selected lights (one exposure in
   each of two overlapping panels is sufficient).
5. Analysis keeps the chosen **Fixed reference frame** or **Expanded mosaic / dithered edges** mode and any custom overlap. Review the
   stacking/calibration options, then choose **Choose output and stack…**. For
   multiple outputs, choose a folder and base name; field/filter suffixes are
   appended. Registration still checks every selected light and reports failures.

Grouping uses approximate TAN/SIP sky footprints, not filenames or a close RA/Dec
center alone. At least 85% mutual coverage groups repeated pointings; 5% connects
panels. Repeat clustering checks every member, preventing a chain of dithers from
collapsing a whole mosaic into one panel. These are grouping heuristics, not a
guarantee that star registration will succeed. Solves require star confirmation;
an unchecked embedded WCS is insufficient. Native Seiza performs all WCS transforms.

Cancellation discards the incomplete analysis and retains any previous complete
selection. Sources are checked for replacement/modification during solving and
again before stacking. No WCS is written to the input images. Re-solving resets
the panel selection; switch back to manual mode to change the input list or
override automatic grouping.

Large selections use smaller native row batches (at most eight rows and about
4096 pair comparisons, or one row). Cancellation is checked during footprint
preparation, between batches and during grouping. The full footprint matrix
still uses time and memory proportional to the square of the solved-light
count; removing the selection cap does not make this analysis linear.

Automatic calibration prepares one compatible set per output across that output's
eligible raw lights. Panels and folders within one output share that calibration
set. Calibrate lights requiring different flat sessions separately before
combining them into one mosaic output.

![Automatic coordinate-grouping setup in the native Windows dialog](images/mosaic-stacking/automatic-setup.png)

![Native panel selection with expandable individual exposures](images/mosaic-stacking/panel-selection.png)

The captures use diagnostic crops and repeated copies of an M31 image, plus an
unrelated M101 field and a calibration flat, passed through the real catalog
solver. They illustrate grouping/selection, not independently captured exposures.

## Choosing a canvas

Open **Stack** and choose **Stacking mode** at the top:

- **Fixed reference frame** keeps the existing reference-sized grid and 60%
  default minimum overlap.
- **Expanded mosaic / dithered edges** plans a canvas for connected overlapping
  panels or preserves a dithered stack's outer edges. Panels can connect through
  other panels and differ in rotation. The selected/automatically ranked first
  light anchors the geometry and photometric scale; it is integrated once, like
  every other light. The default minimum overlap becomes 5%. Custom overlap
  values are retained when switching modes.

Existing **Similarity**, **Affine**, **Quadratic**, weighting, calibration and
transient-removal controls remain available. Expanded canvases support **None**,
**Global**, and **Local background** normalization. **Local** requires a fixed
canvas; choosing it for a mosaic disables starting the run and explains the
alternative before any inputs are read. Quadratic follows field distortion.
Earlier published stacking 0.44.0 includes the expanded Local background
sky/color fix and improved mild-lens distortion fitting.
Planning/lens notes appear in the completion summary,
including lights that did not join the plan. Per-light rejections are still
reported. An output requires at least two accepted lights.

**Maximum canvas** counts pixels times channels, not only pixels: an RGB canvas
uses three samples per pixel. Native processing can peak near 40 bytes per
sample, so the default 256-million-sample ceiling permits about 10 GB of working
memory. Lower it for machines with less available RAM. Planning can be
cancelled; the core checks cancellation between reading lights. An individual
read/registration is synchronous and cannot be interrupted mid-operation.

Expanded canvases cannot use Bayer drizzle or a shared fixed alignment image.
Separate filter mosaics plan independently and are not promised to share a grid
for RGB/LRGB composition. Live acquisition retains its fixed canvas: this API
plans from a complete input list rather than growing a future capture stream.
Binned/superpixel stacking and drizzled/full-resolution reintegration are not
new UI controls in this change.

![Native Windows expanded mosaic canvas controls, showing an example 32-million-sample ceiling](images/mosaic-stacking/canvas.png)

![Four-panel synthetic mosaic output rendered by Seiza](images/mosaic-stacking/example-result.png)

Synthetic demonstration: four overlapping 480 × 360 FITS panels containing
procedural stars and diffuse emission were integrated by the production batch
stacker into a 760 × 541 canvas (four accepted, zero rejected). This is generated
test data, not an observed sky image; the result uses Seiza's normal image renderer.

## Viewing large mosaics

The Windows viewer opens mosaics wider or taller than the graphics device's
single-bitmap limit (commonly 16,384 pixels). Large display images use Win2D
virtual bitmaps with on-demand GPU caching. Fit, zoom and pan retain the source
resolution; small images keep the normal resident-bitmap path. Full-size
pixel sampling uses the same display samples across cached regions.

Clipboard and 8-bit PNG/JPEG/TIFF exports encode those samples directly, without
an oversized GPU target. Overlays for both 8-bit and 16-bit exports render in
bounded tiles at the original image coordinates. Display colors and output
dimensions are preserved. Source rendering still needs enough CPU memory for
the complete image, and managed pixel-buffer and output-format limits still
apply; this does not add disk-backed scientific processing.

Regression checks cover 20,001-pixel-wide and 20,003-pixel-tall images, exact
pixels beyond 16,384, hardware-default/software rendering, filtered fractional
zoom, overlay continuity and lossless PNG/TIFF round trips. See the
[Windows validation notes](SEIZA-0442-VALIDATION.md) for the interactive scope.

## Faster solver: measured against 0.39.0

The existing bounded image-solve entry point now uses Seiza's shared image-file
solver, faster blind candidate selection and full-catalog refinement. Viewer
and parallax solving receive those improvements without a separate Windows
solver implementation. Existing cancellation, 120-second limit, scale range
0.1–20 arcsec/pixel and default SIP order 0 are retained.

Local measurements on an Intel Core i7-12700H use the exact old and new registry-built C ABI DLLs, the
same existing catalog/index and unmodified source images. Each row is the
median of three runs after warming the filesystem cache. No claim is made for
cold-start speed or all fields/hardware.

| Image | Solve route | 0.39.0 | 0.40.1 | Matched stars | RMS (arcsec) |
| --- | --- | ---: | ---: | ---: | ---: |
| Tulip JPEG | Blind | 2,024 ms | 350 ms | 88 → 495 | 0.819 → 0.475 |
| Big Q TIFF | Blind | 2,672 ms | 655 ms | 108 → 388 | 1.909 → 1.512 |
| M31 FITS | Checked header | 267 ms | 198 ms | 116 → 584 | 0.935 → 0.662 |

All three source hashes remained unchanged. These are end-to-end C ABI calls,
including result/overlay assembly, not only the inner solver's timer.

A follow-up with the published **0.41.0** DLL built using Rust **1.99.1** returned
medians of **319 ms** (Tulip), **720 ms** (Big Q) and **212 ms** (M31), again over
three calls per unmodified source. The historical table above was measured with
the earlier compiler; these follow-up figures are not a controlled compiler
comparison or a cold-start claim.

## Adoption and review notes

The 0.44.2 adoption includes the GPU memory planner, ahead-of-time frame
preparation, parallel blind-registration scoring, early expanded/Local
normalization rejection, and RC-Astro's Windows console-window fix. The app
uses the published GPU-resume export to apply current policy to existing
accumulators. A native 0.42.0 checkpoint reopened under 0.44.2 with unchanged
dimensions, counts, source ledger and configuration fingerprint after changing
to CPU-only with a 4096 MiB budget.

This update covers the existing raw-light stacking workflows and adds the
[narrowband, continuum and RGB star tools](COLOR-TOOLS.md). The newer
registered-panel background-matching API takes already aligned panel
integrations; it does not run implicitly on raw-light mosaic stacks.

The C ABI SNR result grew an appended `bin_factor` field in 0.40. The Windows
binding now reserves the full 80-byte x64 struct, with a regression test for
size and field offset. The old allocation could corrupt the managed stack.
State validation also accepts the planner's empty, unintegrated canvas without
mistaking it for a missing shared-reference image.

Upstream mosaic fixes [#338](https://github.com/theatrus/seiza/pull/338) and
[#339](https://github.com/theatrus/seiza/pull/339) are included in the published
stacking 0.44.0 adoption. Core/C ABI 0.41.0 also includes faster banded batch
loading and neutral clipped-color calibration. The earlier solver table is a
historical 0.40.1 comparison, not a new 0.41.0 benchmark or Rust-compiler comparison.
