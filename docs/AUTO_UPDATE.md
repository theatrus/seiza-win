# Auto-update proposal: NetSparkleUpdater

Status: proposal. This documents the plan for in-app update checks; no code
in this change.

## Goal

The app should tell the user when a new release exists and install it in
place. Today users must watch the GitHub Releases page and download each
MSI by hand.

## Recommendation

Use [NetSparkleUpdater](https://github.com/NetSparkleUpdater/NetSparkle),
the maintained .NET port of the Sparkle update framework (MIT licensed,
`NetSparkleUpdater.SparkleUpdater` on NuGet). It fits what we already ship:

- It reads the same signed `appcast.xml` feed format Sparkle uses, so the
  macOS app (see the matching seiza-mac proposal) and this app can share
  one release convention and one key-management story.
- It supports MSI artifacts directly: it downloads the new installer,
  verifies its ed25519 signature, and launches it. Our WiX `MajorUpgrade`
  already gives clean in-place upgrades, as exercised by the 0.1.0 →
  0.1.1 rename.
- It has no UI of its own unless asked; we drive it from WinUI with our
  own dialog, avoiding the WPF/WinForms assemblies.

## Integration plan

1. **Add the NuGet package** to `Seiza.App` and create a `SparkleUpdater`
   with the appcast URL and the ed25519 public key. Disable its built-in
   UI (`UIFactory = null`); implement the prompt as a small WinUI dialog.
2. **Check cadence**: check once at startup (after the main window shows)
   and from a "Check for updates…" item in the app menu. Store
   "skip this version" in the existing settings store.
3. **Generate the key once** with NetSparkle's `netsparkle-generate-appcast`
   tool. Keep the private key as a GitHub Actions secret; publish the
   public key in the repo.
4. **Publish an appcast**: extend the release process to run
   `netsparkle-generate-appcast` over the released MSI and upload
   `appcast.xml` as a release asset. Feed URL:
   `https://github.com/theatrus/seiza-win/releases/latest/download/appcast.xml`.
   Releases are currently cut by hand (CI builds the MSI artifact, the
   release is created manually); the appcast step belongs in a future
   `release.yml` so a tag push produces MSI, checksum, appcast, and the
   GitHub release in one pass.
5. **Install flow**: on user approval, NetSparkle downloads the MSI,
   verifies it, and starts it; the app exits and Windows Installer performs
   the `MajorUpgrade`. No silent installs while builds are unsigned — the
   user sees the standard UAC and installer UI.
6. **Release notes**: point each appcast item at the matching
   `docs/releases/` page.

## Security notes

- The ed25519 private key is a repository secret; pull requests never see
  it. The app ships only the public key.
- The appcast and MSI travel over HTTPS from GitHub; the signature check
  protects against a compromised or spoofed download even so.
- Production code signing of the MSI itself stays on the roadmap and is
  independent of this: NetSparkle's signature gates what the updater will
  launch, Authenticode gates what SmartScreen says when it runs.

## Alternatives considered

- **Velopack** — solid and fast, but it owns packaging (its own installer
  and delta format), which would replace the WiX MSI, its all-users
  install, PATH-free file associations, and the smoke-tested upgrade path.
  Not worth the migration for an update check.
- **winget** — worth doing as well (publish to `winget-pkgs` so
  `winget upgrade` works), but it is a separate distribution channel, not
  an in-app check, and it does not reach users who installed from the MSI
  link.
- **Hand-rolled GitHub API check** — a version compare against
  `/releases/latest`, then opening the browser. Less code today, but no
  signature verification, no download-and-launch, and we would
  re-implement scheduling and skip-version logic NetSparkle already has.

## Testing

- Unit-test the version-compare and prompt view-model; NetSparkle's feed
  handling needs no tests of ours.
- Manual: serve a local appcast with a bumped version and verify prompt,
  download, MSI launch, upgrade, and relaunch.
- The installer smoke test already covers the `MajorUpgrade` path the
  updater relies on.
