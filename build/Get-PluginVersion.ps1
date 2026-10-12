<#
  One plugin's version, from git (each plugin has its own):
    N.M.0, where N = the highest major tag "<plugin-id>/vN" (e.g. naurid.hello-world/v2, only you create
    those), and M = the commits on main's first-parent line (pushes / PR merges) since that tag that
    touched the plugin's folder. Changing another plugin doesn't change this one's version.
  The tagged commit is N.0.0; before any major tag, 0.M.0 (tag "<id>/v1" for 1.0.0).
  Release tags made by CI ("<plugin-id>/1.3.0") don't look like "/vN", so they don't count.

    pwsh build/Get-PluginVersion.ps1 -Folder plugins/HelloWorld
#>
param(
    [Parameter(Mandatory)] [string]$Folder,
    [string]$Ref = "HEAD"
)
$ErrorActionPreference = "Stop"

$id = (Get-Content (Join-Path $Folder "plugin.json") -Raw | ConvertFrom-Json).id
$prefix = "$id/v"
$majors = @(git tag --merged $Ref --list "$prefix*" | Where-Object { $_.Substring($prefix.Length) -match '^\d+$' } | ForEach-Object { [int]$_.Substring($prefix.Length) })

if ($majors.Count -eq 0) {
    $major = 0
    $minor = [int](git rev-list --first-parent --count $Ref -- $Folder)
} else {
    $major = ($majors | Measure-Object -Maximum).Maximum
    $minor = [int](git rev-list --first-parent --count "$prefix$major..$Ref" -- $Folder)
}
if ($LASTEXITCODE -ne 0) { throw "git failed (is this a clone with full history?)" }

"$major.$minor.0"
