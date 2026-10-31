# Mac #96: simpler stacking setup

The Windows development branch follows merged [Mac PR #96](https://github.com/theatrus/seiza-mac/pull/96),
reviewed at `b1e31749440e585302b5a5d4c91bfe11252e0992`. These UI changes are not
in the published Windows 0.9.1 installer. Core dependencies and product version
are unchanged: published Seiza/C ABI 0.48.0, calibration 0.11.0 and stacking 0.55.0.

## Start directly from Stack

1. Open a folder or TXT/CSV image list, then choose **File → Stack images…** or
   **Stack…** in the toolbar. Both commands reuse the current document's setup
   window. Raster-only collections cannot start an astronomy stack.
2. Keep **Fixed reference** for repeated exposures of one field, or choose
   **Expanded mosaic** for overlapping fields. All detected lights are selected.
   Mixed collections provide calibration files and start matching automatically.
3. Wait for matching to finish, then choose **Stack…** and select an output.
   Masters are built or reused automatically and the saved FITS result opens
   in the viewer. Complete matching needs no review approval or additional tab.

The first tab shows selected-light/output counts, matching progress, calibration
frame and matched-light counts, warnings, and **Review matches**. Source folders,
import notes, individual lights and sky panels start collapsed. Splitting controls
appear when multiple filters are detected; header filters still take precedence
over filename guesses.

## Detailed controls

| Location | Controls |
| --- | --- |
| Calibration | Existing masters or Calibration files; individual files/library folder; Automatic or All compatible frames independently for flats and darks; incomplete-light policy; Matching and grading. |
| Processing | Registration reference, shared alignment, integration settings, and Registration and memory limits, including the expanded-canvas ceiling. |
| Review | Calibration frames, Light matches, Master plan and Flat groups. More → Refresh from files performs a manual refresh. Done returns to Stack. |
| Matching failure | The error and Try again appear on Stack. Retry is absent during normal automatic matching. |

These are settings pages, not a sequence of required steps. Windows uses sentence
case for control labels. The first selected light remains the reference by default;
automatic selection remains off. Processing defaults and native matching policies
are preserved. Sky-panel solving is optional for expanded mosaics.

The default incomplete-light policy still stops. Keep remains explicit. Exclude
checks every output group has at least two remaining lights before opening the
output picker, while native execution retains its own checks. Edits remain local
until stacking starts; cancellation does not save preferences. Source protection,
master reuse, per-light sessions, replay and the live-acquisition workflow are
unchanged.

## Validation

- 875 managed tests passed, including readiness checks for complete matching,
  Stop/Keep/Exclude, separate outputs, and unreviewed/stale coverage.
- Release app publish and solution build succeeded; final build results are
  recorded in the pull request. The solution build has zero warnings/errors.
- Interactive validation and current screenshots are unavailable: the Windows
  computer-use helper returned `GetCursorPos: Access is denied (0x80070005)` on
  launch and retry. The published preview was then started as a development
  process and created its Seiza window without exiting. The helper also refused
  to bind that window, reporting an owner-identity mismatch on refresh/retry.
  This establishes startup only: no manual fixed/expanded stack or visual-layout
  pass is claimed. Older #92 screenshots describe the previous UI.

Local validation logs and the preview build are under ignored `artifacts/mac96-*`.
