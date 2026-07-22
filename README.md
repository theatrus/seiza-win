# Seiza for Windows

A first-class native Windows astronomy image viewer and plate-solving app.
WinUI 3 owns the Windows experience while the existing Seiza Rust libraries
own image decoding, FITS stretching, metadata, solving, and overlay data.

## Architecture

- WinUI 3, XAML, and C# for windows, controls, accessibility, activation, and settings.
- Win2D for the GPU-backed image viewport and solve-overlay drawing.
- The unified upstream `seiza-cabi` crate, pinned to an exact Seiza Git commit and built as a Rust `cdylib`.
- Packaged, self-contained MSIX distribution, initially targeting Windows 11 x64.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the decisions and component boundaries,
and [docs/FEATURE_PARITY.md](docs/FEATURE_PARITY.md) for the maintained macOS-to-Windows
feature matrix and delivery order.

## Prerequisites

- Windows 11 with Developer Mode enabled
- Visual Studio 2026 with the **WinUI application development** workload
- .NET 10 SDK
- Rust 1.89 or newer using the `x86_64-pc-windows-msvc` target

## Build and test

```powershell
.\scripts\build-rust.ps1 -Test
dotnet build Seiza.slnx
```

The .NET build enters the installed Visual Studio developer environment,
builds the pinned upstream `seiza_cabi.dll`, and copies it and its exact
version/commit metadata into the app output automatically.

## Current vertical slice

- Opens FITS, PNG, JPEG, and TIFF images through the Rust core.
- Opens a folder or discovers sibling images next to a selected file.
- Naturally sorts and navigates supported images without blocking the UI thread.
- Uploads a BGRA8 frame once to Win2D, then fits, pans, and zooms on the GPU.
- Edits FITS processing in a modeless native tool window with seven stretch methods, ordered stages, three color strategies, optional background-gradient removal, light Richardson-Lucy deconvolution, numeric/sliding controls, validation, and debounced live previews.
- Samples the GHS symmetry point directly from median 3 x 3 display luminance in the image and returns the result to the modeless editor.
- Commits full-resolution stretch changes only on Save and supports toolbar/keyboard undo and redo; Cancel restores the committed image immediately.
- Shows image statistics, input/display RGB histograms, deconvolution provenance, searchable/copyable FITS headers, solve quality, and overlay availability in a native inspector.
- Accepts image and folder drag-and-drop and reports native errors with actionable detail.
- Reports solver and overlay catalog readiness in native Catalog Settings.
- Persists default or custom catalog locations and installs, verifies, or repairs shared Rust catalog presets with durable progress.
- Plate-solves explicitly in the background with stale-result protection and a native quality summary.
- Draws WCS grids, field center, stars, catalog-filtered deep-sky objects, contours, transients, and solar-system motion overlays in Win2D with zoom-stable screen-space strokes, markers, and labels.
- Exports the full-resolution stretched image as PNG, JPEG, or TIFF, with or without the currently visible overlays.
