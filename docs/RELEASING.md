# Releasing Seiza for Windows

This is the maintainer runbook for preparing, publishing, and verifying a
Seiza for Windows release. The tag-driven GitHub Actions workflow is the only
supported production release path; do not create the GitHub release or upload
its assets manually.

## Version policy

Seiza for Windows uses semantic versions and immutable `vMAJOR.MINOR.PATCH`
tags. Before 1.0, Windows and macOS keep the same `0.MINOR` product generation;
patch versions may advance independently for platform-specific fixes. At 1.0
and later, the apps keep the same major version while minor and patch versions
may advance independently unless a cross-platform release calls for alignment.

Choose the version before opening the release-preparation pull request. A
published tag or release is never moved, reused, or silently replaced.

## Release system

Pushing a release tag runs [`.github/workflows/release.yml`](../.github/workflows/release.yml).
It runs as two jobs. The first validates the tag and release notes, tests Rust,
publishes the application, Authenticode-signs the first-party binaries, builds
the self-contained all-users x64 WiX MSI, signs that too, verifies every
signature, checks the installer's Finish action, and performs an elevated
install/launch/uninstall smoke test. The second signs the Sparkle appcast,
records the MSI enclosure signature, produces checksums, and creates the GitHub
release.

The split exists because the two kinds of signing need different GitHub
environments and a job can only be in one:

- `release` holds nothing secret. It exists so the OIDC token carries the
  subject `repo:theatrus/seiza-win:environment:release`, which is what the
  Entra federated credential for Azure Artifact Signing is bound to. The
  Authenticode key itself never leaves Microsoft's HSM and there is no signing
  secret in this repository; the repository variables `AZURE_CLIENT_ID`,
  `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `SIGNING_ENDPOINT`,
  `SIGNING_ACCOUNT`, and `SIGNING_PROFILE` name the account and profile to use.
- `signing` holds `SPARKLE_ED_PRIVATE_KEY`, which must match the public key
  compiled into
  [`UpdateController.cs`](../src/Seiza.App/Services/UpdateController.cs). Never
  print, copy into logs, or commit it.

The order matters and the workflow enforces it. Authenticode is applied to the
binaries before WiX packages them, so the installed program carries publisher
identity and not merely the installer; and to the MSI before the appcast is
generated, so the Sparkle signature covers the bytes clients actually download.
An appcast signed over a pre-Authenticode MSI would look healthy in CI and
would make every installed copy refuse the update.

Sparkle signing establishes update trust. Authenticode establishes what Windows
and SmartScreen trust. Neither substitutes for the other.

Before starting, install the same local prerequisites listed in the main
README: Visual Studio's **WinUI application development** workload, .NET 10,
and the repository's Rust toolchain. Install and authenticate GitHub CLI, then
confirm repository access without exposing any secret values:

```powershell
gh auth status
gh secret list --env signing
gh variable list
```

The second command must list `SPARKLE_ED_PRIVATE_KEY`. The third must list the
six `AZURE_*` and `SIGNING_*` variables. Only the tag workflow needs the
private key; local release builds must not download or handle it, and they
produce unsigned installers.

## Prepare the release pull request

Start from a clean, current `main` whose CI run is green:

```powershell
git switch main
git pull --ff-only origin main
git switch -c codex/release-MAJOR.MINOR.PATCH
```

Update every product-version surface:

- `src/Seiza.App/Seiza.App.csproj`: `Version`, `AssemblyVersion`, and
  `FileVersion`;
- `src/Seiza.App/app.manifest` and `Package.appxmanifest`: four-part identity
  version;
- `packaging/windows/Seiza.App.wixproj`: default `SeizaVersion`;
- `.github/workflows/ci.yml`: MSI build and test paths;
- `README.md`: release heading, download/checksum links, highlights, and build
  example;
- `packaging/windows/README.md`: installer examples;
- `docs/releases/vMAJOR.MINOR.PATCH.md`: user-facing release notes.

Do not perform a repository-wide version replacement. Dependency versions,
including NuGet packages and `seiza-*` Cargo crates, are independent of the
Windows product version.

Describe concrete user-visible changes in both the README highlights and the
release notes. Add or refresh tightly cropped screenshots when the release
changes visible UI or Windows shell integration. Verify that screenshots do
not expose unrelated files, accounts, machine names, or a polluted desktop or
Explorer view.

Audit the change before publishing the pull request:

```powershell
rg -n "MAJOR\.MINOR\.PATCH|PREVIOUS_VERSION" README.md docs packaging src .github
git diff --check
git diff --stat
git diff
```

The first search is intentionally manual: every hit should be either a current
product-version surface, current documentation, an independent dependency
version, or immutable historical release notes.

## Validate locally

The release workflow is authoritative. Run the closest practical local subset
before merging:

```powershell
cargo fmt --all -- --check
.\scripts\build-rust.ps1 -Test
dotnet restore Seiza.slnx -r win-x64 -p:Configuration=Release
dotnet build Seiza.slnx -c Release --no-restore
dotnet test Seiza.slnx -c Release --no-build
dotnet build packaging\windows\Seiza.App.wixproj `
  -c Release `
  -p:SeizaVersion=MAJOR.MINOR.PATCH
.\.github\scripts\test-windows-installer-launch.ps1 `
  -Msi .\dist\seiza-MAJOR.MINOR.PATCH-windows-x86_64.msi
```

In an elevated PowerShell session, the full MSI smoke test is:

```powershell
.\.github\scripts\test-windows-installer.ps1 `
  -Msi .\dist\seiza-MAJOR.MINOR.PATCH-windows-x86_64.msi
```

That test installs, launches, and uninstalls the all-users product. Close Seiza
first and do not run it casually over an installation whose state matters.
Also open representative FITS and XISF data, test the release's primary feature,
and confirm About reports the expected app and Seiza core versions.

Push the branch and open a pull request. Merge only after CI is green and the
release notes, README links, screenshots, version surfaces, and diff have been
reviewed.

## Tag and publish

After the release-preparation pull request is merged, update local `main` and
confirm the intended merge commit and app version before tagging:

```powershell
git switch main
git pull --ff-only origin main
git status --short
git log -1 --oneline
Select-String -Path src\Seiza.App\Seiza.App.csproj -Pattern '<Version>'
```

Create one annotated tag on that merge commit and push only that tag:

```powershell
git tag -a vMAJOR.MINOR.PATCH -m "Seiza for Windows MAJOR.MINOR.PATCH"
git push origin vMAJOR.MINOR.PATCH
```

Watch the release workflow through completion:

```powershell
gh run list --workflow release.yml --limit 5
gh run watch RUN_ID --exit-status
```

The successful release must contain exactly these generated assets:

- `seiza-MAJOR.MINOR.PATCH-windows-x86_64.msi`;
- `appcast.xml`;
- `appcast.xml.signature`;
- `SHA256SUMS.txt`.

## Verify the published release

Inspect the release and download links:

```powershell
gh release view vMAJOR.MINOR.PATCH
```

Then verify all of the following:

- the GitHub release title and body use the intended Windows version and notes;
- every expected asset exists and `SHA256SUMS.txt` matches downloaded files;
- the downloaded MSI's Digital Signatures tab names StackFoundry LLC, and so
  do `Seiza.App.exe`, `Seiza.App.dll`, `seiza_cabi.dll`, and
  `SeizaThumbnailProvider.dll` once installed;
- `appcast.xml` names the new version, points at the versioned MSI asset, and
  contains its Ed25519 signature;
- `appcast.xml.signature` is present at the `latest/download` URL;
- the README's MSI and checksum links resolve;
- a previous installed version discovers, downloads, verifies, and opens the
  update; Seiza exits and the WiX major upgrade completes;
- the updated app launches and About reports the expected versions;
- Explorer shows FITS/XISF thumbnails and file associations after the upgrade
  without requiring a restart.

Because the updater feed uses GitHub's `releases/latest/download` redirect,
the newest non-draft GitHub release controls what installed clients see.

## Failure and recovery

Do not move or overwrite a published tag and do not replace assets under an
existing release version. If the workflow has published a GitHub release or
any clients may have observed its appcast, fix forward with a higher patch
version.

If the workflow fails before publishing anything, inspect its logs and repair
the release on a new commit. Prefer a higher patch version here too. Deleting
and recreating an unpublished tag is exceptional: first prove that no GitHub
release, assets, appcast, or external automation observed it, then get explicit
maintainer agreement.

If the release itself is valid but README copy needs correction, use an
ordinary documentation pull request; do not rebuild or retag the release.

## Checklist

- [ ] Current `main` is clean and green.
- [ ] Product version surfaces and release notes agree.
- [ ] README highlights, download links, and screenshots are release-ready.
- [ ] Rust, app, installer-build, and Finish-action tests pass locally.
- [ ] Pull request review and CI are complete.
- [ ] The merged `main` commit is the commit being tagged.
- [ ] The tag workflow succeeds in both the `release` and `signing` environments.
- [ ] The published MSI shows StackFoundry LLC as its publisher.
- [ ] Release assets, checksums, appcast, update path, About, and Explorer
      integration are verified.
