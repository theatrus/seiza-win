$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "native-test-manifest.ps1")

$testDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("SeizaNativeManifest-" + [guid]::NewGuid().ToString("N"))
$nativeDirectory = Join-Path $testDirectory "registry\seiza-cabi"
$calibrationDirectory = Join-Path $testDirectory "registry\seiza-calibration"
$nativeManifest = Join-Path $nativeDirectory "Cargo.toml"
$calibrationManifest = Join-Path $calibrationDirectory "Cargo.toml"
$sourceRevision = "0123456789abcdef0123456789abcdef01234567"
New-Item -ItemType Directory -Force -Path (Join-Path $nativeDirectory "src"), (Join-Path $calibrationDirectory "tests\fixtures\library") | Out-Null
try {
    Set-Content -LiteralPath $nativeManifest -Value "[package]`nname = 'seiza-cabi'`nversion = '0.47.0'"
    Set-Content -LiteralPath $calibrationManifest -Value "[package]`nname = 'seiza-calibration'`nversion = '0.11.0'"
    $vcsPath = Join-Path $calibrationDirectory ".cargo_vcs_info.json"
    Set-Content -LiteralPath $vcsPath -Value (@{ git = @{ sha1 = $sourceRevision } } | ConvertTo-Json)
    $fixture = Join-Path $calibrationDirectory "tests\fixtures\library\recipes.json"
    Set-Content -LiteralPath $fixture -Value '{"capture":"preserve these bytes"}'
    Set-Content -LiteralPath (Join-Path $nativeDirectory "src\a_no_match.rs") -Value "pub fn ordinary() {}"

    $originalManifest = [System.IO.File]::ReadAllBytes($nativeManifest)
    $targetDirectory = Join-Path $testDirectory "fresh-target"
    $arguments = @{ NativeManifest = $nativeManifest; CalibrationManifest = $calibrationManifest; TargetDirectory = $targetDirectory; SourceRevision = $sourceRevision }
    if ((Get-SeizaNativeTestManifest @arguments) -ne $nativeManifest -or (Test-Path -LiteralPath $targetDirectory)) {
        throw "A self-contained archive must remain untouched and need no staging."
    }

    $matchingSource = Join-Path $nativeDirectory "src\z_fixture.rs"
    Set-Content -LiteralPath $matchingSource -Value 'include_str!("../../seiza-calibration/tests/fixtures/library/recipes.json");'
    $stagedManifest = Get-SeizaNativeTestManifest @arguments
    if ($stagedManifest -eq $nativeManifest) { throw "A reference after an unmatched first file must stage the crate." }
    $stagedFixture = Join-Path (Split-Path -Parent (Split-Path -Parent $stagedManifest)) "seiza-calibration\tests\fixtures\library\recipes.json"
    if ((Get-FileHash -LiteralPath $fixture).Hash -ne (Get-FileHash -LiteralPath $stagedFixture).Hash) { throw "Fixture bytes changed." }
    if ((Get-FileHash -LiteralPath $matchingSource).Hash -ne (Get-FileHash -LiteralPath (Join-Path (Split-Path $stagedManifest) "src\z_fixture.rs")).Hash) { throw "Published source changed." }
    if ([System.Convert]::ToHexString($originalManifest) -ne [System.Convert]::ToHexString([System.IO.File]::ReadAllBytes($nativeManifest))) { throw "Registry manifest was modified." }
    if ((Get-Content -LiteralPath $stagedManifest -Raw) -notmatch '(?m)^\[workspace\]') { throw "Copied manifest must be isolated." }

    # Repeat with an existing stage: no nested copies or duplicated tables.
    $again = Get-SeizaNativeTestManifest @arguments
    if ($again -ne $stagedManifest -or @(Select-String -LiteralPath $again -Pattern '^\[workspace\]').Count -ne 1) { throw "Repeated staging is not stable." }
    Set-Content -LiteralPath $vcsPath -Value '{"git":{"sha1":"ffffffffffffffffffffffffffffffffffffffff"}}'
    $refusedMismatch = $false
    try { Get-SeizaNativeTestManifest @arguments | Out-Null } catch { $refusedMismatch = $_.Exception.Message -like '*exact published source revision*' }
    if (-not $refusedMismatch) { throw "Mismatched fixture revisions must be refused." }
    Write-Host "Native test manifest regression checks passed."
}
finally {
    # Only the explicit, newly-created test directory may be removed.
    $resolvedTestDirectory = [System.IO.Path]::GetFullPath($testDirectory)
    $expectedParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedTestDirectory.StartsWith($expectedParent, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $resolvedTestDirectory).StartsWith('SeizaNativeManifest-')) { throw "Unexpected test cleanup path." }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}
