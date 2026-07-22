# Feature parity

This is the maintained product-parity checklist between Seiza for macOS and
Seiza for Windows. It tracks user-visible behavior, shared-core readiness, and
Windows-specific integration separately so a feature is not marked complete
merely because its Rust implementation exists.

## Baseline

- macOS reference: `main` at
  [`5b110ed`](https://github.com/theatrus/seiza-mac/commit/5b110edd813af485d18810d48173d6ec3dcc303b)
- Seiza core reference: `main` at
  [`32a8dbf`](https://github.com/theatrus/seiza/commit/32a8dbf62dda60e5ae2a1f3743c5d21976461f83)
- Windows reference: current stretch-controls branch
- Last audited: 2026-07-21

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

| Capability | macOS current | Windows | Windows gap / acceptance criterion |
| --- | --- | --- | --- |
| FITS, JPEG, PNG, and TIFF opening | Available | **Complete** | Keep the supported-extension lists synchronized. |
| File and folder picker | Available | **Complete** | — |
| Drop file/folder into the active viewer | Available | **Complete** | — |
| Mixed-format folder collection and natural ordering | Available | **Complete** | — |
| Previous/next and arrow-key navigation | Available | **Complete** | — |
| Replace the active viewer contents when opening another item | Available | **Complete** | — |
| Multiple document windows and file activation routing | Available | **Planned** | One process, one window per document collection, activation redirected into the running app. |
| Thumbnail drawer | Available | **Planned** | Virtualized WinUI thumbnail rail with selection and accessibility names. |
| Memory/disk thumbnail cache and adjacent prefetch | Available | **Planned** | Cache keys include source identity and the full FITS processing configuration; visible work wins over prefetch. |
| Cached preview while full resolution loads | Available | **Planned** | Never blank an already available preview during a full render. |
| Mono FITS autostretch | Available | **Complete** | Runtime-tested against telescope FITS data. |
| Planar RGB and Bayer/OSC rendering | Available | **Partial** | Core path exists; add representative RGB and Bayer fixtures and visual QA. |
| Seven FITS stretch methods | Available | **Partial** | Auto MTF and GHS are runtime-tested through the Windows editor; complete a visual fixture matrix for Percentile Asinh, Linear, Asinh, explicit MTF, and No Stretch. |
| Ordered stretch stages | Available | **Complete** | Modeless editor adds, selects, removes, and reorders stages; a GHS plus identity stack is runtime-tested through the upstream C ABI. |
| Color strategies | Available | **Complete** | Linked Channels, Per Channel, and Preserve Luminance Color replace the old three-item RGB menu and share the macOS JSON contract. |
| Background-gradient removal | Available | **Complete** | Runtime-tested as an interactive preview on a 261 MB planar-RGB FITS frame with explicit progress. |
| Light Richardson-Lucy deconvolution | Available | **Complete** | Shared Rust runs background correction, deconvolution, then display stretch; the native controls, bounded live preview, full-resolution commit, validation, and inspector provenance are runtime-tested on a 261 MB planar-RGB FITS frame. |
| Debounced live stretch preview | Available | **Complete** | Latest valid draft renders at a bounded 2,048-pixel dimension without replacing the committed full-resolution bitmap. |
| Save/Cancel and stretch undo/redo | Available | **Complete** | Runtime-tested full-resolution commit, cancel restoration, and Ctrl+Z/Ctrl+Shift+Z history. |
| GHS symmetry-point image picker | Available | **Complete** | Runtime-tested end to end: the modeless panel hides, the viewer samples median 3 x 3 Rec.709 display luminance, protection bounds clamp, and the panel returns with a fresh preview. |
| Modeless/detachable stretch panel | Available | **Complete** | Native DPI-aware Windows tool window leaves the viewer undimmed and interactive while previews render. |
| Input and display histogram inspector | Available | **Complete** | Native 256-bin RGB/mono plots use the macOS robust 98th-percentile interior-bin ceiling and expose accessible histogram names. |
| Transfer-curve inspector | Not present | **Deferred** | Track as a shared future enhancement rather than a current macOS parity gap. |
| Fit, pan, wheel zoom, and toolbar zoom | Available | **Complete** | — |
| Pointer-anchored pinch/touch zoom | Available | **Planned** | Add native manipulation handling without rerendering pixels. |
| Image dimensions, format, and color-kind status | Available | **Complete** | — |
| Image statistics and FITS header inspector | Available | **Complete** | Native right-side inspector includes all statistics plus searchable, selectable, and copyable FITS headers. |
| Detailed loading and native error states | Available | **Complete** | — |
| Export stretched image without overlays | Available | **Complete** | Runtime-tested at the full 6,248 x 4,176 source resolution. |
| Export with visible overlays | Available | **Complete** | Uses the same Win2D renderer and layer state as the live viewport. |
| PNG, JPEG, and TIFF export | Available | **Complete** | Native Save As picker selects the encoder from the chosen extension. |

## Catalog settings and managed data

The macOS Settings flow is now part of first-release parity, not a future
enhancement. Previewing remains catalog-free; catalog I/O starts only for
status/setup or an explicitly requested solve.

| Capability | macOS current | Windows | Windows gap / acceptance criterion |
| --- | --- | --- | --- |
| Use Seiza's default catalog directory | Available | **Complete** | Resolved by shared Rust; runtime-tested against the default Windows catalog. |
| Choose and persist a custom catalog directory | Available | **Complete** | WinUI folder picker persists both FutureAccessList access and the display path. |
| Per-component status | Available | **Complete** | Star catalog, blind index, objects, transients, and minor bodies are reported independently. |
| Separate solve-ready and overlay-ready status | Available | **Complete** | Native readiness cards distinguish the two capabilities. |
| Setup presets | Available | **Complete** | Standard blind (recommended), Deepest blind, and All map directly to shared Rust. |
| Download and install | Available | **Complete** | Shared Rust owns manifest, cache, download, and atomic install behavior. |
| Verify or repair an existing install | Available | **Complete** | Retrying reuses files only after their size and digest are verified. |
| Structured setup progress | Available | **Complete** | Preparing, manifest, downloading, verifying, installing, and complete are surfaced. |
| File and byte progress | Available | **Complete** | File name/count, downloaded bytes, total bytes, written bytes, and percentage are supported. |
| Full SHA-256 verification feedback | Available | **Complete** | Settings explains full-file verification and the core reports verification progress. |
| Setup continues after Settings closes | Available | **Complete** | The app-scoped singleton controller owns the worker operation. |
| Solve error links to Catalog Settings | Available | **Complete** | A catalog-readiness failure opens the existing download/repair UI directly. |
| Catalog bundle update discovery and selective datasets | Planned | **Deferred** | Track after first-release catalog parity. |

The shared upstream C ABI includes `seiza_catalog_status_json`,
`seiza_catalog_setup`, the three preset values, and the progress callback
contract while retaining Windows BGRA render output.

## Plate solving

| Capability | macOS current | Windows | Windows gap / acceptance criterion |
| --- | --- | --- | --- |
| Explicit local blind solve | Available | **Complete** | Runtime-tested through the upstream C ABI on a raw telescope FITS frame. |
| Background solve state | Available | **Complete** | Solving remains off the UI thread and leaves viewing/navigation responsive. |
| Default solve range and SIP order | Available | **Complete** | Matches macOS: 0.1-20 arcsec/pixel and SIP order 0. |
| Solution quality summary | Available | **Complete** | Center RA/Dec, scale, matched/detected stars, RMS, elapsed time, and overlay counts. |
| WCS/SIP result model | Available | **Complete** | Source-generated JSON models cover WCS, SIP, stars, objects, motion, contours, and availability. |
| Solve only on explicit request | Available | **Complete** | Catalog and solve work starts only from the Solve command. |
| Stale-result protection during navigation | Available | **Complete** | Cancellation plus source path and load-generation checks prevent stale attachment. |
| Cooperative cancellation and in-process catalog/index cache | Planned | **Deferred** | Add after the first correct end-to-end solve. |
| Hinted solve before blind fallback | Planned | **Deferred** | Use trustworthy FITS header hints when available. |

## Solver overlays

The Windows renderer consumes the upstream solve response directly and shares
one Win2D drawing path between the live viewport and full-resolution export.

| Layer or behavior | macOS current | Windows | Windows gap / acceptance criterion |
| --- | --- | --- | --- |
| Overlay availability, unavailable reasons, and counts | Available | **Complete** | Counts and disabled states remain in the overlay menu; detailed core reasons are selectable in the inspector. |
| Named stars | Available | **Complete** | Catalog palette, markers, and labels share the macOS defaults. |
| Field stars with magnitude | Available | **Complete** | Magnitude-aware restrained markers, off by default. |
| Deep-sky objects | Available | **Complete** | Markers, catalog color, labels, and independent filters. |
| Individual DSO catalogs | Available | **Complete** | Messier, NGC, IC, Sharpless/vdB, LBN, Cederblad, dark nebulae, SNR, UGC, PGC, and Other. |
| Detailed OpenNGC contours | Available | **Complete** | Draws projected contours and falls back to rotated catalog ellipses. |
| Independent object labels and outlines | Available | **Partial** | Separate toggles are complete; add label-collision avoidance for dense fields. |
| Current and historical transients | Available | **Complete** | Independent visibility using acquisition-time classification. |
| Comets and asteroids | Available | **Complete** | Acquisition-time positions, distinct markers, motion direction, and arrows. |
| Detected-star diagnostics | Available | **Complete** | Diagnostic split-cross layer is off by default. |
| RA/Dec coordinate grid and labels | Available | **Complete** | Derived from solved WCS and cached per solution. |
| Field-center marker | Available | **Complete** | Drawn in the common solved-image coordinate space. |
| Hide all overlays | Available | **Complete** | One accessible action without losing catalog filter preferences. |
| Overlay transforms during pan/zoom | Available | **Complete** | Runtime-tested after a 6.20-second solve at Fit and two zoom levels: contours and object extents remain image-scaled while strokes, marker glyphs, label text, and halos remain screen-space stable. |
| Catalog-aware palette and restrained styling | Available | **Complete** | Matches the semantic macOS palette with readable haloed labels. |
| Satellite overlays | Planned | **Deferred** | Requires time span, observer, element epoch, and explicit provenance. |

## Windows platform integration

| Capability | macOS analogue | Windows | Windows gap / acceptance criterion |
| --- | --- | --- | --- |
| Product app icon | macOS app icon | **Complete** | The same Seiza artwork is supplied at Windows executable, taskbar, title-bar, Start, Store, tile, lock-screen, splash, and About sizes. |
| FITS file registration and document icon | Finder association/icon available | **Planned** | MSIX `.fits`, `.fit`, and `.fts` associations with a dedicated icon. |
| Stretched system preview | Quick Look extension available | **Planned** | Explorer Preview Pane handler in a separately hosted native component. |
| Content thumbnails on file icons | Finder thumbnail provider (planned) | **Planned** | Explorer thumbnail provider, isolated from .NET, catalogs, and solving. |
| Signed distributable | Signed/notarized universal DMG | **Planned** | Signed self-contained x64 MSIX; ARM64 follows parity. |
| Release automation | macOS release workflows | **Partial** | CI builds Debug; add signed packaging, artifacts, tags, and protected release environment. |
| Native accessibility | SwiftUI/AppKit accessibility | **Partial** | Core controls are named; add automated coverage for inspector, Settings, and overlay controls. |
| About and native-core provenance | About panel | **Complete** | Reports the Windows app version plus the exact Seiza crate version and 40-character source commit resolved by Cargo. |

## Shared future roadmap

These remain tracked beyond the current macOS parity surface:

- XISF opening (shared core ready; Windows activation, extension handling, and
  representative runtime fixtures remain);
- native RGBA16 export (shared core ready; Windows high-bit-depth export remains);
- transfer-curve visualization and direct curve editing;
- pixel loupe and WCS-aware cursor sampling;
- star-detection overlays with HFR/FWHM measurements;
- compass, scale bar, and WCS cursor readout;
- solve sidecar provenance and FITS WCS-card export;
- sequence comparison, blink/difference views, and registration;
- multi-extension FITS image-HDU navigation;
- lazy FITS cube slices with neighboring-slice preloading;
- updater, crash reporting, and a repeatable performance corpus.

## Delivery order

1. **Complete: Catalog Settings vertical slice** — shared Rust ABI, native
   status/location UI, presets, download/repair, durable progress, and tests.
2. **Complete: Solve vertical slice** — safe C# solve bindings, solve state, stale-result
   protection, solution summary, and Settings remediation.
3. **Complete: Overlay/export vertical slice** — common coordinate transform,
   layer menu, grid/center, catalog layers, and clean/composited export.
4. **Complete: Current FITS processing interaction set** — modeless ordered
   stages, live preview, background removal, light deconvolution, GHS image
   sampling, histograms, history, and full-resolution commit are implemented.
   The remaining stretch-method fixture matrix is tracked as visual QA.
5. **Windows integration** — multi-window activation, file associations,
   Explorer components, signed packaging, and release automation.
