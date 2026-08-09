# Windows installer

The WiX 4 MSI installs Seiza for every user into `Program Files\Seiza for
Windows`, adds a shared Start Menu shortcut, and registers Seiza with Windows
Default Apps for `.fit`, `.fits`, `.fts`, and `.xisf` files. It also registers
`SeizaThumbnailProvider.dll`, a native Rust Explorer shell extension containing
content-thumbnail and Preview Pane handlers for all four extensions.

The payload is self-contained: it includes .NET 10, the Windows App SDK/WinUI
runtime, Win2D, and the Cargo-locked Seiza Rust core. Installation and first launch do
not need a network connection or separate runtime installers.

Explorer supplies file contents through `IInitializeWithStream`. Windows loads
the thumbnail COM class in its default isolated `dllhost.exe` and the Preview
Pane COM class in the x64 low-integrity `prevhost.exe`. The installer
deliberately does not set `DisableProcessIsolation`; neither class has a WinUI,
.NET, catalog, or solver dependency. A native post-finalize custom action broadcasts
`SHCNE_ASSOCCHANGED` after install, repair, and uninstall so Explorer reloads
the handler and invalidates stale icon and thumbnail cache entries immediately.

Build the installer from the repository root:

```powershell
dotnet build packaging\windows\Seiza.App.wixproj `
  -c Release `
  -p:SeizaVersion=0.6.0
```

The MSI is written to `dist`. The version must be a valid three-part MSI
version. WiX and its UI/Heat extensions stay pinned to 4.0.6 to match the main
Seiza installer.

That single command publishes the application and packages it in one pass.
Release builds need a gap in between, so they can Authenticode-sign the
application before WiX seals it into the installer, and they split the same
work in two:

```powershell
dotnet build packaging\windows\Seiza.App.wixproj `
  -c Release `
  -p:SeizaVersion=0.6.0 `
  -t:PublishSeizaApp
# sign artifacts\publish\win-x64 here
dotnet build packaging\windows\Seiza.App.wixproj `
  -c Release `
  -p:SeizaVersion=0.6.0 `
  -p:SeizaSkipPublish=true
```

`SeizaSkipPublish` matters: without it the second command would publish again
and overwrite the signed files, since the SDK's copy decides a file is unchanged
by size and timestamp and a signature changes both. The build fails rather than
harvesting an empty directory if nothing has been published.

The interactive installer's selected-by-default **Launch Seiza** option uses
WiX's unelevated shell action so an all-users install opens Seiza in the
signed-in user's desktop session. CI verifies the generated MSI custom-action
and Finish-button tables before running the elevated smoke test.
The smoke test also verifies the provider DLL, both COM classes, Preview Pane
host AppID and global handler-list registration, all thumbnail and preview
extension mappings, the Shell-notification action, and complete registration
removal on uninstall.

An elevated install/launch/uninstall smoke test is available for local and CI
validation:

```powershell
.\.github\scripts\test-windows-installer.ps1 `
  -Msi .\dist\seiza-0.6.0-windows-x86_64.msi
```

Tagged releases use `.github/workflows/release.yml` to build and smoke-test
this MSI, Authenticode-sign the first-party binaries and the installer as
StackFoundry LLC, sign it for the in-app updater, generate `appcast.xml` and its
detached signature, and publish all release assets. See
[`docs/RELEASING.md`](../../docs/RELEASING.md) for the complete maintainer
checklist and [`docs/AUTO_UPDATE.md`](../../docs/AUTO_UPDATE.md) for the updater
trust model and signing environment.
