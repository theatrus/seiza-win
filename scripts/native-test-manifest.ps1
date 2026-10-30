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
    $fixtureArchive = $null
    if ($calibrationVcs.git.sha1 -ne $SourceRevision) {
        # An unchanged leaf is not republished for every C ABI release. In that
        # case use the exact registry archive in the C ABI's packaged lockfile,
        # verified before extraction, rather than trust a different checkout.
        $lockPath = Join-Path $nativeDirectory "Cargo.lock"
        if (-not (Test-Path -LiteralPath $lockPath)) {
            throw "Calibration test fixtures must come from the C ABI's exact published source revision or its locked calibration archive."
        }
        $lock = Get-Content -LiteralPath $lockPath -Raw
        $package = [regex]::Match($lock, '(?ms)^\[\[package\]\]\r?\nname = "seiza-calibration"\r?\n(?<body>.*?)(?=^\[\[package\]\]|\z)').Groups['body'].Value
        $version = [regex]::Match($package, '(?m)^version = "([^"]+)"').Groups[1].Value
        $checksum = [regex]::Match($package, '(?m)^checksum = "([0-9a-f]{64})"').Groups[1].Value
        $manifestVersion = [regex]::Match((Get-Content -LiteralPath $CalibrationManifest -Raw), '(?m)^version = "([^"]+)"').Groups[1].Value
        if (-not $version -or $version -ne $manifestVersion -or -not $checksum -or
            $package -notmatch '(?m)^source = "registry\+https://github.com/rust-lang/crates.io-index"') {
            throw "Calibration fixture version/source must match the C ABI's locked registry archive."
        }
        $registrySource = Split-Path -Parent $calibrationDirectory
        $registryRoot = Split-Path -Parent (Split-Path -Parent $registrySource)
        $archiveName = "seiza-calibration-$version"
        $fixtureArchive = Join-Path $registryRoot "cache\$(Split-Path -Leaf $registrySource)\$archiveName.crate"
        if (-not (Test-Path -LiteralPath $fixtureArchive -PathType Leaf)) {
            throw "The locked calibration archive is missing. Run cargo fetch --locked before native tests."
        }
        if ((Get-FileHash -LiteralPath $fixtureArchive -Algorithm SHA256).Hash -ne $checksum) {
            throw "The calibration archive checksum does not match the C ABI's lockfile."
        }
    }

    $testRoot = Join-Path $TargetDirectory "published-tests\$SourceRevision"
    $testCrate = Join-Path $testRoot "seiza-cabi"
    $testFixtures = Join-Path $testRoot "seiza-calibration\tests\fixtures"
    New-Item -ItemType Directory -Force -Path $testCrate, $testFixtures | Out-Null
    Copy-Item -Path (Join-Path $nativeDirectory "*") -Destination $testCrate -Recurse -Force
    if ($fixtureArchive) {
        $stream = [System.IO.File]::OpenRead($fixtureArchive)
        $gzip = [System.IO.Compression.GZipStream]::new($stream, [System.IO.Compression.CompressionMode]::Decompress)
        $tar = [System.Formats.Tar.TarReader]::new($gzip)
        try {
            $prefix = "$archiveName/tests/fixtures/"
            $fixtureRoot = [System.IO.Path]::GetFullPath($testFixtures).TrimEnd('\') + '\'
            $extracted = 0
            while ($entry = $tar.GetNextEntry()) {
                if (-not $entry.Name.StartsWith($prefix, [System.StringComparison]::Ordinal)) { continue }
                if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) { continue }
                if ($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile)) {
                    throw "Calibration fixture archives must contain regular files, not links."
                }
                $destination = [System.IO.Path]::GetFullPath((Join-Path $testFixtures $entry.Name.Substring($prefix.Length)))
                if (-not $destination.StartsWith($fixtureRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "A calibration fixture archive path escapes the staging directory."
                }
                [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
                $output = [System.IO.File]::Create($destination)
                try { $entry.DataStream.CopyTo($output) } finally { $output.Dispose() }
                $extracted++
            }
            if ($extracted -eq 0) { throw "The locked calibration archive contains no fixtures." }
        }
        finally { $tar.Dispose(); $gzip.Dispose(); $stream.Dispose() }
    } else {
        Copy-Item -Path (Join-Path $fixtureDirectory "*") -Destination $testFixtures -Recurse -Force
    }
    $testManifest = Join-Path $testCrate "Cargo.toml"
    # Registry manifests have explicit versions. Isolate this copied test crate
    # from the host workspace without changing the published Rust source.
    if ((Get-Content -LiteralPath $testManifest -Raw) -notmatch '(?m)^\[workspace\]') {
        Add-Content -LiteralPath $testManifest -Value "`n[workspace]" -Encoding utf8
    }
    return $testManifest
}
