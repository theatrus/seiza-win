# Seiza for Windows

Seiza is a fast, native Windows astronomy image viewer and plate-solving app.
It combines a modern WinUI 3 interface and GPU-backed viewport with the same
Rust image, catalog, and solving core used by Seiza on macOS.

[Download Seiza 0.1.3 for Windows (x64)](https://github.com/theatrus/seiza-win/releases/download/v0.1.3/seiza-0.1.3-windows-x86_64.msi)
· [Release notes and previous versions](https://github.com/theatrus/seiza-win/releases)

![A solved NGC 7000 FITS image with WCS grid, catalog overlays, solution summary, and histogram inspector](docs/images/solved-overlays.png)

## Current development highlights

- Directory image stacking now registers and combines selected FITS or XISF
  light frames into an unstretched 32-bit floating-point FITS image.
- Stacks can be split automatically by filename filter, with an independent
  reference frame for each filter group. Global or local normalization,
  delta-sigma rejection, registration limits, and bias/dark/flat calibration
  masters match the macOS workflow.
- Stacking runs off the UI thread with per-frame progress, accepted/rejected
  counts, cancellation between frames, and automatic opening of the result.

### Directory image stacking

Open a folder containing FITS or XISF light frames, then choose **Stack** from
the toolbar. Select the exposures to include and a reference frame, then tune
normalization, pixel rejection, registration limits, or optional calibration
masters. When filenames contain multiple filter names, Seiza enables a
separate output per filter by default so incompatible channels are not mixed.

| Frame and reference selection | Registration and rejection controls |
| --- | --- |
| ![Selecting light frames and a reference for directory image stacking](docs/images/directory-image-stacking.png) | ![Normalization, pixel rejection, calibration, and registration controls](docs/images/directory-image-stacking-options.png) |

Seiza reports per-frame progress without blocking the viewer, writes an
unstretched 32-bit floating-point FITS result, and opens the completed stack
automatically. Completed files are published atomically, so a failed or
cancelled write cannot replace an existing output with a partial file. Seiza
also validates every output in a multi-filter batch before stacking begins and
identifies any files that were already saved if a later stack is cancelled.

![A completed full-resolution FITS stack reopened automatically in Seiza for Windows](docs/images/directory-image-stacking-result.png)

## Seiza 0.1.3 release highlights

This is the first release delivered through Seiza's signed in-app updater:

- Fixed the installer's selected-by-default **Launch Seiza** action so Finish
  opens the installed app in the signed-in user's desktop session.
- The launch now uses WiX's unelevated shell action with the app's full
  installed path instead of a directory-relative executable command.
- Seiza 0.1.2 users can discover, verify, and install this release from
  **More > Check for updates** or **Settings > Check now**.

The earlier preview highlights still apply:

- GPU-backed FITS and XISF viewing with a cached thumbnail browser and
  image-anchored pan, wheel zoom, and pinch zoom.
- Seven stretch models, ordered adjustment stages, RGB strategies, background
  extraction, live histograms, and Richardson-Lucy deconvolution.
- Local plate solving with WCS grids, deep-sky contours, named and field stars,
  transients, solar-system objects, and motion vectors.
- Full-resolution clean or overlay-composited export, FITS WCS sidecars, and
  image or processing-adjustment copy/paste.
- Native catalog download, verification, repair, and relocation UI.
- A self-contained, all-users MSI with FITS and XISF Windows file associations.

## What it can do

- Open FITS, XISF, PNG, JPEG, and TIFF images, folders, or dropped files; browse
  naturally sorted image sets in a cached thumbnail rail without blocking the UI.
- Register and stack selected FITS or XISF frames from an opened folder, with
  optional per-filter outputs, calibration masters, normalization, pixel
  rejection, reference selection, progress, and cancellation.
- Fit, pan, wheel-zoom, and pointer-anchored pinch-zoom a GPU-backed
  high-resolution viewport. Overlay geometry and labels stay registered to image
  pixels while line weights remain readable.
- Stretch FITS and XISF data with Auto MTF, GHS, Percentile Asinh, Linear, Asinh,
  explicit MTF, or no stretch; stack and reorder stages with live previews,
  undo, and redo.
- Process linear astronomy data with background-gradient removal, three color
  strategies, and conservative Richardson-Lucy deconvolution.
- Inspect image statistics, input/display RGB histograms, searchable source
  headers, processing provenance, and plate-solution quality.
- Blind-solve locally using downloaded catalogs, then draw a WCS grid, field
  center, named and field stars, deep-sky catalog objects and contours,
  transients, and solar-system motion overlays when their catalogs are present.
- Start solving from either the toolbar or the inspector, and export a solved
  TAN/TAN-SIP header as a standards-compatible FITS `.wcs` file.
- Export the full-resolution stretched image as PNG, JPEG, or TIFF, either
  clean or composited with the currently visible overlays.
- Copy and paste rendered images or a versioned set of Seiza processing
  adjustments through the Windows clipboard.
- Download, verify, repair, and relocate Seiza catalogs from the native
  Settings window. The recommended preset includes deep-sky objects,
  transients, and solar-system bodies as well as solving data.
- Check the signed Sparkle feed automatically or on demand, then download,
  verify, and open an in-place MSI update without visiting the Releases page.

| Astronomy processing | Catalog management |
| --- | --- |
| ![Modeless astronomy-image processing controls with background correction and deconvolution](docs/images/astronomy-processing.jpg) | ![Catalog status, location, and installation controls](docs/images/catalog-settings.png) |

The maintained [feature-parity matrix](docs/FEATURE_PARITY.md) records the
remaining macOS and Windows integration work.

## Install

Download the [Seiza 0.1.3 x64 MSI](https://github.com/theatrus/seiza-win/releases/download/v0.1.3/seiza-0.1.3-windows-x86_64.msi).
Its [SHA-256 checksums](https://github.com/theatrus/seiza-win/releases/download/v0.1.3/SHA256SUMS.txt)
are published beside it. The installer places Seiza in
`Program Files\Seiza for Windows` for every user, adds a shared Start Menu
shortcut, and registers `.fit`, `.fits`, `.fts`, and `.xisf` with Windows
Default Apps.

The MSI is fully self-contained. It includes .NET 10, the Windows App SDK/WinUI
runtime, Win2D, and the Cargo-locked Seiza Rust core, so installation and first
launch do not download or bootstrap a separate runtime. Windows will request
administrator approval because this is an all-users installation.

System requirements are Windows 11 24H2 or newer on an x64 computer. Release
signing is still on the roadmap, so Windows may show an unknown-publisher
warning for this preview installer.

## Build and test

Install:

- Visual Studio with the **WinUI application development** workload
- .NET 10 SDK
- Rust 1.89 or newer with the `x86_64-pc-windows-msvc` target

Then build the app and native core:

```powershell
.\scripts\build-rust.ps1 -Test
dotnet build Seiza.slnx
```

Build the self-contained all-users WiX MSI:

```powershell
dotnet build packaging\windows\Seiza.App.wixproj `
  -c Release `
  -p:SeizaVersion=0.1.3
```

The installer is written to `dist`. See the
[installer notes](packaging/windows/README.md) for its layout and smoke test.
The [signed update guide](docs/AUTO_UPDATE.md) documents appcast generation and
the tag-driven release workflow.

## Architecture

WinUI 3 and C# own Windows lifecycle, controls, accessibility, and settings.
Win2D owns interactive image and vector-overlay presentation. The published
`seiza-cabi` Rust crate and its crates.io dependencies own decoding, FITS/XISF
processing, statistics, catalogs, WCS, and solving. `Cargo.lock` selects exact
versions, and the About dialog reports the C ABI version and packaged source
commit.

See [Architecture](docs/ARCHITECTURE.md) for component boundaries and
performance rules.
