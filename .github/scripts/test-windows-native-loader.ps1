[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Directory,

    [string]$Msi
)

$ErrorActionPreference = "Stop"
$source = (Resolve-Path -LiteralPath $Directory).Path
$msiPath = if ($Msi) { (Resolve-Path -LiteralPath $Msi).Path } else { $null }
$vsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vsRoot = & $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $vsRoot) { throw "Visual Studio C++ tools are required for the loader test." }
$developerCommand = Join-Path $vsRoot "Common7\Tools\VsDevCmd.bat"
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("seiza-native-loader-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null
foreach ($name in @("seiza_cabi.dll", "seiza_platform.dll", "SeizaThumbnailProvider.dll")) {
    # Rust's output filename has underscores; the publish payload uses title case.
    $nativeInput = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $nativeInput) -and $name -eq "SeizaThumbnailProvider.dll") {
        $nativeInput = Join-Path $source "seiza_thumbnail_provider.dll"
    }
    Copy-Item -LiteralPath $nativeInput -Destination (Join-Path $testRoot $name)
}
$probeSource = Join-Path $PSScriptRoot "windows-native-loader-probe.cpp"
$controlSource = Join-Path $PSScriptRoot "windows-dynamic-runtime-control.cpp"
$command = "call `"$developerCommand`" -no_logo -arch=x64 -host_arch=x64 && cl.exe /nologo /W4 /WX /MT /EHsc `"$probeSource`" /Fe:loader-probe.exe msi.lib && cl.exe /nologo /W4 /WX /MD /EHsc /LD `"$controlSource`" /Fe:vc-dynamic-control.dll"
Push-Location $testRoot
try {
    & $env:ComSpec /d /s /c $command
    if ($LASTEXITCODE -ne 0) { throw "Native loader test compilation failed." }
    if (-not (Test-Path -LiteralPath (Join-Path $testRoot "vc-dynamic-control.dll") -PathType Leaf)) {
        throw "The dynamic-runtime negative control DLL was not built."
    }
    foreach ($name in @("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll")) {
        [System.IO.File]::WriteAllText((Join-Path $testRoot $name), "VC runtime resolution intentionally blocked")
    }
    if ($msiPath) { & (Join-Path $testRoot "loader-probe.exe") $msiPath }
    else { & (Join-Path $testRoot "loader-probe.exe") }
    if ($LASTEXITCODE -ne 0) { throw "Native loader test failed with exit code $LASTEXITCODE ($testRoot)." }
}
finally { Pop-Location }
