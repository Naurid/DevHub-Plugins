<#
  One plugin's version, from git (each plugin has its own):
    N.M.P, where N = the highest major tag "<plugin-id>/vN" (e.g. naurid.hello-world/v2, only you create
    those), and M.P come from the commits on main's first-parent line (pushes / PR merges) since that tag that
    touched the plugin's folder: the folder right under plugins/ (plugins/Git for plugins/Git/DevHub.Git,
    so a change to its DevHub.Git.Core counts too). Changing another plugin doesn't change this one's version.
      - a fix (the merge of a Fix/, Hotfix/ or Bugfix/ branch or pull request, or a commit titled "fix: …") adds 1 to P;
      - anything else adds 1 to M and starts P again at 0.
  The tagged commit is N.0.0; before any major tag, 0.M.P (tag "<id>/v1" for 1.0.0).
  Release tags made by CI ("<plugin-id>/1.3.0") don't look like "/vN", so they don't count.

    pwsh build/Get-PluginVersion.ps1 -Folder plugins/Charts/DevHub.Charts
#>
param(
    [Parameter(Mandatory)] [string]$Folder,
    [string]$Ref = "HEAD"
)
$ErrorActionPreference = "Stop"

# Fixes count as patches from here on; up to this commit (main when the rule came in) every commit was a minor, and
# counting the older fixes as patches now would make versions go back (installed plugins wouldn't update).
$PatchesSince = "e43703e257005613f0226293a3944051f307e7d7"
$FixPattern = "^(Merge (branch '|pull request #\d+ from [^/\s]+/)(fix|hotfix|bugfix)/|(fix|hotfix|bugfix)(\(.*\))?!?:)"

$id = (Get-Content (Join-Path $Folder "plugin.json") -Raw | ConvertFrom-Json).id
$prefix = "$id/v"

# The plugin's own folder under plugins/ (plugins/Git for plugins/Git/DevHub.Git), as an absolute path for git.
$plugins = Join-Path (Split-Path $PSScriptRoot) "plugins"
$first = [IO.Path]::GetRelativePath($plugins, (Resolve-Path $Folder).Path).Split([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)[0]
$Folder = Join-Path $plugins $first
$majors = @(git tag --merged $Ref --list "$prefix*" | Where-Object { $_.Substring($prefix.Length) -match '^\d+$' } | ForEach-Object { [int]$_.Substring($prefix.Length) })

$range = $Ref
$major = 0
if ($majors.Count -gt 0) {
    $major = ($majors | Measure-Object -Maximum).Maximum
    $range = "$prefix$major..$Ref"
}

$before = @{}
git rev-list --first-parent $PatchesSince 2>$null | ForEach-Object { $before[$_] = $true }
$minor = 0
$patch = 0
foreach ($line in @(git log --first-parent --reverse --format="%H%x09%s" $range -- $Folder)) {
    $sha, $subject = $line -split "`t", 2
    if (-not $before.ContainsKey($sha) -and $subject -match $FixPattern) {
        $patch++
    } else {
        $minor++
        $patch = 0
    }
}
if ($LASTEXITCODE -ne 0) { throw "git failed (is this a clone with full history?)" }

"$major.$minor.$patch"
