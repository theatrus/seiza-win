function Get-SeizaNativeTestManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$NativeManifest,
        [Parameter(Mandatory)][string]$CalibrationManifest,
        [Parameter(Mandatory)][string]$TargetDirectory,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceRevision
    )

    $nativeDirectory = Split-Path -Parent $NativeManifest
    # -Quiet emits a Boolean for every file, including False. Taking its first
    # result misses references in later files. -List emits matching files only.
    $fixtureReferences = @(Get-ChildItem -LiteralPath (Join-Path $nativeDirectory "src") -Recurse -Filter *.rs |
        Select-String -SimpleMatch "../../seiza-calibration/tests/fixtures/" -List)
    if ($fixtureReferences.Count -eq 0) {
        return $NativeManifest
    }

    $calibrationDirectory = Split-Path -Parent $CalibrationManifest
    $fixtureDirectory = Join-Path $calibrationDirectory "tests\fixtures"
    if (-not (Test-Path -LiteralPath $fixtureDirectory)) {
        throw "The published calibration archive does not contain required native test fixtures."
    }
    $calibrationVcs = Get-Content -LiteralPath (Join-Path $calibrationDirectory ".cargo_vcs_info.json") -Raw | ConvertFrom-Json
    if ($calibrationVcs.git.sha1 -ne $SourceRevision) {
        throw "Calibration test fixtures must come from the C ABI's exact published source revision."
    }

    $testRoot = Join-Path $TargetDirectory "published-tests\$SourceRevision"
    $testCrate = Join-Path $testRoot "seiza-cabi"
    $testFixtures = Join-Path $testRoot "seiza-calibration\tests\fixtures"
    New-Item -ItemType Directory -Force -Path $testCrate, $testFixtures | Out-Null
    Copy-Item -Path (Join-Path $nativeDirectory "*") -Destination $testCrate -Recurse -Force
    Copy-Item -Path (Join-Path $fixtureDirectory "*") -Destination $testFixtures -Recurse -Force
    $testManifest = Join-Path $testCrate "Cargo.toml"
    # Registry manifests have explicit versions. Isolate this copied test crate
    # from the host workspace without changing the published Rust source.
    if ((Get-Content -LiteralPath $testManifest -Raw) -notmatch '(?m)^\[workspace\]') {
        Add-Content -LiteralPath $testManifest -Value "`n[workspace]" -Encoding utf8
    }
    return $testManifest
}
