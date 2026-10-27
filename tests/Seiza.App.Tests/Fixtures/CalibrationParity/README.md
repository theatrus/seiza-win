# Calibration parity references

These are synthetic, pinned upstream captures, not expected outputs generated
by the Windows implementation. No user catalogs or astronomical source images
were used to create them.

| File | Source | Git blob |
| --- | --- | --- |
| `psf-guard.json` | [Mac #92](https://github.com/theatrus/seiza-mac/pull/92), `Tests/Fixtures/CalibrationParity/psf-guard.json` at `d574764e6713f7ab3c0826452991504d6c6f6ca3` | `7b8b6afccc237cbf604c0921dc56a6edcb473e4e` |
| `flat-groups-request.json` | [Seiza #385](https://github.com/theatrus/seiza/pull/385), `seiza-calibration/tests/fixtures/flat-groups/request.json` at `41caa22ae49b3a512f65c1947a17d0258231da29` | `e0ee74aa54a96c230bf3a1bc6bb1b358e703291b` |
| `flat-groups-expected.json` | Same commit, `seiza-calibration/tests/fixtures/flat-groups/expected.json` | `28a2522d515be795560a7de1cbf1857a3ff8849c` |

Each copy's Git blob hash was checked against its pinned upstream file. The
Mac capture uses PSF Guard `79f5e31d53b8371e8fcd0b22a9b69692412cd8b4`,
calibration 0.8.1 and stacking 0.51.2. Its
[capture procedure](https://github.com/theatrus/seiza-mac/blob/d574764e6713f7ab3c0826452991504d6c6f6ca3/Tests/Fixtures/CalibrationParity/README.md)
and harness remain upstream; Windows does not copy the matching algorithms.

## What Windows currently verifies

- Native bias/dark screening reproduces the captured grades and finding codes
  for the fixture's explicitly partitioned kind/gain peers. Stored measurements
  round-trip without rescaling or dropping their algorithm version/hot counts.
- Existing flat screening reproduces the same capture's flat grades/findings.
- Separate synthetic FITS boundary tests prove physical-ADU measurement, fixed
  and expanded session switching, recorded/measured rotation, hard gain gates,
  checkpoint identity and per-pixel replay. Expanded planning is deliberately
  **uncalibrated** to isolate integration/replay. This is not correct production
  multi-session reference planning.
- The optional #385 test compares measured families, boundaries, events and
  before/undetermined/after placements against the captured shared-core fixture.
  Only the test converts Rust record field names to C camelCase; string values,
  opaque identifiers and enums are unchanged. Production forwards C JSON as-is.
  This test is reported **skipped** when the native export is absent.

The capture also retains selections, override transitions, recipes and gates
for the future planner. The current Windows tests do not claim those decisions
as implemented. Do not regenerate expectations from local results or copy
fixture limits into production defaults. Updating these snapshots requires a
reviewed upstream capture/contract change.

See [the Windows boundary and implementation plan](../../../../docs/STACKING-CALIBRATION-FOUNDATIONS.md).
