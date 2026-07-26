# Signed in-app updates

Seiza for Windows uses
[NetSparkleUpdater](https://github.com/NetSparkleUpdater/NetSparkle) with a
native WinUI interface. It follows the same Sparkle release convention and
Ed25519 trust root as Seiza for Mac while continuing to ship the existing
all-users WiX MSI.

## User experience

- Seiza checks once after the main window opens when automatic checks are
  enabled. A failed background check is silent.
- **More > Check for updates…** always performs a visible manual check.
- **Settings > Software updates** controls automatic checks and offers another
  **Check now** button.
- An available update can be installed, deferred, or skipped. Manual checks
  still show a skipped version; automatic checks do not.
- NetSparkle downloads the MSI and verifies its Ed25519 signature before
  opening it. The app then exits and WiX `MajorUpgrade` performs the in-place
  update. The normal Windows Installer and UAC interface remains visible.

The feed is:

`https://github.com/theatrus/seiza-win/releases/latest/download/appcast.xml`

The app runs NetSparkle in strict mode. Both the MSI enclosure signature in
`appcast.xml` and the detached `appcast.xml.signature` must verify against the
public key compiled into the app.

## Release setup

The `signing` GitHub Actions environment must contain a secret named
`SPARKLE_ED_PRIVATE_KEY`. It is the base64 Ed25519 private key corresponding to
the shared public key in `UpdateController.cs`. Use the same secret as
`theatrus/seiza-mac`; never add the private key to this repository.

The repository pins the NetSparkle appcast generator as a local .NET tool.
Restore it with:

```powershell
dotnet tool restore
```

For a release:

1. Set the app version in `src/Seiza.App/Seiza.App.csproj`.
2. Add `docs/releases/vMAJOR.MINOR.PATCH.md`.
3. Merge the release commit and tag it `vMAJOR.MINOR.PATCH`.
4. Push the tag. `.github/workflows/release.yml` tests the core, builds and
   smoke-tests the all-users MSI, creates the signed appcast, produces
   checksums, and publishes the GitHub release.

Every updater-enabled release publishes these assets:

- `seiza-MAJOR.MINOR.PATCH-windows-x86_64.msi`
- `appcast.xml`
- `appcast.xml.signature`
- `SHA256SUMS.txt`

Versions 0.1.0 and 0.1.1 predate the updater feed. Version 0.1.2 seeds the
feed, and version 0.1.3 is the first end-to-end in-app upgrade.

## Local verification

Generate a temporary key pair outside the repository, build two MSI versions,
and serve the newer MSI, `appcast.xml`, and `appcast.xml.signature` over HTTP.
For production-like testing, compile a test build with the temporary public key
and a local appcast URL. Verify all of these paths:

- no update available;
- feed unavailable during startup and during a manual check;
- update available, later, and skip-version behavior;
- canceled and failed downloads;
- tampered appcast, detached signature, and MSI rejection;
- verified MSI launch, application exit, UAC, and WiX major upgrade.

Production MSI Authenticode signing is independent of Sparkle signing and
remains required to remove the unknown-publisher warning. Sparkle signatures
determine what Seiza trusts; Authenticode determines what Windows and
SmartScreen trust.
