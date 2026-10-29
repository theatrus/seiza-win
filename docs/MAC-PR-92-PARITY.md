# Mac #92 parity: graded calibration and flat inspection

Reference: merged [Mac #92](https://github.com/theatrus/seiza-mac/pull/92), head
`3c09a71245505890352bd364597831c61d9fff62` (merge `d557d401`). Windows uses
published Seiza/C ABI **0.47.0**, calibration **0.11.0**, stacking **0.54.0** and
parallax **0.19.5** through the application lockfile. The C ABI and calibration
archives identify source revision `9a29be7534c251cfbfdff7b10a09281a0a5c3a9c`.
This includes the shared coordinator from [Seiza #406](https://github.com/theatrus/seiza/pull/406)
and the policy/session planner extracted in [#385](https://github.com/theatrus/seiza/pull/385).
Mac's review pin `d11b570` preceded this published merge; Windows has no Git
dependency or private calibration coordinator. Product release/version 0.8.8
has not been changed by this work.

## Setup

1. Open a folder tree or image collection and choose **Stack**. Native FITS/XISF
   metadata routes only lights into selection; bias, dark, dark-flat, flat and
   their masters automatically become provided calibration files. Calibration
   files remain browsable. Unknown types are offered as lights with an explicit
   note; unreadable files and other masters are set aside with their reasons.
2. **Inputs** selects lights, fixed/expanded canvas, output grouping and references.
   Optional sky-panel analysis remains independent of calibration sessions.
3. **Calibration** offers existing masters or **Graded calibration files or
   library**. Add individual files, a recursive folder, or both. Remove a folder
   or individual included files without deleting their sources.
4. Flats and darks independently choose **Group and match automatically** or
   **Use all as one set**. One-set retains native physical/quality gates and
   eligible inputs across nights. It cannot retry an unrelated flat session.
   **Incomplete lights** chooses Stop and review, Keep with available calibration,
   or Exclude from stack. Changing that execution choice preserves reviewed
   matches and ongoing analysis.
5. The expandable settings panel uses Seiza's field schema for global/per-rig
   matching, dark policies, flat grouping and grade thresholds. Setting-key
   strings and enum values pass unchanged. Flat star masking and the native
   stability/trim/alternate/retention controls are included.
6. Analysis starts after source/settings/choice edits. Debouncing cancels stale
   requests while numeric controls remain editable. The **Review** tab contains
   Calibration frames, Light matches, Master plan and Flat groups. Quality
   exclusions/use-anyway are source-stamped; split/join and per-light group or
   before/after choices use the native commands and explanations. Uncertain
   evidence remains incomplete.
7. Choose an output and start. Only then commit this setup's preferences. Cancel,
   window close and output-picker cancellation leave previous defaults alone.
   Execute revalidates the review before building masters; stale settings/files
   cannot silently produce an uncalibrated stack. Preparation warnings have one
   attached confirmation dialog.

## Execution and ownership

`CalibrationLibraryService` calls `seiza_calibration_files_json` directly on a
worker thread with the native cancellation signal and progress callback. Native
code owns discovery/identities/locks, evidence/refusal caches, grading/grouping,
cohort selection, dependency recipes, stable master reuse, retries, pedestal
acceptance and corrected-master publication. C# retains the native records and
owns local draft preferences, cache location and the UI.

Fixed reference candidates each open with their own calibration and supported
application gates. Subsequent tolerance â†’ rotation â†’ master replacement and
push occur under one exclusive stack-handle executor; a failed setter aborts
the push. Expanded planning calls `open_fits_expanding_sessions` with original
physical-sample inputs, sessions and matching assignments before star detection
or footprint planning. It returns an empty accumulator: every selected light,
including the anchor, is pushed exactly once. Native history retains assignments
for contexts and transient-removal replay, including Aâ†’Bâ†’A.

Declared-unit XISF sources are staged by core in physical ADU with original
metadata and source protection. Accepted flat-only pedestals are materialized
as native bias masters so fixed and expanded C openers use the same correction.
Both paths receive corrected flats. Source stamps are verified before use and
replay; output protection includes the whole reviewed plan and historical
masters, including files belonging to other output groups.

The coordinator's immutable cache is separate from the older host master cache;
the latter's age/size pruning cannot delete this plan's historical masters.
Automatic eviction is absent here, as in Mac #92. Windows learned-defect
estimation currently requires disabling graded per-light calibration, with an
explicit setup error instead of measuring against the wrong single master set.

## Flat inspection

Open an image identified as a flat by native headers. **Inspect Flat** is a
contextual toolbar action and appears first in Inspector; **Ctrl+Alt+I** toggles
Inspector independently of toolbar overflow. Both entry points share per-image
measurement options. Readings always come from the linear source before viewer
adjustments. Saturation is in source units (typically 65535 ADU or 1 for declared
normalized data); optional focal ratio, binned pixel size and obstruction supply
dust-distance estimates.

The sidebar displays native raw median/channel levels and saturation, clipping,
response noise, detection depth, default single-frame grade/findings, a coarse
normalized illumination map, dust locations/shapes/radii/depth/distances and
vignetting. Processed/master flats have measurements without a raw-exposure
grade. Changes invalidate displayed readings. Single-frame inspection does not
claim set stability or association with lights. Individual fields and map cells
have accessible labels; long details scroll without expanding the window.

## Validation boundary

The managed application-driver tests run the actual published Windows DLL,
including native analyze â†’ execute â†’ verify and actual output writing. They
cover Aâ†’Bâ†’A on fixed/expanded canvases and replay, corrected flats, materialized
pedestals compared against the direct native fit, normalized XISF, per-rig
gates, candidate-specific shared-reference fallback, missing/historical source
protection, incomplete policies/review-key reuse, recursive mixed-folder routing,
provided-only sources, master reuse and native cache-lock cancellation. Frozen
PSF Guard grade/group fixtures remain unchanged; the previously skipped grouping
binding fixture now executes with 0.47.0.

The large hardware regression uses 1056Ã—1024 lights, exceeding the native GPU
dispatch threshold, and compares calibrated output/background with CPU runs.
Arc A770M Vulkan and D3D12 traces confirm device computation, fixed and expanded
planning/integration/replay, and the 64 MiB planner budget. This is a correctness
smoke test, not a real-observation speed benchmark or a repeat of the earlier
full dedicated/iGPU memory-cap matrix. A planner budget is not a hard driver
allocation limit.

The published C ABI archive's tests reference sibling fixture files omitted from
that archive. `build-rust.ps1 -Test` stages its unchanged source and the published
calibration archive's fixtures under `target/published-tests`, requires matching
Cargo VCS revisions, and adds only an empty workspace boundary to the copied
test manifest. Cargo's registry cache is untouched; the shipped DLL is rebuilt
from the application's registry-only locked dependency graph afterward.

Live acquisition, the full PSF Guard catalog/night/light-check UI, validity
editing and automatic cache eviction remain outside Mac #92 and this parity
change. Source identities are metadata stamps rather than pixel hashes.

## Recorded Windows result

- Release solution build: zero warnings/errors.
- Managed tests: **861 passed, zero skips/failures**.
- Windows graphics/codec tests: **96 passed, four optional-input skips**.
- Published native/local suites through `build-rust.ps1 -Test`: **1,017 passed,
  six existing ignored cases**, zero failures. Includes C ABI's 172 cases and
  stacking's 66 output-version cases.
- Rust formatting and all-target workspace Clippy with warnings denied pass.
- Arc A770M: the large calibrated-session regression passes with actual device
  compute on Vulkan and D3D12; fixed and expanded outputs/replay agree with the
  CPU background oracle within 0.05 ADU. These are synthetic correctness runs.
- Interactive checks use an isolated startup probe with production App/controls,
  resources and native DLLs, without replacing the installed application. Mixed
  collection routing reports two lights/six calibration files. The tabbed setup,
  compact included-file disclosure, native settings and four review sections
  render in the resizable window. Changing flat rotation 2→12 triggers analysis
  while retaining the editor/value; cancel leaves no saved preference file.
  The Inspector shortcut works with toolbar overflow, and native flat inspection
  shows the measured dust/illumination sidebar. A startup CanvasControl resource
  race encountered with tiny images was fixed by creating display bitmaps on the
  shared device, then clean startup was rechecked.

Local logs/screenshots and the probe live under ignored `artifacts/mac92-parity`.
The probe changes only startup/identity so the registered installed app cannot
intercept the validation launch. It is not an installer or a release artifact.
