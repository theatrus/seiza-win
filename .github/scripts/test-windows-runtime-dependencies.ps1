[CmdletBinding(DefaultParameterSetName = "Directory")]
param(
    [Parameter(Mandatory, ParameterSetName = "Directory")]
    [string]$Directory,

    [Parameter(Mandatory, ParameterSetName = "Files")]
    [string[]]$Path
)

$ErrorActionPreference = "Stop"
$vsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vsRoot = & $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $vsRoot) { throw "Visual Studio C++ tools are required for the runtime dependency audit." }
$versionFile = Join-Path $vsRoot "VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt"
$toolsVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
$dumpbin = Join-Path $vsRoot "VC\Tools\MSVC\$toolsVersion\bin\Hostx64\x64\dumpbin.exe"

$files = if ($PSCmdlet.ParameterSetName -eq "Directory") {
    $root = Get-Item -LiteralPath $Directory
    if (-not $root.PSIsContainer) { throw "Runtime audit requires a directory: $Directory" }
    @(Get-ChildItem -LiteralPath $root.FullName -Recurse -File | Where-Object { $_.Extension -in ".dll", ".exe" })
}
else {
    @($Path | ForEach-Object { Get-Item -LiteralPath $_ })
}
if ($files.Count -eq 0) { throw "No PE binaries were supplied to the runtime dependency audit." }

$failures = @()
foreach ($file in $files) {
    $report = & $dumpbin /NOLOGO /DEPENDENTS $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect PE dependencies of $($file.FullName)." }
    # /DEPENDENTS includes normal and delay-load imports. UCRT API sets and
    # unversioned msvcrt.dll are Windows components; versioned VC CRTs are not.
    $imports = @($report | Select-String -Pattern '^\s+((?:vcruntime|msvcp|msvcr|concrt|vcomp)\d[^\s]*\.dll)\s*$' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique)
    if ($imports.Count -gt 0) {
        $failures += "$($file.Name): $($imports -join ', ')"
    }
}
if ($failures.Count -gt 0) {
    throw "External Visual C++ runtime dependencies are forbidden in the self-contained installer. Rebuild with the static CRT and check RUSTFLAGS overrides. $($failures -join '; ')"
}
Write-Output "Runtime dependency audit passed: $($files.Count) binaries require no separate Visual C++ runtime."
