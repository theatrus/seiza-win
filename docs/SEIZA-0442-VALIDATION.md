# Seiza 0.44.2 Windows adoption validation

Validated on Windows x64, 2026-10-25, with Rust 1.99.1 and .NET 10. This is a
follow-up to merged Windows PR #70. Dependencies remain published registry
packages: Seiza/C ABI 0.44.2, stacking 0.51.2, parallax 0.19.2 and satellites
0.22.2. The native release source is
`d955f83055432e21a435960dbf802d0e6d940b48`.

## GPU policy and memory budget

The actual production Settings window was exercised in an isolated diagnostic
build with a separate settings file. The planning budget was changed from
automatic to 4096 MiB, then 128 MiB, and survived reopening Settings. Turning
GPU off disabled the budget control while retaining its value. A full process
restart retained CPU-only / 128 MiB. Production batch and live dialog option
creation agreed with the persisted values.

| Production batch runs, with final reintegration | Accepted fixed / mosaic | Elapsed total |
| --- | --- | ---: |
| GPU enabled, 128 MiB | 3/3 and 8/8 | 5.795 s |
| CPU-only, 128 MiB retained | 3/3 and 8/8 | 1.822 s |

These are small M31 diagnostic crops/repeats, not independent telescope
exposures. Times include startup, and the GPU run overlapped compilation.
They are smoke-test timings, not a controlled speed comparison.

The enabled run selected Intel Arc A770M / Vulkan and logged actual device
computation, not only adapter detection. Its 1312 MiB accumulator requirement
exceeded three quarters of the 128 MiB planning budget, selecting a CPU
accumulator and small reintegration bands. The trace's device reservation
exceeded 128 MiB, consistent with the documented soft planning budget.
The disabled run logged no adapter/device-compute/GPU-error entries. All
diagnostic source SHA-256 hashes were unchanged in both runs.

The earlier dedicated Arc / Iris Xe iGPU, D3D12 / Vulkan memory-planner matrix
belongs to [core PR #363](https://github.com/theatrus/seiza/pull/363). That full
matrix was not repeated for this app update; the current app smoke test above
uses the published default adapter selection.

A native checkpoint saved under 0.42.0 reopened under 0.44.2 with a CPU-only /
4096 MiB override and identical dimensions, frame counts, source ledger and
configuration fingerprint. Managed tests cover changing GPU policy in both
directions and changing the budget after the original reference file is gone.
Expanded canvases with Local normalization fail validation with alternatives
before a stack can start.

## Color contracts

Native and managed tests cover signed/NaN-preserving continuum subtraction,
exact-source saved-plan replay, stale-source rejection, missing-saturation
skips without manual fallback, clipping-mask validation and output aliases.
A synthetic 720 × 720 Gaussian star field recovers its known 0.7 continuum
coefficient within 0.001, passes validation with and without Gaussian PSF
matching, and its reviewed plan produces residuals below 0.01 in original
sample units.

Analytical pixel checks cover linear RGB star extraction on a common scale,
prepared-layer screening without another stretch, HOO/SHO display composition,
linked linear-to-display preparation and conflicting transfer declarations.
FITS and XISF display outputs preserve the expected colors through the app's
8-bit preview, 16-bit export, processing clipboard and Explorer thumbnail /
preview renderer. Failure and pre-cancellation preserve existing outputs;
reports cannot replace a source, plan or result.

The production native color controls were exercised interactively in a
diagnostic entry point that bypasses single-instance activation routing.
All five workflows built visible previews using owned synthetic FITS/XISF
fixtures: HOO, continuum subtraction, RGB star extraction, restoration from
original/starless RGB, and restoration from a prepared display stars layer.
Native file pickers saved FITS and XISF, and a saved display result reopened
with No Stretch. These are UI/scientific smoke checks, not real telescope
color-quality validation.

The dialog now follows the PSF Guard preview-first pattern: required sources
and common controls are visible, optional processing/fit controls start
collapsed, and saving follows preview review. Input changes disable saving
stale previews. Automatic analysis with unknown saturation visibly skipped;
with producer ceilings it recovered 0.7 using 168 fit and 57 validation stars
(validation fractional RMS 8.71e-9). Its report was saved and replayed.
Importing that report into an empty form restored both source paths and
65535 saturation ceilings, reset input confirmation, and successfully rebuilt
the residual. Delayed WinUI field events no longer discard the imported plan;
editing a source still disables both plan-based building and stale saving.
The saved residual retained its LINEAR marker and ranged from -6.77149e-5
to 9.03689e-5, with all eight fixture source hashes unchanged.
Wide and narrow window layouts were inspected; the narrow form scrolls
above its preview while the status and actions remain visible.
Interactive saving exposed a transfer-header loss in format conversion;
the adapter now explicitly preserves SEIZA transfer/provenance cards.
Regression checks cover both FITS/XISF directions, unchanged reviewed pixels
even when original sources change, and negative/NaN preservation.

## Large mosaic display and export

The viewer now uses Win2D's virtual bitmap path when either image dimension
exceeds the current device's maximum bitmap size. An immutable, seekable BMP
view exposes the native BGRA samples without encoding a second full image or
writing a temporary raster. Direct2D handles on-demand GPU tiles and filtering;
ordinary images retain the resident-bitmap path.

Windows graphics regression tests compare exact source pixels across cached
regions and at far edges for 20,001 × 17 and 17 × 20,003 images, on both the
default device and forced software renderer. Filtered gradient comparisons
cover 100%, 75%, 20% and 5% zoom. Overlay rasterization reaches past pixel
16,384 without a full-image render target. PNG and TIFF encoders round-trip
large width/height and exact BGRA colors. CPU snapshots survive display-resource
disposal; cancellation before virtual loading leaves source pixels intact.

An isolated production App entry point loaded a synthetic 20,001 × 1,025 FITS
fixture and exposed its correct dimensions and normal viewer controls. The
initial desktop check was blocked by access errors. A later interactive retry
displayed that fixture and two 20,001 × 2,160 M31 diagnostic copies. Loading,
zooming and panning were observed without a GPU dimension error.

The astronomical source available for this retry was 3,840 × 2,160. Its samples
were placed unchanged in the middle of the larger canvas, first with integer
black padding and then with float NaN coverage outside the observed field.
The float copy also carried the source's previously verified WCS translated to
the padded grid. These are derived fixtures, not independently observed wide
mosaics. Original and fixture hashes remained unchanged during testing.

The integer fixture's interactive Solve returned unverified header coordinates.
A direct 0.44.2 C ABI solve of the NaN fixture returned `header-unchecked`, zero
matches and 600 detections after 56.339 s (55.638 s native elapsed). A control
solve of the unchanged original returned `header-checked`, 584 matches and
0.662 arcsec RMS in 227 ms. The retry confirms large-image viewing, but does
not establish successful solving of a >20k astronomical mosaic. Automated
GPU rendering and codec checks above passed; a representative full mosaic
remains a separate validation input.

These changes remove the per-texture dimension limit. Native rendering still
materializes the complete CPU image and uses managed pixel arrays; available
memory, array sizes and encoder-format limits remain applicable.

## Checks

- Release solution build: zero warnings/errors.
- Unsigned MSI build and static Finish/Shell sequencing check pass; no install.
- Managed suite: 796 passed.
- Windows suite: 96 passed, 4 optional real-source/encoder tests skipped.
- Windows platform adapter: 22 passed; Explorer provider: 30 passed.
- Published native suites: C ABI 145; stacking unit 503 (5 ignored), caller-pool
  1, integrations 20 + 12, output-version 55, doc 1; parallax 141 passed.
- Rustfmt, workspace Clippy with warnings denied, and Git whitespace checks pass.

Raw local evidence is under ignored `artifacts/seiza-0442/`, including native
traces, source hashes, checkpoint comparison, test logs and diagnostic app
results. User settings and original images were unchanged. No installation,
merge or release was performed.
Large-image graphics, codec, fixture and package evidence is under ignored
`artifacts/large-mosaic-ui/`.
