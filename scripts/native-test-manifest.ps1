function Copy-SeizaNativeTestFixtures {
    param(
        [string]$NativeDirectory,
        [string]$FixturePackage,
        [string]$FixtureManifest,
        [string]$SourceRevision,
        [string]$TestFixtures
    )

    $fixturePackageDirectory = Split-Path -Parent $FixtureManifest
    $fixtureDirectory = Join-Path $fixturePackageDirectory "tests\fixtures"
    if (-not (Test-Path -LiteralPath $fixtureDirectory)) {
        throw "The published fixture archive does not contain required native test fixtures."
    }
    $fixtureVcs = Get-Content -LiteralPath (Join-Path $fixturePackageDirectory ".cargo_vcs_info.json") -Raw | ConvertFrom-Json
    $fixtureArchive = $null
    if ($fixtureVcs.git.sha1 -ne $SourceRevision) {
        # An unchanged leaf is not republished for every C ABI release. In that
        # case use the exact registry archive in the C ABI's packaged lockfile,
        # verified before extraction, rather than trust a different checkout.
        $lockPath = Join-Path $NativeDirectory "Cargo.lock"
        if (-not (Test-Path -LiteralPath $lockPath)) {
            throw "Test fixtures must come from the C ABI's exact published source revision or its locked fixture archive."
        }
        $lock = Get-Content -LiteralPath $lockPath -Raw
        $packagePattern = '(?ms)^\[\[package\]\]\r?\nname = "{0}"\r?\n(?<body>.*?)(?=^\[\[package\]\]|\z)' -f [regex]::Escape($FixturePackage)
        $package = [regex]::Match($lock, $packagePattern).Groups['body'].Value
        $version = [regex]::Match($package, '(?m)^version = "([^"]+)"').Groups[1].Value
        $checksum = [regex]::Match($package, '(?m)^checksum = "([0-9a-f]{64})"').Groups[1].Value
        $manifestVersion = [regex]::Match((Get-Content -LiteralPath $FixtureManifest -Raw), '(?m)^version = "([^"]+)"').Groups[1].Value
        if (-not $version -or $version -ne $manifestVersion -or -not $checksum -or
            $package -notmatch '(?m)^source = "registry\+https://github.com/rust-lang/crates.io-index"') {
            throw "Fixture version/source must match the C ABI's locked registry archive."
        }
        $registrySource = Split-Path -Parent $fixturePackageDirectory
        $registryRoot = Split-Path -Parent (Split-Path -Parent $registrySource)
        $archiveName = "$FixturePackage-$version"
        $fixtureArchive = Join-Path $registryRoot "cache\$(Split-Path -Leaf $registrySource)\$archiveName.crate"
        if (-not (Test-Path -LiteralPath $fixtureArchive -PathType Leaf)) {
            throw "The locked fixture archive is missing. Run cargo fetch --locked before native tests."
        }
        if ((Get-FileHash -LiteralPath $fixtureArchive -Algorithm SHA256).Hash -ne $checksum) {
            throw "The fixture archive checksum does not match the C ABI's lockfile."
        }
    }

    New-Item -ItemType Directory -Force -Path $TestFixtures | Out-Null
    if ($fixtureArchive) {
        $stream = [System.IO.File]::OpenRead($fixtureArchive)
        $gzip = [System.IO.Compression.GZipStream]::new($stream, [System.IO.Compression.CompressionMode]::Decompress)
        $tar = [System.Formats.Tar.TarReader]::new($gzip)
        try {
            $prefix = "$archiveName/tests/fixtures/"
            $fixtureRoot = [System.IO.Path]::GetFullPath($TestFixtures).TrimEnd('\') + '\'
            $extracted = 0
            while ($entry = $tar.GetNextEntry()) {
                if (-not $entry.Name.StartsWith($prefix, [System.StringComparison]::Ordinal)) { continue }
                if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) { continue }
                if ($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile)) {
                    throw "Fixture archives must contain regular files, not links."
                }
                $destination = [System.IO.Path]::GetFullPath((Join-Path $TestFixtures $entry.Name.Substring($prefix.Length)))
                if (-not $destination.StartsWith($fixtureRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "A calibration fixture archive path escapes the staging directory."
                }
                [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
                $output = [System.IO.File]::Create($destination)
                try { if ($entry.DataStream) { $entry.DataStream.CopyTo($output) } } finally { $output.Dispose() }
                $extracted++
            }
            if ($extracted -eq 0) { throw "The locked fixture archive contains no fixtures." }
        }
        finally { $tar.Dispose(); $gzip.Dispose(); $stream.Dispose() }
    } else {
        Copy-Item -Path (Join-Path $fixtureDirectory "*") -Destination $TestFixtures -Recurse -Force
    }
}

function Get-SeizaNativeTestManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$NativeManifest,
        [Parameter(Mandatory)][string]$CalibrationManifest,
        [Parameter(Mandatory)][string]$TargetDirectory,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceRevision,
        [string]$ModelManifest
    )

    $nativeDirectory = Split-Path -Parent $NativeManifest
    $fixtureManifests = [ordered]@{
        'seiza-calibration' = $CalibrationManifest
        'seiza-model' = $ModelManifest
    }
    $fixturePatterns = @($fixtureManifests.Keys | ForEach-Object { "../$_/tests/fixtures" })
    $fixtureReferences = @(Get-ChildItem -LiteralPath (Join-Path $nativeDirectory "src") -Recurse -Filter *.rs |
        Select-String -SimpleMatch -Pattern $fixturePatterns)
    if ($fixtureReferences.Count -eq 0) {
        return $NativeManifest
    }

    $testRoot = Join-Path $TargetDirectory "published-tests\$SourceRevision"
    $testCrate = Join-Path $testRoot "seiza-cabi"
    foreach ($package in $fixtureManifests.Keys) {
        if ($fixtureReferences.Pattern -notcontains "../$package/tests/fixtures") { continue }
        $fixtureManifest = $fixtureManifests[$package]
        if (-not $fixtureManifest) {
            throw "The published $package manifest is required for native test fixtures."
        }
        Copy-SeizaNativeTestFixtures -NativeDirectory $nativeDirectory -FixturePackage $package `
            -FixtureManifest $fixtureManifest -SourceRevision $SourceRevision `
            -TestFixtures (Join-Path $testRoot "$package\tests\fixtures")
    }
    New-Item -ItemType Directory -Force -Path $testCrate | Out-Null
    Copy-Item -Path (Join-Path $nativeDirectory "*") -Destination $testCrate -Recurse -Force
    $testManifest = Join-Path $testCrate "Cargo.toml"
    # Registry manifests have explicit versions. Isolate this copied test crate
    # from the host workspace without changing the published Rust source.
    if ((Get-Content -LiteralPath $testManifest -Raw) -notmatch '(?m)^\[workspace\]') {
        Add-Content -LiteralPath $testManifest -Value "`n[workspace]" -Encoding utf8
    }
    return $testManifest
}
