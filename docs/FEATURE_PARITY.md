# Feature parity

This is the maintained product-parity checklist between Seiza for macOS and
Seiza for Windows. It tracks user-visible behavior, shared-core readiness, and
Windows-specific integration separately so a feature is not marked complete
merely because its Rust implementation exists.

## Baseline

- macOS reference: Seiza 0.2.0, `main` at
  [`edeefd5`](https://github.com/theatrus/seiza-mac/commit/edeefd53d578b764eabc6af555dbed46e6e7ed36)
- Windows reference: initial native viewer through `85aeb51`
- Last audited: 2026-07-20

Update this baseline and the affected rows whenever the macOS app gains a
feature or changes an interaction. A Windows feature is **Complete** only after
the WinUI surface, accessibility behavior, error states, and a real runtime
test exist.

| Status | Meaning |
| --- | --- |
| **Complete** | Available and runtime-tested in the Windows app. |
| **Partial** | Some Windows behavior exists, but parity or validation is incomplete. |
| **Core ready** | Rust/C ABI data exists; C# models or WinUI presentation are missing. |
| **Planned** | No usable Windows implementation yet. |
| **Deferred** | Intentionally outside the first parity release. |

## Viewer and navigation

| Capability | macOS 0.2.0 | Windows | Windows gap / acceptance criterion |
| --- | --- | --- | --- |
| FITS, JPEG, PNG, and TIFF opening | Available | **Complete** | Keep the supported-extension lists synchronized. |
| File and folder picker | Available | **Complete** | — |
| Drop file/folder into the active viewer | Available | **Complete** | — |
| Mixed-format folder collection and natural ordering | Available | **Complete** | — |
| Previous/next and arrow-key navigation | Available | **Complete** | — |
| Replace the active viewer contents when opening another item | Available | **Complete** | — |
| Multiple document windows and file activation routing | Available | **Planned** | One process, one window per document collection, activation redirected into the running app. |
| Thumbnail drawer | Available | **Planned** | Virtualized WinUI thumbnail rail with selection and accessibility names. |
| Memory/disk thumbnail cache and adjacent prefetch | Available | **Planned** | Cache keys include source identity and RGB stretch mode; visible work wins over prefetch. |
| Cached preview while full resolution loads | Available | **Planned** | Never blank an already available preview during a full render. |
| Mono FITS autostretch | Available | **Complete** | Runtime-tested against telescope FITS data. |
| Planar RGB and Bayer/OSC rendering | Available | **Partial** | Core path exists; add representative RGB and Bayer fixtures and visual QA. |
| Auto, Linked Auto, and Linear RGB modes | Available | **Core ready** | Expose the existing ABI mode in the toolbar and include it in cache/render identity. |
| Fit, pan, wheel zoom, and toolbar zoom | Available | **Complete** | — |
| Pointer-anchored pinch/touch zoom | Available | **Planned** | Add native manipulation handling without rerendering pixels. |
| Image dimensions, format, and color-kind status | Available | **Complete** | — |
| Image statistics and FITS header inspector | Available | **Core ready** | Metadata is already decoded; add a native inspector pane with searchable/copyable headers. |
| Detailed loading and native error states | Available | **Complete** | — |

## Catalog settings and managed data

The macOS Settings flow is now part of first-release parity, not a future
enhancement. Previewing remains catalog-free; catalog I/O starts only for
status/setup or an explicitly requested solve.

| Capability | macOS 0.2.0 | Windows | Windows gap / acceptance criterion |
| --- | --- | --- | --- |
| Use Seiza's default catalog directory | Available | **Planned** | Resolve the same default through shared Rust. |
| Choose and persist a custom catalog directory | Available | **Planned** | Use a WinUI folder picker and persist a FutureAccessList token plus display path. |
| Per-component status | Available | **Planned** | Report star catalog, blind index, objects, transients, and minor bodies. |
| Separate solve-ready and overlay-ready status | Available | **Planned** | Show clear Ready, Setup required, and Incomplete states. |
| Setup presets | Available | **Planned** | Standard blind (recommended), Deepest blind, and All. |
| Download and install | Available | **Planned** | Call shared Rust setup; do not reproduce manifest/download logic in C#. |
| Verify or repair an existing install | Available | **Planned** | Safe to retry and reuse already verified files. |
| Structured setup progress | Available | **Planned** | Support preparing, manifest, downloading, verifying, installing, and complete phases. |
| File and byte progress | Available | **Planned** | Show file name/count, downloaded bytes, total bytes, unpacked/written bytes, and percentage. |
| Full SHA-256 verification feedback | Available | **Planned** | Explain that verification reads large files completely and may take several minutes. |
| Setup continues after Settings closes | Available | **Planned** | App-scoped controller/service owns the operation, not the page. |
| Solve error links to Catalog Settings | Available | **Planned** | Missing data should offer a direct Settings action. |
| Catalog bundle update discovery and selective datasets | Planned | **Deferred** | Track after first-release catalog parity. |

The current Windows C ABI predates the macOS catalog setup additions. The next
sync must add `seiza_catalog_status_json`, `seiza_catalog_setup`, the three
preset values, and the progress callback contract while retaining Windows BGRA
render output.

## Plate solving

| Capability | macOS 0.2.0 | Windows | Windows gap / acceptance criterion |
| --- | --- | --- | --- |
| Explicit local blind solve | Available | **Core ready** | `seiza_solve_image_json` already exists; add safe C# bindings and a Solve command. |
| Background solve state | Available | **Planned** | Idle, solving, solved, failed; keep image navigation responsive. |
| Default solve range and SIP order | Available | **Core ready** | Preserve the macOS defaults initially; expose advanced options later. |
| Solution quality summary | Available | **Planned** | Center RA/Dec, scale, matched/detected stars, RMS, and elapsed time. |
| WCS/SIP result model | Available | **Core ready** | Decode the existing versioned JSON into C# records. |
| Solve only on explicit request | Available | **Planned** | No catalog load or solve during ordinary viewing or thumbnail generation. |
| Stale-result protection during navigation | Available | **Planned** | A solve result may only attach to the exact source/render generation that requested it. |
| Cooperative cancellation and in-process catalog/index cache | Planned | **Deferred** | Add after the first correct end-to-end solve. |
| Hinted solve before blind fallback | Planned | **Deferred** | Use trustworthy FITS header hints when available. |

## Solver overlays

The Rust solve response already carries most overlay data. **Core ready** below
means the Windows shell still needs C# models, layer state, and Win2D drawing.

| Layer or behavior | macOS 0.2.0 | Windows | Windows gap / acceptance criterion |
| --- | --- | --- | --- |
| Overlay availability, unavailable reasons, and counts | Available | **Core ready** | Surface disabled layers and reasons instead of silently hiding them. |
| Named stars | Available | **Core ready** | Catalog palette, markers, and labels. |
| Field stars with magnitude | Available | **Core ready** | Magnitude-aware restrained markers. |
| Deep-sky objects | Available | **Core ready** | Markers, catalog color, labels, and independent filters. |
| Individual DSO catalogs | Available | **Core ready** | Messier, NGC, IC, Sharpless/vdB, LBN, Barnard, UGC, PGC, and Other. |
| Detailed OpenNGC contours | Available | **Core ready** | Draw projected contours; fall back to catalog ellipses. |
| Independent object labels and outlines | Available | **Planned** | Separate toggles and collision-aware label placement. |
| Current and historical transients | Available | **Core ready** | Independent visibility and acquisition-time filtering. |
| Comets and asteroids | Available | **Core ready** | Acquisition-time positions, motion direction, and tails. |
| Detected-star diagnostics | Available | **Core ready** | Diagnostic layer off by default. |
| RA/Dec coordinate grid and labels | Available | **Planned** | Derive from solved WCS; keep grid geometry in shared Rust where practical. |
| Field-center marker | Available | **Planned** | Draw in the common solved-image coordinate space. |
| Hide all overlays | Available | **Planned** | One accessible action without losing individual preferences. |
| Overlay transforms during pan/zoom | Available | **Planned** | Draw from image coordinates in the same Win2D transform as the bitmap. |
| Catalog-aware palette and restrained styling | Available | **Planned** | Match semantic colors and readable line/label weights in light and dark themes. |
| Satellite overlays | Planned | **Deferred** | Requires time span, observer, element epoch, and explicit provenance. |

## Windows platform integration

| Capability | macOS analogue | Windows | Windows gap / acceptance criterion |
| --- | --- | --- | --- |
| FITS file registration and document icon | Finder association/icon | **Planned** | MSIX `.fits`, `.fit`, and `.fts` associations with a dedicated icon. |
| Stretched system preview | Quick Look extension | **Planned** | Explorer Preview Pane handler in a separately hosted native component. |
| Content thumbnails on file icons | Finder thumbnail provider (planned) | **Planned** | Explorer thumbnail provider, isolated from .NET, catalogs, and solving. |
| Signed distributable | Signed/notarized universal DMG | **Planned** | Signed self-contained x64 MSIX; ARM64 follows parity. |
| Release automation | macOS release workflows | **Partial** | CI builds Debug; add signed packaging, artifacts, tags, and protected release environment. |
| Native accessibility | SwiftUI/AppKit accessibility | **Partial** | Core controls are named; add automated coverage for inspector, Settings, and overlay controls. |

## Shared future roadmap

These remain tracked even though they are not macOS 0.2.0 release features:

- pixel loupe, histogram, and black/midtone controls;
- star-detection overlays with HFR/FWHM measurements;
- compass, scale bar, and WCS cursor readout;
- solve sidecar provenance and FITS WCS-card export;
- sequence comparison, blink/difference views, and registration;
- multi-extension FITS image-HDU navigation;
- lazy FITS cube slices with neighboring-slice preloading;
- updater, crash reporting, and a repeatable performance corpus.

## Delivery order

1. **Catalog Settings vertical slice** — sync the new Rust ABI; add status,
   location, presets, download/repair, durable progress, and tests.
2. **Solve vertical slice** — safe C# solve bindings, solve state, stale-result
   protection, solution summary, and Settings remediation.
3. **Overlay scene** — common coordinate transform, layer menu, grid/center,
   stars, DSOs and catalog filters, contours, transients, and minor bodies.
4. **Inspection parity** — metadata inspector, RGB modes, thumbnails/cache, and
   preview-while-loading.
5. **Windows integration** — multi-window activation, file associations,
   Explorer components, signed packaging, and release automation.
