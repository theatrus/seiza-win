[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Test
)

$ErrorActionPreference = "Stop"

$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$targetDirectory = Join-Path $workspaceRoot "target"
$vsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vsWhere)) {
    throw "Visual Studio Installer's vswhere.exe was not found."
}

$visualStudioPath = & $vsWhere `
    -latest `
    -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $visualStudioPath) {
    throw "Visual Studio with the Desktop development with C++ workload was not found."
}

$developerCommand = Join-Path $visualStudioPath "Common7\Tools\VsDevCmd.bat"
$metadataJson = & cargo metadata --format-version 1 --locked
if ($LASTEXITCODE -ne 0) {
    throw "Cargo metadata failed with exit code $LASTEXITCODE."
}

$metadata = $metadataJson | ConvertFrom-Json
$nativePackages = @(
    $metadata.packages | Where-Object {
        $_.name -eq "seiza-cabi" -and
        ($_.source -like "registry+*" -or $_.source -like "git+*")
    }
)
if ($nativePackages.Count -ne 1) {
    throw "Expected exactly one resolved seiza-cabi package, found $($nativePackages.Count)."
}

$nativePackage = $nativePackages[0]
$stackingPackages = @(
    $metadata.packages | Where-Object {
        $_.name -eq "seiza-stacking" -and
        ($_.source -like "registry+*" -or $_.source -like "git+*")
    }
)
if ($stackingPackages.Count -ne 1) {
    throw "Expected exactly one resolved seiza-stacking package, found $($stackingPackages.Count)."
}
$stackingPackage = $stackingPackages[0]
$parallaxPackages = @(
    $metadata.packages | Where-Object {
        $_.name -eq "seiza-parallax" -and
        ($_.source -like "registry+*" -or $_.source -like "git+*")
    }
)
if ($parallaxPackages.Count -ne 1) {
    throw "Expected exactly one resolved seiza-parallax package, found $($parallaxPackages.Count)."
}
$parallaxPackage = $parallaxPackages[0]
$vcsInfoPath = Join-Path (Split-Path -Parent $nativePackage.manifest_path) ".cargo_vcs_info.json"
if ($nativePackage.source -like "git+*") {
    $resolvedRevision = ([string]$nativePackage.source -split '#')[-1]
} else {
    if (-not (Test-Path -LiteralPath $vcsInfoPath)) { throw "Published C ABI is missing Cargo VCS metadata." }
    $resolvedRevision = [string](Get-Content -LiteralPath $vcsInfoPath -Raw | ConvertFrom-Json).git.sha1
}
if ($resolvedRevision -notmatch '^[0-9a-f]{40}$') { throw "The core must resolve to an exact source commit." }

$cargoAction = if ($Test) { "test" } else { "build" }
$releaseFlag = if ($Configuration -eq "Release") { " --release" } else { "" }
$nativePackageSpec = "seiza-cabi@$($nativePackage.version)"
$workspaceNativeBuildCommand = "cargo build --package $nativePackageSpec --target-dir `"$targetDirectory`" --locked$releaseFlag"
if ($Test) {
    # The 0.47.0 C ABI archive refers to sibling calibration fixtures. Stage
    # the unchanged published crate and fixtures from the matching published
    # calibration archive; never write into Cargo's registry cache or fetch a
    # moving branch. Exact Git revisions already carry sibling fixtures in their
    # workspace; registry archives use the staging helper. The shipped DLL is
    # always built from the app lock below.
    $nativeTestManifest = $nativePackage.manifest_path
    $calibrationPackages = @($metadata.packages | Where-Object {
        $_.name -eq "seiza-calibration" -and ($_.source -like "registry+*" -or $_.source -like "git+*")
    })
    if ($calibrationPackages.Count -ne 1) {
        throw "Expected exactly one resolved calibration package for native test fixtures."
    }
    if ($nativePackage.source -like "registry+*") {
        . (Join-Path $PSScriptRoot "native-test-manifest.ps1")
        $nativeTestManifest = Get-SeizaNativeTestManifest `
            -NativeManifest $nativePackage.manifest_path `
            -CalibrationManifest $calibrationPackages[0].manifest_path `
            -TargetDirectory $targetDirectory `
            -SourceRevision $resolvedRevision
    }
    Write-Host "Testing resolved C ABI using $nativeTestManifest"
    # Cargo cannot run a dependency package's dev-targets from the consuming
    # workspace, so exercise the published crate's own locked test suite, then
    # exercise the exact stacking leaf selected by the application lock. The
    # C ABI archive can predate a compatible stacking patch and retain the
    # earlier leaf in its packaged lockfile. Finally build the shipped DLL from
    # the application's locked dependency graph.
    $nativeTestCommand = "cargo test --manifest-path `"$nativeTestManifest`" --package seiza-cabi --target-dir `"$targetDirectory`" --locked$releaseFlag"
    $stackingTestCommand = "cargo test --manifest-path `"$($stackingPackage.manifest_path)`" --package seiza-stacking --target-dir `"$targetDirectory`" --locked$releaseFlag"
    $cargoCommand = "$nativeTestCommand && $stackingTestCommand && $workspaceNativeBuildCommand"
}
else {
    # Select the resolved dependency from this workspace so the application
    # lockfile, rather than the crate archive's release-time lockfile, controls
    # compatible transitive patches such as seiza-calibration bug fixes.
    $cargoCommand = $workspaceNativeBuildCommand
}
$thumbnailCommand = "cargo $cargoAction --package seiza-thumbnail-provider --target-dir `"$targetDirectory`" --locked$releaseFlag"
$platformCommand = "cargo $cargoAction --package seiza-platform --target-dir `"$targetDirectory`" --locked$releaseFlag"
if ($Test) {
    # Testing an rlib/cdylib crate does not emit its deployable DLL. Build both
    # local DLLs explicitly before MSBuild evaluates native content items; on a
    # clean runner no earlier build exists to satisfy their Exists conditions.
    $thumbnailCommand += " && cargo build --package seiza-thumbnail-provider --target-dir `"$targetDirectory`" --locked$releaseFlag"
    $platformCommand += " && cargo build --package seiza-platform --target-dir `"$targetDirectory`" --locked$releaseFlag"
    # Dependency dev-targets must run from the published archive, not the
    # application workspace. The shipped DLL still uses the application lock.
    $platformCommand += " && cargo test --manifest-path `"$($parallaxPackage.manifest_path)`" --package seiza-parallax --lib --target-dir `"$targetDirectory`" --locked$releaseFlag"
}

$buildCommand = "call `"$developerCommand`" -no_logo -arch=x64 -host_arch=x64 && $cargoCommand && $thumbnailCommand && $platformCommand"

Push-Location $workspaceRoot
try {
    & $env:ComSpec /d /s /c $buildCommand
    if ($LASTEXITCODE -ne 0) {
        throw "Cargo failed with exit code $LASTEXITCODE."
    }

    $profileDirectory = Join-Path $targetDirectory $Configuration.ToLowerInvariant()
    foreach ($dllName in @("seiza_cabi.dll", "seiza_thumbnail_provider.dll", "seiza_platform.dll")) {
        $dllPath = Join-Path $profileDirectory $dllName
        if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
            throw "Native build did not produce required DLL '$dllPath'."
        }
    }

    $buildInfo = [ordered]@{
        version = [string]$nativePackage.version
        commit = $resolvedRevision
        repository = "https://github.com/theatrus/seiza"
        parallaxVersion = [string]$parallaxPackage.version
    }
    $buildInfo = $buildInfo | ConvertTo-Json -Compress
    $buildInfoPath = Join-Path $targetDirectory "seiza-build-info.json"
    [System.IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
    [System.IO.File]::WriteAllText(
        $buildInfoPath,
        $buildInfo,
        [System.Text.UTF8Encoding]::new($false))
}
finally {
    Pop-Location
}
