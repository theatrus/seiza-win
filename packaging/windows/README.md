# Windows installer

The WiX 4 MSI installs Seiza for every user into `Program Files\Seiza for
Windows`, adds a shared Start Menu shortcut, and registers Seiza with Windows
Default Apps for `.fit`, `.fits`, `.fts`, and `.xisf` files.

The payload is self-contained: it includes .NET 10, the Windows App SDK/WinUI
runtime, Win2D, and the Cargo-locked Seiza Rust core. Installation and first launch do
not need a network connection or separate runtime installers.

Build the installer from the repository root:

```powershell
dotnet build packaging\windows\Seiza.App.wixproj `
  -c Release `
  -p:SeizaVersion=0.1.0
```

The MSI is written to `dist`. The version must be a valid three-part MSI
version. WiX and its UI/Heat extensions stay pinned to 4.0.6 to match the main
Seiza installer.

An elevated install/launch/uninstall smoke test is available for local and CI
validation:

```powershell
.\.github\scripts\test-windows-installer.ps1 `
  -Msi .\dist\seiza-0.1.0-windows-x86_64.msi
```
