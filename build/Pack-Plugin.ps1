<#
  Builds one plugin and packs it for the marketplace: <out>/<id>-<version>.zip holding plugin.json
  (with the version filled in) and the plugin's own DLLs. Prints the package's details as JSON (id,
  name, version, file, sha256, size), which publish.yml turns into the index entry.

    pwsh build/Pack-Plugin.ps1 -Folder plugins/HelloWorld [-Version 1.2.0] [-Out out] [-SdkVersion <DevHub Sdk version>]
#>
param(
    [Parameter(Mandatory)] [string]$Folder,
    [string]$Version,
    [string]$Out = "out",
    [string]$SdkVersion
)
$ErrorActionPreference = "Stop"

if (-not $Version) { $Version = & (Join-Path $PSScriptRoot "Get-PluginVersion.ps1") -Folder $Folder }
$info = Get-Content (Join-Path $Folder "plugin.json") -Raw | ConvertFrom-Json
$build = Join-Path $Out "build/$($info.id)"
Remove-Item -Recurse -Force $build -ErrorAction SilentlyContinue

$args = @("build", $Folder, "-c", "Release", "-o", $build, "-p:Version=$Version", "--nologo")
if ($SdkVersion) { $args += "-p:DevHubSdkVersion=$SdkVersion" }
dotnet @args
if ($LASTEXITCODE -ne 0) { throw "Build failed: $Folder" }

# The plugins this one requires (and what they require) that live in this repository: a ProjectReference
# to one copies its DLLs here, but its own package brings them. DevHub installs the requirement with it
# and loads its DLLs first, so they're left out of this package (one copy, the required plugin's own).
$root = Split-Path $PSScriptRoot
$folders = @{} # plugin id → folder (a PowerShell hashtable ignores case, like plugin ids)
foreach ($manifest in Get-ChildItem (Join-Path $root "plugins") -Filter plugin.json -Recurse -Depth 2 | Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" }) {
    $folders[(Get-Content $manifest.FullName -Raw | ConvertFrom-Json).id] = $manifest.Directory.FullName
}
$leftOut = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$queue = [Collections.Generic.Queue[string]]::new()
@($info.requires) | Where-Object { $_ } | ForEach-Object { $queue.Enqueue($_) }
while ($queue.Count -gt 0) {
    $id = $queue.Dequeue()
    if (-not $seen.Add($id) -or -not $folders[$id]) { continue } # built into DevHub, or from elsewhere
    $requiredBuild = Join-Path $Out "build/required/$id"
    Remove-Item -Recurse -Force $requiredBuild -ErrorAction SilentlyContinue
    $requiredArgs = @("build", $folders[$id], "-c", "Release", "-o", $requiredBuild, "--nologo")
    if ($SdkVersion) { $requiredArgs += "-p:DevHubSdkVersion=$SdkVersion" }
    dotnet @requiredArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($folders[$id]) (required by $($info.id))" }
    Get-ChildItem $requiredBuild -Filter *.dll | ForEach-Object { [void]$leftOut.Add($_.Name) }
    @((Get-Content (Join-Path $folders[$id] "plugin.json") -Raw | ConvertFrom-Json).requires) | Where-Object { $_ } | ForEach-Object { $queue.Enqueue($_) }
}

# plugin.json with the version (comments dropped: it's written by this script now).
$info | Add-Member -NotePropertyName version -NotePropertyValue $Version -Force
$info | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $build "plugin.json") -Encoding utf8

# Only what the plugin brings: its DLLs and the files it copies to its output, subfolders included (e.g.
# Templates' BuiltInTemplates\). DevHub's own assemblies are never copied (see Directory.Build.props); the
# required plugins' DLLs are left out (see above); symbols and .deps.json aren't needed.
$zip = Join-Path $Out "$($info.id)-$Version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
$buildRoot = (Resolve-Path $build).Path
$files = @(Get-ChildItem $buildRoot -File -Recurse | Where-Object {
    $_.Extension -ne ".pdb" -and $_.Name -notlike "*.deps.json" -and -not ($_.DirectoryName -eq $buildRoot -and $leftOut.Contains($_.Name)) })
$skipped = @(Get-ChildItem $buildRoot -Filter *.dll | Where-Object { $leftOut.Contains($_.Name) } | ForEach-Object Name)
if ($skipped.Count -gt 0) { Write-Host "Left out of $($info.id) (its required plugins bring them): $($skipped -join ', ')" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open((Join-Path (Resolve-Path $Out).Path (Split-Path $zip -Leaf)), [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $entry = [IO.Path]::GetRelativePath($buildRoot, $file.FullName).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry)
    }
} finally {
    $archive.Dispose()
}

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
