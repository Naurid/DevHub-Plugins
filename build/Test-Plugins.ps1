<#
  Starts DevHub with this repository's plugins, from their build folders (DEVHUB_PLUGIN_PATHS), and reports
  whether it stays up and what it loaded. Run it after changing a plugin: a plugin that only works next to
  another one, or that breaks DevHub at startup, shows up here.

  Builds every plugin (Debug) first. Cases: all of them together, then each one alone (DevHub's own plugins
  off, except those a plugin requires), plus every DevHub plugin switched on. Each run gets a scratch data
  folder under %TEMP%, so your settings aren't touched.

    pwsh build/Test-Plugins.ps1                                  # the installed DevHub
    pwsh build/Test-Plugins.ps1 -Exe C:\P\DevHub\DevHub\src\DevHub.App\bin\Debug\net10.0\DevHub.App.exe
    pwsh build/Test-Plugins.ps1 -SdkVersion 1.7.0-local.20261012143005   # against unreleased Sdk changes
#>
param(
    [string]$Exe = (Join-Path $env:LOCALAPPDATA "DevHub\current\DevHub.App.exe"),
    [string]$SdkVersion,
    [int]$Seconds = 8
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
if (-not (Test-Path $Exe)) { throw "DevHub not found at $Exe (install it, or pass -Exe)." }

# Build every plugin; its build folder (with plugin.json, see Directory.Build.targets) is what DevHub loads.
$plugins = @()
foreach ($manifest in Get-ChildItem (Join-Path $root "plugins") -Filter plugin.json -Recurse -Depth 2 | Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" }) {
    $args = @("build", $manifest.Directory.FullName, "-c", "Debug", "--nologo", "-v", "q", "-p:DevHubLaunchSettings=false")
    if ($SdkVersion) { $args += "-p:DevHubSdkVersion=$SdkVersion" }
    dotnet @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($manifest.Directory.Name)" }
    $info = Get-Content $manifest.FullName -Raw | ConvertFrom-Json
    $output = Join-Path $manifest.Directory.FullName "bin\Debug\net10.0"
    $plugins += [pscustomobject]@{ Id = $info.id; Folder = $output; Requires = @($info.requires | Where-Object { $_ }) }
}
if ($plugins.Count -eq 0) { throw "No plugins in $(Join-Path $root 'plugins')." }

# A plugin "alone": its folder plus those of the plugins it requires that live in this repository.
function Get-Folders($plugin) {
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue = [Collections.Generic.Queue[object]]::new(); $queue.Enqueue($plugin)
    while ($queue.Count -gt 0) {
        $next = $queue.Dequeue()
        if (-not $ids.Add($next.Id)) { continue }
        foreach ($id in $next.Requires) { $plugins | Where-Object Id -eq $id | ForEach-Object { $queue.Enqueue($_) } }
    }
    @($plugins | Where-Object { $ids.Contains($_.Id) } | ForEach-Object Folder)
}

$scratch = Join-Path $env:TEMP "devhub-plugin-tests"
$failed = 0

function Start-Case([string]$name, [string[]]$folders, $enabled) {
    $dir = Join-Path $scratch ($name -replace '[^A-Za-z0-9+.-]', '_')
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force (Join-Path $dir "DevHub") | Out-Null
    $settings = @{}
    if ($null -ne $enabled) { $settings.enabledPlugins = @($enabled) }
    $settings | ConvertTo-Json | Set-Content (Join-Path $dir "DevHub\settings.json")

    $env:DEVHUB_DATA_DIR = $dir
    $env:DEVHUB_PLUGIN_PATHS = $folders -join [IO.Path]::PathSeparator
    $out = Join-Path $dir "stdout.txt"
    $err = Join-Path $dir "stderr.txt"
    $p = Start-Process -FilePath $Exe -PassThru -RedirectStandardError $err -RedirectStandardOutput $out
    Start-Sleep $Seconds
    $report = (Get-Content $out -ErrorAction SilentlyContinue | Select-String "DevHub plugins: ") -replace "DevHub plugins: found [^;]*; ", ""
    if ($p.HasExited) {
        Write-Host ("{0,-36} EXITED ({1})" -f $name, $p.ExitCode) -ForegroundColor Red
        Get-Content $err -TotalCount 12 | ForEach-Object { Write-Host "    $_" }
        $script:failed++
        return
    }
    $hung = (Get-Process -Id $p.Id).MainWindowHandle -eq 0
    Stop-Process -Id $p.Id -Force
    if ($hung) {
        Write-Host ("{0,-36} NO WINDOW (startup hangs)  {1}" -f $name, $report) -ForegroundColor Red
        $script:failed++
    } elseif ("$report" -match "problems:|skipped ") {
        Write-Host ("{0,-36} PROBLEMS  {1}" -f $name, $report) -ForegroundColor Yellow
        $script:failed++
    } else {
        Write-Host ("{0,-36} ok   {1}" -f $name, $report)
    }
}

try {
    Start-Case "all mine" $plugins.Folder @()
    foreach ($plugin in $plugins) { Start-Case "$($plugin.Id) alone" (Get-Folders $plugin) @() }
    Start-Case "all mine + every DevHub plugin" $plugins.Folder $null
} finally {
    Remove-Item Env:DEVHUB_DATA_DIR, Env:DEVHUB_PLUGIN_PATHS -ErrorAction SilentlyContinue
}
if ($failed -gt 0) { throw "$failed case(s) failed." }
