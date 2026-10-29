# Per-light calibration foundations

This document records the first Windows foundation slice. It is superseded by
[the Mac #92 parity implementation](MAC-PR-92-PARITY.md), which consumes the
published Seiza/C ABI **0.47.0**, calibration **0.11.0** and stacking **0.54.0**.
The graded setup is now connected to the shared native file coordinator and both
canvas paths. Windows 0.8.8 remains the last released application; this branch
has not made another product release.

The original foundation captures remain under
[Fixtures/CalibrationParity](../tests/Seiza.App.Tests/Fixtures/CalibrationParity/README.md).
They pin PSF Guard `79f5e31d53b8371e8fcd0b22a9b69692412cd8b4`, Mac #92 at
`d574764e6713f7ab3c0826452991504d6c6f6ca3`, and Seiza #385 at
`41caa22ae49b3a512f65c1947a17d0258231da29`. Their bytes have not been rewritten
to manufacture parity with a newer core.

## Foundation evidence retained

- Typed physical-unit bias/dark measurements and reusable native screening.
- Captured PSF Guard bias/dark/flat grades and finding codes.
- Exact measured-family/change/uncertainty fixture through the C JSON binding.
- Raw C ABI fixed/expanded A→B→A integration, measured rotation, full gate reset,
  checkpoint identity, historical-master protection and per-pixel replay.
- Failed geometry replacement preserves the previous calibration set.
- Prepared-only arrays refuse manual calibration/gate setters.

The original expanded boundary test intentionally plans without calibration;
it remains an integration/replay boundary test. The newer application-driver
regressions in `CalibrationLibraryTests` additionally use session-aware
calibrated raw-file planning, corrected flats, fitted pedestals, normalized
XISF and shared-reference candidate gates. These are distinct claims.

## Shared boundary

Windows owns controls, local draft preferences, source selection, jobs and cache
location/lifetime. Seiza owns measurement/evidence caches, grading, family and
cohort matching, settings validation, manual-choice resolution, master recipes,
retries, pedestal acceptance, corrections and atomic master publication. No
matching/coordinator policy is copied into C# or the Windows Rust platform crate.

Fixed-canvas master/gate replacement and its subsequent push share the Windows
handle's exclusive executor. Any failed setter aborts the push. Expanded stacks
receive all sessions and assignments before planning and let the native handle
retain their immutable history for integration and replay.
