# Shared image I/O and filter grouping

The development branch adopts Seiza 0.48.0's shared header and window readers
from upstream PRs [407](https://github.com/theatrus/seiza/pull/407) and
[408](https://github.com/theatrus/seiza/pull/408).

## Published dependencies

The app uses published crates.io releases, pinned centrally in the workspace
manifest: Seiza/C ABI 0.48.0, image-io 0.12.0, FITS 0.7.0, XISF 0.8.0,
stacking 0.55.0, stretch 0.3.2 and download 0.9.0. Parallax resolves to 0.19.6
and calibration remains at 0.11.0. No Seiza Git dependency remains.

Release [409](https://github.com/theatrus/seiza/pull/409) was published from
`350a5e79700e6997047adecd35c0939cbf9a4234`; its source tree is identical to the
initial review pin `723fb702eb01283ddf0343a7217f7a987f0d709a`. The native build
records the published C ABI's Cargo VCS revision. This adoption does not cut a
Windows release. Core's stacking/stretch output versions are unchanged.

Published C ABI tests refer to sibling calibration fixtures omitted from their
archive. Staging uses the calibration crate from the same source revision, or,
when that unchanged leaf came from an earlier release, extracts only fixtures
from the exact calibration archive locked by the C ABI. The archive SHA-256,
version and registry source are checked before extraction; links and escaping
paths are refused. Cargo's registry sources remain untouched. Regression checks
cover clean/repeated staging, mismatched versions/checksums, modified unpacked
fixtures and archive path traversal.

## Header reads and filters

`ImageSourceService` calls `seiza_read_image_header_json`. A bounded cache checks
length, modification time and Windows file identity on every lookup, verifies
the source again after reading, and never caches failures. Header loading and
browser filter discovery run off the UI thread. Cancellation and collection
identity checks prevent an old browser request from regrouping a new collection.

Color processing reads `SEIZAPCC` and `SEIZATRF` from the same header instead of
two host-specific C exports. Their Rust implementations and the platform crate's
direct XISF dependency are removed. FITS HISTORY and COMMENT entries are shown
in Inspector's searchable header list. The shared reader also carries supported
PixInsight XISF astrometric solutions as WCS cards.

Browser groups, fixed/expanded batch outputs and live stacking prefer `FILTER`
metadata; missing or blank metadata falls back to the existing filename aliases.
Batch setup retains filter names from its native input-role probes, including
sky-field/filter grouping. Browser grouping remains a pure model supplied with
metadata; it initially uses filename fallbacks while header reads finish.
Pickers share `ImageFileTypes.AstronomyExtensions`. Explicit file eligibility is
kept separate from the core reader's FITS fallback for unknown extensions.

## Source pixel inspection

Open Inspector with Ctrl+Alt+I and enable **Show source values under pointer**.
Move over the image to read a source pixel. Coordinates are zero-based, in
stored row order. FITS scaling and integer blanks are handled by core; XISF
values remain in their stored units. RGB values retain channel order. The
service also supports rectangular windows and an explicit normalized full scale.

Reads are debounced and discard superseded results. No side may exceed 1024
pixels; the pointer reads only one pixel. Unsupported compressed, complex or
CIELab XISF reads report an error without falling back to a full-image decode.
The block checksum is not verified by a window read. The existing stretch
symmetry picker continues to sample displayed luminance.

Explorer's byte-stream decoder and its bounded memory/layout validation remain
in place. The new path-based APIs are not replacements for those checks.

## Mac #92 fixes and Windows coverage

The shared calibration coordinator and Mac #92 execution behavior are retained,
including review-key reuse for Stop/Keep/Exclude, draft preferences and stale
source checks. A Windows regression verifies that recursive native discovery
skips junction directories, explicitly supplied files through a junction resolve
to the physical sources, cached masters are reused, and source/output protection
recognizes those aliases. No separate host calibration coordinator is added.

The new image-I/O regressions exercise header cache invalidation, commentary and
color markers, conflicting filename/header filters across browser/batch/live,
physical unsigned FITS samples, clipped/empty windows, cancellation, normalized
XISF scaling, PixInsight WCS, RGB plane layout and refused compressed XISF.
A Windows sparse-file regression reads four far-edge pixels of a
40,000 × 40,000 float FITS image (6.4 GB logical data) without loading its image.

Interactive validation uses an isolated probe built from the production controls.
At adoption time the desktop helper returned `GetCursorPos: Access is denied`
before launching that probe, so interactive layout verification remains pending.
Local build/test logs and synthetic fixtures are under ignored
`artifacts/imageio-048-*` paths.

Recorded validation: Release solution and unsigned MSI builds pass with zero
warnings/errors; 868 managed tests pass, 96 Windows graphics/codec tests pass
with four optional-input skips, and 1,019 native tests pass with six existing
ignored cases. Rust formatting, warnings-denied Clippy and the fresh archive
staging regression pass. Arc A770M traces confirm actual Vulkan and D3D12 compute
on the existing large calibrated-session regression, including fixed/expanded
integration and replay against the CPU background comparison.
