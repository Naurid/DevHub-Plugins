<#
  Puts DevHub's Sdk (DevHub.Sdk.dll + DevHub.Sdk.Core.dll) into .sdk/ for building plugins: from the
  DevHub-Sdk-<version>.zip asset of a DevHub release (public, no login needed).

    pwsh build/Get-Sdk.ps1              # the latest DevHub release
    pwsh build/Get-Sdk.ps1 -Version 1.4.0
#>
param(
    [string]$Version,
    [string]$Repository = "Naurid/DevHub",
    [string]$Out = (Join-Path $PSScriptRoot "../.sdk")
)
$ErrorActionPreference = "Stop"

$api = "https://api.github.com/repos/$Repository/releases"
$release = if ($Version) { Invoke-RestMethod "$api/tags/$([uri]::EscapeDataString("release/$Version"))" } else { Invoke-RestMethod "$api/latest" }
$asset = $release.assets | Where-Object { $_.name -like "DevHub-Sdk-*.zip" } | Select-Object -First 1
if (-not $asset) { throw "Release $($release.tag_name) has no DevHub-Sdk-*.zip." }

$zip = Join-Path ([IO.Path]::GetTempPath()) $asset.name
Invoke-WebRequest $asset.browser_download_url -OutFile $zip
Remove-Item -Recurse -Force $Out -ErrorAction SilentlyContinue
Expand-Archive $zip -DestinationPath $Out
Remove-Item $zip
Write-Output "DevHub Sdk from $($release.tag_name) in $((Resolve-Path $Out).Path)"
