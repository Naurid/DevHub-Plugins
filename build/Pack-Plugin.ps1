<#
  Builds one plugin and packs it for the marketplace: <out>/<id>-<version>.zip holding plugin.json
  (with the version filled in) and the plugin's own DLLs. Prints the package's details as JSON (id,
  name, version, file, sha256, size), which publish.yml turns into the index entry.

    pwsh build/Pack-Plugin.ps1 -Folder plugins/HelloWorld [-Version 1.2.0] [-Out out] [-SdkDir <path>]
#>
param(
    [Parameter(Mandatory)] [string]$Folder,
    [string]$Version,
    [string]$Out = "out",
    [string]$SdkDir
)
$ErrorActionPreference = "Stop"

if (-not $Version) { $Version = & (Join-Path $PSScriptRoot "Get-PluginVersion.ps1") -Folder $Folder }
$info = Get-Content (Join-Path $Folder "plugin.json") -Raw | ConvertFrom-Json
$build = Join-Path $Out "build/$($info.id)"
Remove-Item -Recurse -Force $build -ErrorAction SilentlyContinue

$args = @("build", $Folder, "-c", "Release", "-o", $build, "-p:Version=$Version", "--nologo")
if ($SdkDir) { $args += "-p:DevHubSdkDir=$SdkDir" }
dotnet @args
if ($LASTEXITCODE -ne 0) { throw "Build failed: $Folder" }

# plugin.json with the version (comments dropped: it's written by this script now).
$info | Add-Member -NotePropertyName version -NotePropertyValue $Version -Force
$info | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $build "plugin.json") -Encoding utf8

# Only what the plugin brings: its DLLs (DevHub's own assemblies are never copied, see Directory.Build.props).
$zip = Join-Path $Out "$($info.id)-$Version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
$files = @(Get-ChildItem $build -File | Where-Object { $_.Extension -in ".dll", ".json" -and $_.Name -notlike "*.deps.json" })
Compress-Archive -Path $files.FullName -DestinationPath $zip

[ordered]@{
    id          = $info.id
    name        = $info.name
    description = $info.description
    category    = $info.category
    version     = $Version
    apiVersion  = $info.apiVersion
    requires    = @($info.requires)
    file        = (Resolve-Path $zip).Path
    sha256      = (Get-FileHash $zip -Algorithm SHA256).Hash
    size        = (Get-Item $zip).Length
} | ConvertTo-Json -Compress
