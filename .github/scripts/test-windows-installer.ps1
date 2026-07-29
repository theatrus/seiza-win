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
$thumbnailProviderId = "{E8D56C6C-4E30-4C89-889A-D022180B710A}"
$thumbnailHandlerId = "{E357FCCD-A995-4576-B01F-234630154E96}"
$thumbnailClass = "Registry::HKEY_LOCAL_MACHINE\Software\Classes\CLSID\$thumbnailProviderId"
$thumbnailHandlers = @(
    ".fit",
    ".fits",
    ".fts",
    ".xisf"
) | ForEach-Object {
    "Registry::HKEY_LOCAL_MACHINE\Software\Classes\SystemFileAssociations\$_\shellex\$thumbnailHandlerId"
}
$previewProviderId = "{47B9C88E-38F5-4DE8-9A33-25E3989A7C51}"
$previewHandlerId = "{8895B1C6-B41F-4C1C-A562-0D564250836F}"
$previewHostAppId = "{6D2B5079-2F0B-48DD-AB7F-97CEC514D30B}"
$previewClass = "Registry::HKEY_LOCAL_MACHINE\Software\Classes\CLSID\$previewProviderId"
$previewHandlersList = "Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\PreviewHandlers"
$previewHandlers = @(
    ".fit",
    ".fits",
    ".fts",
    ".xisf"
) | ForEach-Object {
    "Registry::HKEY_LOCAL_MACHINE\Software\Classes\SystemFileAssociations\$_\shellex\$previewHandlerId"
}
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
        (Join-Path $installDirectory "SeizaThumbnailProvider.dll"),
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
    if (-not (Test-Path -LiteralPath $thumbnailClass)) {
        throw "Explorer thumbnail provider COM class was not installed"
    }
    $registeredThumbnailDll = (Get-Item -LiteralPath (Join-Path $thumbnailClass "InprocServer32")).GetValue("")
    if ($registeredThumbnailDll -ne (Join-Path $installDirectory "SeizaThumbnailProvider.dll")) {
        throw "Explorer thumbnail provider points to '$registeredThumbnailDll'"
    }
    foreach ($thumbnailHandler in $thumbnailHandlers) {
        $registeredHandler = (Get-Item -LiteralPath $thumbnailHandler).GetValue("")
        if ($registeredHandler -ne $thumbnailProviderId) {
            throw "Thumbnail handler '$thumbnailHandler' points to '$registeredHandler'"
        }
    }
    if (-not (Test-Path -LiteralPath $previewClass)) {
        throw "Explorer Preview Pane provider COM class was not installed"
    }
    $registeredPreviewDll = (Get-Item -LiteralPath (Join-Path $previewClass "InprocServer32")).GetValue("")
    if ($registeredPreviewDll -ne (Join-Path $installDirectory "SeizaThumbnailProvider.dll")) {
        throw "Explorer Preview Pane provider points to '$registeredPreviewDll'"
    }
    $registeredPreviewAppId = (Get-Item -LiteralPath $previewClass).GetValue("AppID")
    if ($registeredPreviewAppId -ne $previewHostAppId) {
        throw "Explorer Preview Pane provider uses AppID '$registeredPreviewAppId'"
    }
    $listedPreviewProvider = (Get-Item -LiteralPath $previewHandlersList).GetValue($previewProviderId)
    if ($listedPreviewProvider -ne "Seiza FITS and XISF Preview Handler") {
        throw "Explorer Preview Pane provider is missing from the global handler list"
    }
    foreach ($previewHandler in $previewHandlers) {
        $registeredHandler = (Get-Item -LiteralPath $previewHandler).GetValue("")
        if ($registeredHandler -ne $previewProviderId) {
            throw "Preview handler '$previewHandler' points to '$registeredHandler'"
        }
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
        if (Test-Path -LiteralPath $thumbnailClass) {
            throw "MSI uninstall left the thumbnail provider COM class behind"
        }
        foreach ($thumbnailHandler in $thumbnailHandlers) {
            if (Test-Path -LiteralPath $thumbnailHandler) {
                throw "MSI uninstall left '$thumbnailHandler' behind"
            }
        }
        if (Test-Path -LiteralPath $previewClass) {
            throw "MSI uninstall left the Preview Pane provider COM class behind"
        }
        $listedPreviewProvider = (Get-Item -LiteralPath $previewHandlersList).GetValue($previewProviderId)
        if ($null -ne $listedPreviewProvider) {
            throw "MSI uninstall left the Preview Pane provider in the global handler list"
        }
        foreach ($previewHandler in $previewHandlers) {
            if (Test-Path -LiteralPath $previewHandler) {
                throw "MSI uninstall left '$previewHandler' behind"
            }
        }
    }
}

Write-Output "All-users MSI install, runtime, launch, integration, and uninstall checks passed."
