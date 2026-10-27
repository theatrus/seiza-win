[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$name = "seiza-analysis-portable-win-x64-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$output = Join-Path $root "dist\$name"

& dotnet publish (Join-Path $root "src\Seiza.App\Seiza.App.csproj") `
    -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:PublishTrimmed=false `
    -p:DebugSymbols=false -p:DebugType=None -o $output
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed ($LASTEXITCODE)." }

Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $output
Copy-Item -LiteralPath (Join-Path $root "rust\seiza-analysis\UPSTREAM.md") -Destination (Join-Path $output "ANALYSIS-PATCH.md")
Copy-Item -LiteralPath (Join-Path $root "docs\ANALYSIS-PORTABLE.txt") -Destination (Join-Path $output "README-PORTABLE.txt")
Compress-Archive -Path "$output\*" -DestinationPath "$output.zip"
$hash = (Get-FileHash -LiteralPath "$output.zip" -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content -LiteralPath "$output.zip.sha256" -Encoding ascii
Write-Output "Portable build: $output.zip"
