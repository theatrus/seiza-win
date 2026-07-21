# Seiza for Windows

A first-class native Windows astronomy image viewer and plate-solving app.
WinUI 3 owns the Windows experience while the existing Seiza Rust libraries
own image decoding, FITS stretching, metadata, solving, and overlay data.

## Architecture

- WinUI 3, XAML, and C# for windows, controls, accessibility, activation, and settings.
- Win2D for the GPU-backed image viewport and solve-overlay drawing.
- `seiza-cabi` as a Rust `cdylib`, with opaque handles for pixels and JSON for evolving records.
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
builds `seiza_cabi.dll`, and copies it into the app output automatically.

## Current vertical slice

- Opens FITS, PNG, JPEG, and TIFF images through the Rust core.
- Opens a folder or discovers sibling images next to a selected file.
- Naturally sorts and navigates supported images without blocking the UI thread.
- Uploads a BGRA8 frame once to Win2D, then fits, pans, and zooms on the GPU.
- Accepts image and folder drag-and-drop and reports native errors with actionable detail.
