# Native ABI

`seiza-cabi.dll` deliberately exposes a small C ABI rather than Rust symbols.
The same API is statically linked by the macOS app and dynamically loaded by
the Windows app. Its implementation lives in the upstream Seiza workspace;
this repository resolves the published crates.io crate through `Cargo.lock`
rather than maintaining a Windows fork or using a Git dependency.

Rules:

- Opaque image handles own their pixel and metadata buffers.
- A handle remains alive until the host calls its matching free function.
- Owned UTF-8 strings have an explicit Rust free function.
- Every exported operation catches panics and returns a host-readable error.
- High-volume pixels use a contiguous BGRA8 buffer suitable for direct Win2D upload; evolving records use JSON.
- FITS and XISF processing use the shared `seiza_rendered_image_open_with_stretch_config` JSON contract so ordered stretch stages, color strategy, background correction, light deconvolution, render `sample_domain`, and interactive-preview intent stay platform-neutral.
- Catalog status is returned as owned JSON; catalog setup runs synchronously on a worker thread and reports borrowed progress JSON through a callback.
- Rust owns manifest resolution, download caching, full SHA-256 verification, and atomic catalog installation.
- A live stacker handle owns registration, calibrated accumulation,
  frame-to-frame normalization, rejection state, and its accepted/rejected
  counters. Each pushed frame returns an owned JSON disposition; finishing
  consumes the live handle and returns a snapshot that can write an unstretched
  32-bit FITS file.
- Cancellation is cooperative at the shell boundary between frame pushes.
  Live stacker, snapshot, disposition, and error allocations are always freed
  by their matching ABI function.
- Calibration master reports are versioned JSON. Schema 1 is accepted only
  when every requested input appears in the accepted `inputs` array. Schema 2
  additionally carries `requestedFrames` and `skippedInputs`; Windows verifies
  that accepted and skipped paths are a disjoint, exact partition, that every
  skip has a reason, and that enough accepted frames remain. An ambiguous
  legacy partial report is discarded rather than guessed or cached. Bundled
  Seiza 0.18.4 emits the complete schema-2 partition.
- Calibration preparation calls the native signature matchers for sensor
  settings, flat optics, and dark exposure/temperature using Seiza's exported
  default tolerances. Native mismatch descriptions name the differing readings
  and applicable tolerance in preparation warnings. Live pushes rely on the
  stacker's authoritative native calibration validation so scalable dark policy
  and restored calibration state are not duplicated in C#. A chosen legacy
  master may omit an old metadata field, but two recorded conflicting readings
  still reject the affected light.
- ABI additions are backward-compatible. Breaking changes require an ABI version bump.

The Windows interop layer uses source-generated `LibraryImport` declarations,
an unmanaged progress trampoline, and `SafeHandle` wrappers. Raw ownership is
contained in the service boundary and is never exposed to view models or
controls.

Interactive edits are debounced in the WinUI shell and rendered at a maximum
2,048-pixel dimension. Save submits the same processing stack at full
resolution; no per-pixel processing or stretch math is duplicated in C#.
For a physical live-stack mean, the native display pipeline is ordered as
background correction, optional light Richardson-Lucy deconvolution, optional
linked robust `sample_domain` mapping into display-working units, and then the
ordered display-stretch stack. A linked mapping resolves one shared range for
mono or RGB rather than independently normalizing color channels. This
presentation step is distinct from the stacker's frame-to-frame normalization:
the accumulator, SNR measurements, resumable checkpoints, snapshots, and
written 32-bit FITS samples all remain physical linear `f32` data. The
`background` JSON object carries subtract/divide mode, strength, and an
automatic, fixed-degree polynomial, or radial-basis model. Automatic selection
uses held-out samples and excludes the flexible radial-basis candidate unless
the user opts in. Recipes written before Seiza 0.14 remain compatible and map
to the historical degree-2 polynomial model. The
`deconvolution` JSON object carries `psf_fwhm_pixels`, `iterations`, `amount`,
`noise_fraction`, and `max_correction`.

Windows live previews request the physical mapping explicitly:

```json
{
  "sample_domain": {
    "type": "physical-linear",
    "normalization": {
      "type": "robust-percentile",
      "black_percentile": 0.001,
      "white_percentile": 0.999,
      "max_analysis_samples": 200000
    }
  },
  "stretch": [
    {
      "model": {
        "type": "auto-mtf",
        "target_median": 0.2,
        "shadows_clip": -2.8
      },
      "color_strategy": "unlinked",
      "max_analysis_samples": 200000
    }
  ]
}
```

The contract also accepts `{ "type": "unit-linear" }` and a physical
normalization of `{ "type": "explicit-range", "black": ..., "white": ... }`.
Omitting `sample_domain` preserves the historical unit-linear render behavior
for existing file-rendering clients.

`scripts/build-rust.ps1` resolves the crates.io package with `cargo metadata`,
then builds that registry package from the application workspace so the root
`Cargo.lock` controls compatible transitive patches. Its test mode first runs
the published crate's own locked tests and then builds the workspace-locked DLL.
The script reads Cargo's packaged `.cargo_vcs_info.json` and emits
`seiza-build-info.json` beside the DLL. The app packages that file so About can
show the exact native crate version and 40-character source commit without a
Git dependency.
