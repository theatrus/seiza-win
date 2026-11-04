param(
    [Parameter(Mandatory = $true)]
    [string]$Msi,
    [string]$BlockedMsi
)

$ErrorActionPreference = "Stop"
$Msi = (Resolve-Path -LiteralPath $Msi).Path
$productCode = "{AD1A4DA0-DAF4-42B5-B989-332A9AB182DC}"
$uninstallKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$productCode"
$markerKey = "HKLM:\Software\SeizaUpgradeTest"
$appPath = Join-Path $env:ProgramFiles "Seiza for Windows\Seiza.App.exe"
if ((Test-Path -LiteralPath $appPath) -or (Test-Path -LiteralPath $markerKey)) {
    throw "Upgrade regression test requires a clean machine with no Seiza installation."
}

if (-not $BlockedMsi) {
    $fixture = Join-Path $PSScriptRoot "fixtures\blocked-upgrade\BlockedUpgrade.wixproj"
    & dotnet build $fixture -c Release
    if ($LASTEXITCODE -ne 0) { throw "Could not build blocked-uninstall fixture." }
    $BlockedMsi = Join-Path $PSScriptRoot "fixtures\blocked-upgrade\bin\Release\BlockedUpgrade.msi"
}
$BlockedMsi = (Resolve-Path -LiteralPath $BlockedMsi).Path
$logDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }

function Invoke-TestMsi([string[]]$Arguments) {
    $process = Start-Process msiexec.exe -WindowStyle Hidden -ArgumentList $Arguments -Wait -PassThru
    return $process.ExitCode
}

try {
    $code = Invoke-TestMsi -Arguments @('/i', "`"$BlockedMsi`"", '/qn', '/norestart')
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $uninstallKey)) {
        throw "Could not install blocked-uninstall fixture: $code"
    }
    $log = Join-Path $logDirectory "seiza-blocked-upgrade.log"
    $code = Invoke-TestMsi -Arguments @('/i', "`"$Msi`"", '/qn', '/norestart', '/l*v', "`"$log`"")
    if ($code -ne 1603 -or -not (Test-Path -LiteralPath $uninstallKey) -or
        -not (Test-Path -LiteralPath $markerKey) -or (Test-Path -LiteralPath $appPath)) {
        throw "Upgrade must fail before installing files when the old product remains: $code"
    }
    $text = Get-Content -LiteralPath $log -Raw
    if ($text -notmatch 'VerifyPreviousProductsRemoved\. Return value 3') {
        throw "Expected the checked removal guard to reject the still-installed product."
    }
    Write-Output "Upgrade correctly rejected a genuine uninstall failure and preserved the old product."
}
finally {
    if (Test-Path -LiteralPath $uninstallKey) {
        $code = Invoke-TestMsi -Arguments @('/x', $productCode, 'SEIZA_TEST_ALLOW_REMOVE=1', '/qn', '/norestart')
        if ($code -ne 0) { throw "Could not clean up test fixture: $code" }
    }
}
