[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Test
)

$ErrorActionPreference = "Stop"

$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
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
$cargoAction = if ($Test) { "test" } else { "build" }
$cargoCommand = "cargo $cargoAction --workspace --locked"
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
}
finally {
    Pop-Location
}
