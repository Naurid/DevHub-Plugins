<#
  Publishes every plugin whose version changed (run by .github/workflows/publish.yml):
  for each plugins/<Folder>/plugin.json, the version comes from git (Get-PluginVersion.ps1). If the
  release "<id>-<version>" doesn't exist yet, the plugin is built and packed, released with its zip,
  and its entry in the marketplace index (index.json on the "index" release) is replaced.
  Needs GH_TOKEN (contents: write); the DevHub Sdk comes from nuget.org (Directory.Build.props).
#>
param([string]$Repository = $env:GITHUB_REPOSITORY)
$ErrorActionPreference = "Stop"
if (-not $Repository) { throw "Set GITHUB_REPOSITORY (owner/name)." }
$root = Split-Path $PSScriptRoot
$out = Join-Path $root "out"

# The current index (empty the very first time).
$index = @()
gh release view index --repo $Repository *> $null
if ($LASTEXITCODE -eq 0) {
    gh release download index --repo $Repository --pattern index.json --dir $out --clobber
    $index = @(Get-Content (Join-Path $out "index.json") -Raw | ConvertFrom-Json)
} else {
    gh release create index --repo $Repository --title "Marketplace index" --latest=false `
        --notes "index.json: the plugins DevHub's marketplace offers (updated by the publish workflow)."
}

$published = 0
foreach ($manifest in Get-ChildItem (Join-Path $root "plugins") -Filter plugin.json -Recurse -Depth 2 | Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" }) {
    $folder = $manifest.Directory.FullName
    $relative = [IO.Path]::GetRelativePath($root, $folder).Replace('\', '/')
    $version = & (Join-Path $PSScriptRoot "Get-PluginVersion.ps1") -Folder $relative
    $manifestInfo = Get-Content $manifest.FullName -Raw | ConvertFrom-Json
    $id = $manifestInfo.id
    if ($manifestInfo.publish -eq $false) {
        Write-Output "$id ${version}: not published (""publish"": false in plugin.json)"
        continue
    }
    $tag = "$id-$version"
    if (git ls-remote --tags origin "refs/tags/$tag") {
        Write-Output "$id ${version}: already published"
        continue
    }

    $package = & (Join-Path $PSScriptRoot "Pack-Plugin.ps1") -Folder $relative -Version $version -Out $out |
        Select-Object -Last 1 | ConvertFrom-Json
    gh release create $tag $package.file --repo $Repository --title "$($package.name) $version" --target $env:GITHUB_SHA `
        --notes "$($package.name) $version for DevHub (plugin API $($package.apiVersion))." --latest=false
    if ($LASTEXITCODE -ne 0) { throw "Couldn't create the release $tag" }

    $entry = [ordered]@{
        id = $package.id; name = $package.name; description = $package.description; category = $package.category
        version = $version; apiVersion = $package.apiVersion; requires = $package.requires
        url = "https://github.com/$Repository/releases/download/$tag/$(Split-Path $package.file -Leaf)"
        sha256 = $package.sha256; size = $package.size; homepage = "https://github.com/$Repository/tree/main/$relative"
    }
    $index = @($index | Where-Object { $_.id -ne $id }) + [pscustomobject]$entry
    Write-Output "$id ${version}: published"
    $published++
}

if ($published -gt 0) {
    ConvertTo-Json -InputObject @($index | Sort-Object name) -Depth 5 | Set-Content (Join-Path $out "index.json") -Encoding utf8
    gh release upload index (Join-Path $out "index.json") --repo $Repository --clobber
    if ($LASTEXITCODE -ne 0) { throw "Couldn't upload index.json" }
}
Write-Output "$published plugin(s) published."
