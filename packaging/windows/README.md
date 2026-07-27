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
  -p:SeizaVersion=0.1.4
```

The MSI is written to `dist`. The version must be a valid three-part MSI
version. WiX and its UI/Heat extensions stay pinned to 4.0.6 to match the main
Seiza installer.

The interactive installer's selected-by-default **Launch Seiza** option uses
WiX's unelevated shell action so an all-users install opens Seiza in the
signed-in user's desktop session. CI verifies the generated MSI custom-action
and Finish-button tables before running the elevated smoke test.

An elevated install/launch/uninstall smoke test is available for local and CI
validation:

```powershell
.\.github\scripts\test-windows-installer.ps1 `
  -Msi .\dist\seiza-0.1.4-windows-x86_64.msi
```

Tagged releases use `.github/workflows/release.yml` to build and smoke-test
this MSI, sign it for the in-app updater, generate `appcast.xml` and its
detached signature, and publish all release assets. See
[`docs/AUTO_UPDATE.md`](../../docs/AUTO_UPDATE.md) for the required signing
environment and release procedure.
