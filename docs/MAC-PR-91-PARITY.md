# Windows parity with Mac PR #91

Compared against [Mac PR #91](https://github.com/theatrus/seiza-mac/pull/91)
at `74926a9ae542b3a1ade714644925d426f9944dfd`.
These behaviors ship in Windows 0.8.8. Native dependency pins are unchanged
from Windows 0.8.7; see the [release notes](releases/v0.8.8.md).

| Mac PR #91 behavior | Windows behavior |
| --- | --- |
| TXT/CSV image-list import | Same path-column aliases, strict quoting, UTF-8/UTF-16 BOM handling, relative paths, preambles and inclusion regardless of Approved. Invalid encodings fail rather than replacing path characters. |
| Open and drag/drop integration | Regular Open, welcome action, toolbar action, dropped list files and command-line/activation relay accept lists. Folder discovery still ignores TXT/CSV files and nested lists are not expanded. |
| Keyboard entry point | Ctrl+Alt+O opens lists; existing Ctrl+Shift+O still opens folders. |
| Source-directory groups on import | Every list starts in Folder Path mode, independent of the remembered grouping. Initial selection/title follows the first displayed image. The remembered preference is not overwritten. |
| Original list order under None | A retained source-order snapshot restores original order after any regrouping. Ordinary folder/image views keep their existing natural sorting. |
| Skipped-entry source locations | Expand Skipped entries for the complete source list path, physical record-start line, listed value and failure reason. Blank lines and quoted multiline CSV fields do not shift the reported line. |
| Alias deduplication | Windows saved-target `.lnk` support and file-identity deduplication cover shortcuts, hard links and symlink/junction aliases. Shortcuts are read without Shell Resolve or updates. |
| Atomic malformed replacement | Parsing and empty-result validation complete before the current collection changes. A malformed or empty replacement leaves the prior collection and image in place. |
| Sandbox access requests | Not applicable to this unpackaged Windows app. Read access is checked for each listed image; Windows permission failures are reported as skipped entries rather than opening Mac sandbox grant dialogs. |
| Primary canvas choice | Stacking mode and the expanded-canvas ceiling appear before input selection. Direct expanded stacking does not require sky-panel analysis or solver catalogs. |
| Optional sky-panel analysis | Collapsed by default. Analysis is a selection aid, keeps the explicit canvas mode, overlap and alignment choices, and does not silently force expanded mode. |
| Expanded/shared-reference compatibility | Choosing a new shared fixed alignment is disabled in expanded mode. An existing active choice remains clearable; native validation still rejects incompatible active settings. |
| More than 512 lights | Removed service and native-adapter admission caps; the two-light minimum, native row bounds, canvas limits and per-output validation remain. |
| Responsive footprint cancellation | At most eight rows and about 4096 comparisons per batch, or one large row. Cancellation checks cover footprint preparation, batch boundaries and grouping. The complete matrix remains quadratic. |
| Calibration scope | One compatible calibration set per output, across that output's eligible raw lights. Panels/folders within an output do not receive independent flat sessions. |

Windows retains its existing multi-document convention: ordinary image opens
create independent document windows, while each list opens as one collection.
No `.csv`/`.txt` default file association is installed.

## Automated validation

- Release solution build: zero warnings/errors.
- Full native suites, including 24 platform tests; Rust formatting and clippy
  with warnings denied passed.
- 92 focused regressions passed, including a complete 513-light native footprint
  matrix grouped into two fields without truncation, service admission,
  cancellation, list order, BOMs, strict quoting and encoding validation, line
  details, command-line admission and aliases.
- Full managed suite: 831 tests passed.
- Windows graphics/codec suite: 96 passed; four optional-input skips.
- Self-contained unsigned MSI build and static Finish-launch/Shell-notification
  sequence checks passed. The installed application was not replaced for QA.

## Interactive validation

- A SubframeSelector-style list opened through the production activation route
  in an isolated startup probe. With a saved None preference, it displayed
  source-folder groups and selected `z.fits`, the first image in grouped order.
  The persisted preference remained None. Switching to None restored CSV order
  (`b.fits`, `a.fits`, `z.fits`) without changing the selected image.
- Skipped-entry details showed the source list and physical lines 8 and 9 after
  a quoted multiline CSV field. The unsupported nested CSV was not expanded.
  Rechecked with the final strict parser. A malformed list showed its source
  path and record-start line in the error window; the earlier document stayed open.
- Regular Open imported a 513-light CSV as one collection. Expanded mode was
  available at the top, with Choose output and stack enabled before panel
  analysis. Opting into analysis left Solve and group selected lights enabled
  for all selections. These are synthetic repeated fixtures; this is an
  admission/layout check, not a 513-exposure integration benchmark.
- Two copies of an M31 FITS image were solved and grouped successfully. Applying
  the analysis retained Fixed reference. With shared alignment active, changing
  to Expanded disabled Choose image but retained Clear shared alignment image;
  clearing restored stack readiness.
- Ctrl+Alt+O opened the TXT/CSV picker. QA restored the original browser preference
  afterward. No original image or installed application was modified.

The startup probe bypassed local AppInstance registration only; the application,
controls, collection routing and native DLLs were the production code.

![Source list and physical-line diagnostics in the native browser](images/csv-list-line-details.png)

![Expanded mode before optional analysis in the native stacking dialog](images/mosaic-stacking/stacking-mode-first.png)
