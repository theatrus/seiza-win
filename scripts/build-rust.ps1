[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Test
)

$ErrorActionPreference = "Stop"

$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$targetDirectory = Join-Path $workspaceRoot "target"
# An explicit target keeps build scripts/proc macros on the host while the DLLs
# and their native dependencies use /MT. The MSI embeds the Shell provider as a
# temporary custom action, so app-local DLLs would not fix its loader failure.
$nativeTarget = "x86_64-pc-windows-msvc"
$runtimeFlags = " --target $nativeTarget --config target.$nativeTarget.rustflags=['-C','target-feature=+crt-static']"
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

# Cargo includes arbitrary package metadata, including case-distinct keys.
# Read only the package fields we need with a parser that also works in Windows PowerShell.
Add-Type -AssemblyName System.Runtime.Serialization
$metadataBytes = [System.Text.Encoding]::UTF8.GetBytes(($metadataJson -join "`n"))
$reader = [System.Runtime.Serialization.Json.JsonReaderWriterFactory]::CreateJsonReader(
    $metadataBytes, [System.Xml.XmlDictionaryReaderQuotas]::Max)
$metadata = [System.Xml.XmlDocument]::new()
try { $metadata.Load($reader) } finally { $reader.Dispose() }
$packages = foreach ($package in $metadata.SelectNodes('/root/packages/item')) {
    [pscustomobject]@{
        name = $package.SelectSingleNode('name').InnerText
        source = $package.SelectSingleNode('source').InnerText
        version = $package.SelectSingleNode('version').InnerText
        manifest_path = $package.SelectSingleNode('manifest_path').InnerText
    }
}
$nativePackages = @(
    $packages | Where-Object {
        $_.name -eq "seiza-cabi" -and
        $_.source -like "registry+*"
    }
)
if ($nativePackages.Count -ne 1) {
    throw "Expected exactly one registry seiza-cabi package, found $($nativePackages.Count)."
}

$nativePackage = $nativePackages[0]
$stackingPackages = @(
    $packages | Where-Object {
        $_.name -eq "seiza-stacking" -and
        $_.source -like "registry+*"
    }
)
if ($stackingPackages.Count -ne 1) {
    throw "Expected exactly one registry seiza-stacking package, found $($stackingPackages.Count)."
}
$stackingPackage = $stackingPackages[0]
$parallaxPackages = @(
    $packages | Where-Object {
        $_.name -eq "seiza-parallax" -and
        $_.source -like "registry+*"
    }
)
if ($parallaxPackages.Count -ne 1) {
    throw "Expected exactly one registry seiza-parallax package, found $($parallaxPackages.Count)."
}
$parallaxPackage = $parallaxPackages[0]
$vcsInfoPath = Join-Path (Split-Path -Parent $nativePackage.manifest_path) ".cargo_vcs_info.json"
if (-not (Test-Path -LiteralPath $vcsInfoPath)) {
    throw "The published seiza-cabi package does not contain Cargo VCS metadata."
}

$vcsInfo = Get-Content -LiteralPath $vcsInfoPath -Raw | ConvertFrom-Json
$resolvedRevision = [string]$vcsInfo.git.sha1
if ($resolvedRevision -notmatch '^[0-9a-f]{40}$') {
    throw "Could not read the Seiza source commit from '$vcsInfoPath'."
}

$cargoAction = if ($Test) { "test" } else { "build" }
$releaseFlag = if ($Configuration -eq "Release") { " --release" } else { "" }
$nativePackageSpec = "seiza-cabi@$($nativePackage.version)"
$workspaceNativeBuildCommand = "cargo build --package $nativePackageSpec --target-dir `"$targetDirectory`" --locked$releaseFlag$runtimeFlags"
if ($Test) {
    # Stage the published C ABI and its sibling fixtures outside the registry cache.
    $calibrationPackages = @($packages | Where-Object {
        $_.name -eq "seiza-calibration" -and $_.source -like "registry+*"
    })
    if ($calibrationPackages.Count -ne 1) {
        throw "Expected exactly one published calibration package for native test fixtures."
    }
    $modelPackages = @($packages | Where-Object {
        $_.name -eq "seiza-model" -and $_.source -like "registry+*"
    })
    if ($modelPackages.Count -ne 1) {
        throw "Expected exactly one published model package for native test fixtures."
    }
    . (Join-Path $PSScriptRoot "native-test-manifest.ps1")
    $nativeTestManifest = Get-SeizaNativeTestManifest `
        -NativeManifest $nativePackage.manifest_path `
        -CalibrationManifest $calibrationPackages[0].manifest_path `
        -ModelManifest $modelPackages[0].manifest_path `
        -TargetDirectory $targetDirectory `
        -SourceRevision $resolvedRevision
    Write-Host "Testing published C ABI using $nativeTestManifest"
    # Cargo cannot run a dependency package's dev-targets from the consuming
    # workspace, so exercise the published crate's own locked test suite, then
    # exercise the exact stacking leaf selected by the application lock. The
    # C ABI archive can predate a compatible stacking patch and retain the
    # earlier leaf in its packaged lockfile. Finally build the shipped DLL from
    # the application's locked dependency graph.
    $nativeTestCommand = "cargo test --manifest-path `"$nativeTestManifest`" --package seiza-cabi --target-dir `"$targetDirectory`" --locked$releaseFlag$runtimeFlags"
    $stackingTestCommand = "cargo test --manifest-path `"$($stackingPackage.manifest_path)`" --package seiza-stacking --target-dir `"$targetDirectory`" --locked$releaseFlag$runtimeFlags"
    $cargoCommand = "$nativeTestCommand && $stackingTestCommand && $workspaceNativeBuildCommand"
}
else {
    # Select the registry dependency from this workspace so the application
    # lockfile, rather than the crate archive's release-time lockfile, controls
    # compatible transitive patches such as seiza-calibration bug fixes.
    $cargoCommand = $workspaceNativeBuildCommand
}
$thumbnailCommand = "cargo $cargoAction --package seiza-thumbnail-provider --target-dir `"$targetDirectory`" --locked$releaseFlag$runtimeFlags"
$platformCommand = "cargo $cargoAction --package seiza-platform --target-dir `"$targetDirectory`" --locked$releaseFlag$runtimeFlags"
if ($Test) {
    # Testing an rlib/cdylib crate does not emit its deployable DLL. Build both
    # local DLLs explicitly before MSBuild evaluates native content items; on a
    # clean runner no earlier build exists to satisfy their Exists conditions.
    $thumbnailCommand += " && cargo build --package seiza-thumbnail-provider --target-dir `"$targetDirectory`" --locked$releaseFlag$runtimeFlags"
    $platformCommand += " && cargo build --package seiza-platform --target-dir `"$targetDirectory`" --locked$releaseFlag$runtimeFlags"
    # Dependency dev-targets must run from the published archive, not the
    # application workspace. The shipped DLL still uses the application lock.
    $platformCommand += " && cargo test --manifest-path `"$($parallaxPackage.manifest_path)`" --package seiza-parallax --lib --target-dir `"$targetDirectory`" --locked$releaseFlag$runtimeFlags"
}

$buildCommand = "call `"$developerCommand`" -no_logo -arch=x64 -host_arch=x64 && $cargoCommand && $thumbnailCommand && $platformCommand"

Push-Location $workspaceRoot
try {
    & $env:ComSpec /d /s /c $buildCommand
    if ($LASTEXITCODE -ne 0) {
        throw "Cargo failed with exit code $LASTEXITCODE."
    }

    $nativeProfileDirectory = Join-Path $targetDirectory "$nativeTarget\$($Configuration.ToLowerInvariant())"
    $dllNames = @("seiza_cabi.dll", "seiza_thumbnail_provider.dll", "seiza_platform.dll")
    $dllPaths = @($dllNames | ForEach-Object { Join-Path $nativeProfileDirectory $_ })
    & (Join-Path $workspaceRoot ".github\scripts\test-windows-runtime-dependencies.ps1") -Path $dllPaths

    # Keep the existing MSBuild/test payload paths. Audit before copying so an
    # ambient RUSTFLAGS override cannot silently restore the VC++ prerequisite.
    $profileDirectory = Join-Path $targetDirectory $Configuration.ToLowerInvariant()
    [System.IO.Directory]::CreateDirectory($profileDirectory) | Out-Null
    foreach ($dllPath in $dllPaths) {
        Copy-Item -LiteralPath $dllPath -Destination $profileDirectory -Force
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
