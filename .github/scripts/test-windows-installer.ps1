param(
    [Parameter(Mandatory = $true)]
    [string]$Msi
)

$ErrorActionPreference = "Stop"

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "The all-users MSI smoke test requires an elevated PowerShell session."
}

$Msi = (Resolve-Path -LiteralPath $Msi).Path
$tempDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$log = Join-Path $tempDirectory "seiza-app-msi-install.log"
$installDirectory = Join-Path $env:ProgramFiles "Seiza for Windows"
$installedApp = Join-Path $installDirectory "Seiza.App.exe"
$programMenuDirectory = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Seiza"
$shortcut = Join-Path $programMenuDirectory "Seiza.lnk"
$registeredApplications = "Registry::HKEY_LOCAL_MACHINE\Software\RegisteredApplications"
$fitsClass = "Registry::HKEY_LOCAL_MACHINE\Software\Classes\Seiza.FitsFile"
$xisfClass = "Registry::HKEY_LOCAL_MACHINE\Software\Classes\Seiza.XisfFile"
$installArguments = @(
    "/i",
    "`"$Msi`"",
    "/qn",
    "/norestart",
    "/l*v",
    "`"$log`""
)
$installed = $false
$appProcess = $null

try {
    $install = Start-Process msiexec.exe -ArgumentList $installArguments -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $log) {
            Get-Content -LiteralPath $log
        }
        throw "MSI install failed with exit code $($install.ExitCode)"
    }
    $installed = $true

    $requiredFiles = @(
        $installedApp,
        (Join-Path $installDirectory "seiza_cabi.dll"),
        (Join-Path $installDirectory "coreclr.dll"),
        (Join-Path $installDirectory "hostfxr.dll"),
        (Join-Path $installDirectory "Microsoft.WindowsAppRuntime.dll"),
        (Join-Path $installDirectory "Microsoft.WinUI.dll")
    )
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Installed runtime file not found at $requiredFile"
        }
    }

    if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) {
        throw "Start Menu shortcut not found at $shortcut"
    }

    $shortcutTarget = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut).TargetPath
    if ($shortcutTarget -ne $installedApp) {
        throw "Start Menu shortcut targets '$shortcutTarget' instead of '$installedApp'"
    }

    $registeredApp = (Get-ItemProperty -LiteralPath $registeredApplications -Name Seiza).Seiza
    if ($registeredApp -ne "Software\Seiza\Capabilities") {
        throw "Seiza is not registered with Windows Default Apps"
    }
    if (-not (Test-Path -LiteralPath $fitsClass)) {
        throw "FITS file class was not installed"
    }
    if (-not (Test-Path -LiteralPath $xisfClass)) {
        throw "XISF file class was not installed"
    }

    $appProcess = Start-Process -FilePath $installedApp -PassThru
    Start-Sleep -Seconds 3
    if ($appProcess.HasExited) {
        throw "Installed Seiza app exited with code $($appProcess.ExitCode)"
    }
}
finally {
    if ($null -ne $appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force
        $appProcess.WaitForExit()
    }

    if ($installed) {
        $uninstall = Start-Process msiexec.exe -ArgumentList "/x", "`"$Msi`"", "/qn", "/norestart" -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) {
            throw "MSI uninstall failed with exit code $($uninstall.ExitCode)"
        }
        if (Test-Path -LiteralPath $installedApp) {
            throw "MSI uninstall left $installedApp behind"
        }
        if (Test-Path -LiteralPath $shortcut) {
            throw "MSI uninstall left $shortcut behind"
        }
        if (Test-Path -LiteralPath $fitsClass) {
            throw "MSI uninstall left the FITS file class behind"
        }
        if (Test-Path -LiteralPath $xisfClass) {
            throw "MSI uninstall left the XISF file class behind"
        }
    }
}

Write-Output "All-users MSI install, runtime, launch, integration, and uninstall checks passed."
