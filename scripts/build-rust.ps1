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
        $_.source -like "registry+*"
    }
)
if ($nativePackages.Count -ne 1) {
    throw "Expected exactly one registry seiza-cabi package, found $($nativePackages.Count)."
}

$nativePackage = $nativePackages[0]
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
$cargoCommand = "cargo $cargoAction --manifest-path `"$($nativePackage.manifest_path)`" --package seiza-cabi --target-dir `"$targetDirectory`" --locked"
if ($Configuration -eq "Release") {
    $cargoCommand += " --release"
}

$buildCommand = "call `"$developerCommand`" -no_logo -arch=x64 -host_arch=x64 && $cargoCommand"

Push-Location $workspaceRoot
try {
    & $env:ComSpec /d /s /c $buildCommand
    if ($LASTEXITCODE -ne 0) {
        throw "Cargo failed with exit code $LASTEXITCODE."
    }

    $buildInfo = [ordered]@{
        version = [string]$nativePackage.version
        commit = $resolvedRevision
        repository = "https://github.com/theatrus/seiza"
    } | ConvertTo-Json -Compress
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
