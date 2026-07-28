param(
    [Parameter(Mandatory = $true)]
    [string]$Msi
)

$ErrorActionPreference = "Stop"

$Msi = (Resolve-Path -LiteralPath $Msi).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember(
    "OpenDatabase",
    "InvokeMethod",
    $null,
    $installer,
    @($Msi, 0)
)

function Open-MsiView([string]$Query) {
    $Query = ($Query -replace "\s+", " ").Trim()
    $view = $database.GetType().InvokeMember(
        "OpenView",
        "InvokeMethod",
        $null,
        $database,
        @($Query)
    )
    $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null
    return $view
}

function Fetch-MsiRecord($View) {
    return $View.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $View, $null)
}

$launchView = Open-MsiView @'
SELECT `Action`, `Type`, `Source`, `Target`
FROM `CustomAction`
WHERE `Action`='LaunchSeiza'
'@
$launch = Fetch-MsiRecord $launchView
if ($null -eq $launch) {
    throw "LaunchSeiza custom action is missing"
}
if ($launch.IntegerData(2) -ne 1 -or
    $launch.StringData(3) -ne "Wix4UtilCA_X64" -or
    $launch.StringData(4) -ne "WixUnelevatedShellExec") {
    throw "LaunchSeiza does not use the x64 WiX unelevated shell action"
}

$targetView = Open-MsiView @'
SELECT `Action`, `Type`, `Source`, `Target`
FROM `CustomAction`
WHERE `Action`='SetLaunchSeizaTarget'
'@
$target = Fetch-MsiRecord $targetView
if ($null -eq $target) {
    throw "SetLaunchSeizaTarget custom action is missing"
}
if ($target.IntegerData(2) -ne 51 -or
    $target.StringData(3) -ne "WixUnelevatedShellExecTarget" -or
    $target.StringData(4) -ne "[APPLICATIONFOLDER]Seiza.App.exe") {
    throw "SetLaunchSeizaTarget does not resolve the installed application path"
}

$eventsView = Open-MsiView @'
SELECT `Event`, `Argument`, `Condition`, `Ordering`
FROM `ControlEvent`
WHERE `Dialog_`='ExitDialog' AND `Control_`='Finish'
ORDER BY `Ordering`
'@
$events = @()
while ($record = Fetch-MsiRecord $eventsView) {
    $events += [pscustomobject]@{
        Event = $record.StringData(1)
        Argument = $record.StringData(2)
        Condition = $record.StringData(3)
        Ordering = $record.IntegerData(4)
    }
}

$launchCondition = "WIXUI_EXITDIALOGOPTIONALCHECKBOX = 1 AND NOT Installed"
if ($events.Count -ne 3 -or
    $events[0].Event -ne "DoAction" -or
    $events[0].Argument -ne "SetLaunchSeizaTarget" -or
    $events[0].Condition -ne $launchCondition -or
    $events[0].Ordering -ne 1 -or
    $events[1].Event -ne "DoAction" -or
    $events[1].Argument -ne "LaunchSeiza" -or
    $events[1].Condition -ne $launchCondition -or
    $events[1].Ordering -ne 2 -or
    $events[2].Event -ne "EndDialog" -or
    $events[2].Argument -ne "Return" -or
    $events[2].Ordering -ne 999) {
    throw "ExitDialog Finish events do not resolve, launch, and then close in the required order"
}

$notifyView = Open-MsiView @'
SELECT `Action`, `Type`, `Source`, `Target`
FROM `CustomAction`
WHERE `Action`='NotifyShellAssociations'
'@
$notify = Fetch-MsiRecord $notifyView
if ($null -eq $notify) {
    throw "NotifyShellAssociations custom action is missing"
}
if ($notify.IntegerData(2) -ne 1 -or
    $notify.StringData(3) -ne "SeizaThumbnailProviderCustomActions" -or
    $notify.StringData(4) -ne "NotifyShellAssociations") {
    throw "NotifyShellAssociations does not call the embedded native provider DLL"
}

$notifySequenceView = Open-MsiView @'
SELECT `Action`, `Condition`, `Sequence`
FROM `InstallExecuteSequence`
WHERE `Action`='NotifyShellAssociations'
'@
$notifySequence = Fetch-MsiRecord $notifySequenceView
if ($null -eq $notifySequence) {
    throw "NotifyShellAssociations is missing from InstallExecuteSequence"
}
if ($notifySequence.IntegerData(3) -le 6600) {
    throw "NotifyShellAssociations must run after InstallFinalize"
}

Write-Output "Installer Finish launch and Shell-association notification actions are correctly sequenced."
