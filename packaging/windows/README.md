# Windows installer

The WiX 4 MSI installs Seiza for every user into `Program Files\Seiza for
Windows`, adds a shared Start Menu shortcut, and registers Seiza with Windows
Default Apps for `.fit`, `.fits`, `.fts`, and `.xisf` files. It also registers
`SeizaThumbnailProvider.dll`, a native Rust Explorer shell extension containing
content-thumbnail and Preview Pane handlers for all four extensions.

The payload is self-contained: it includes .NET 10, the Windows App SDK/WinUI
runtime, Win2D, and the Cargo-locked Seiza Rust core and offline distance adapter.
The three Rust DLLs statically link their C/C++ runtime, including native C
libraries compiled through Cargo. No Visual C++ Redistributable package is
needed. This also covers the MSI's embedded Shell-notification DLL: it is loaded
from an Installer temporary directory during install, repair and uninstall,
where runtime files beside the installed app cannot satisfy its dependencies.

It also includes the Inter font license used by parallax labels. Installation and first launch do
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
  -p:SeizaVersion=0.9.2
```

`build-rust.ps1` uses an explicit `x86_64-pc-windows-msvc` target and
`target-feature=+crt-static` for all DLL builds and native tests, as described in
the [Rust linkage reference](https://doc.rust-lang.org/reference/linkage.html#static-and-dynamic-c-runtimes).
Host build scripts and procedural macros retain their host runtime. The audited
DLLs are copied back to the existing `target/release` paths for MSBuild/tests.
An ambient Rust-flags override that reintroduces a dynamic VC runtime fails the
dependency audit rather than shipping a hidden prerequisite.

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
  -p:SeizaVersion=0.9.2 `
  -t:PublishSeizaApp
# sign artifacts\publish\win-x64 here
dotnet build packaging\windows\Seiza.App.wixproj `
  -c Release `
  -p:SeizaVersion=0.9.2 `
  -p:SeizaSkipPublish=true
```

`SeizaSkipPublish` matters: without it the second command would publish again
and overwrite the signed files, since the SDK's copy decides a file is unchanged
by size and timestamp and a signature changes both. The build fails rather than
harvesting an empty directory if nothing has been published. A normal publish
first removes its dedicated output directory so a deleted or local QA binary
cannot survive from an earlier build and be harvested into the MSI.

Both normal publishing and `SeizaSkipPublish=true` packaging audit every published
DLL/EXE's normal and delay-load imports with the Visual Studio C++ tools. A
versioned Visual C++ runtime import fails the build. The audit only reads files,
so the release signing gap and signed payload bytes are preserved.

CI and releases also run a separate `/MT` native loader process with invalid VC
runtime DLLs in its private application directory. A deliberately `/MD` control
must fail to load, while the three Seiza DLLs and the Shell action extracted
from the actual MSI must load and execute. This catches the prerequisite even
on development/CI machines that already have the system-wide runtime installed.
Run the same check locally after packaging:

```powershell
.\.github\scripts\test-windows-native-loader.ps1 `
  -Directory .\artifacts\publish\win-x64 `
  -Msi .\dist\seiza-0.9.2-windows-x86_64.msi
```

The interactive installer's selected-by-default **Launch Seiza** option uses
WiX's unelevated shell action so an all-users install opens Seiza in the
signed-in user's desktop session. CI verifies the generated MSI custom-action
and Finish-button tables before running the elevated smoke test.
The smoke test verifies that the real Seiza main window starts, not merely that
a loader-error process remains alive. It also verifies the native distance adapter, Inter license, provider DLL, both COM classes, Preview Pane
host AppID and global handler-list registration, all thumbnail and preview
extension mappings, the Shell-notification action, and complete registration
removal on uninstall.

An elevated install/launch/uninstall smoke test is available for local and CI
validation:

```powershell
.\.github\scripts\test-windows-installer.ps1 `
  -Msi .\dist\seiza-0.9.2-windows-x86_64.msi
```

Tagged releases use `.github/workflows/release.yml` to build and smoke-test
this MSI, Authenticode-sign the first-party binaries and the installer as
StackFoundry LLC, sign it for the in-app updater, generate `appcast.xml` and its
detached signature, and publish all release assets. See
[`docs/RELEASING.md`](../../docs/RELEASING.md) for the complete maintainer
checklist and [`docs/AUTO_UPDATE.md`](../../docs/AUTO_UPDATE.md) for the updater
trust model and signing environment.
