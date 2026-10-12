# DevHub plugins

Plugins for [DevHub](https://github.com/Naurid/DevHub), offered in its marketplace (DevHub: Settings →
Plugins → Marketplace). Each folder in `plugins/` is one plugin; every push to `main` publishes the
plugins that changed.

## Layout

```
plugins/<Name>/          one plugin: <Name>.csproj, plugin.json, its plugin class, ViewModels/, Views/
build/Get-Sdk.ps1        DevHub's Sdk (what plugins build against) from DevHub's latest release, into .sdk/
build/Get-PluginVersion.ps1   a plugin's version, from git
build/Pack-Plugin.ps1    builds one plugin and packs <id>-<version>.zip (plugin.json + its DLLs)
build/Publish-Plugins.ps1     CI: releases the plugins whose version changed and updates the index
.github/workflows/       ci.yml (build, vulnerable packages), publish.yml (releases + index)
Directory.Build.props    shared settings: the Sdk reference, Avalonia / CommunityToolkit / DI build-only
```

## Writing a plugin

1. `pwsh build/Get-Sdk.ps1` once (and after DevHub releases, to build against the newest Sdk).
2. Copy `plugins/HelloWorld` to `plugins/<YourPlugin>` and rename:
   - the `.csproj`;
   - `plugin.json`: a new `id` (lowercase, `yourname.plugin-name`), name, description, category.
     Leave out `version`, which is computed;
   - the plugin class: its `PluginInfo` id must equal plugin.json's.
3. Build: `dotnet build plugins/<YourPlugin>`. DevHub's plugin guide (docs/PLUGINS.md in DevHub)
   explains the extension points (tool windows, documents, actions, settings pages…).
4. Try it in DevHub before publishing:
   - `pwsh build/Pack-Plugin.ps1 -Folder plugins/<YourPlugin> -Version 0.0.1`;
   - write an `index.json` with one entry whose `url` is the zip's `file:///…` path and `sha256` its
     hash (the script prints both);
   - add that index.json's path to DevHub's Settings → Plugins → Marketplace → Sources, Refresh,
     Install, and restart.

Keep the versions of Avalonia, CommunityToolkit.Mvvm and Microsoft.Extensions.DependencyInjection
in `Directory.Build.props` equal to DevHub's (DevHub provides them at run time). `apiVersion` in
plugin.json must match DevHub's plugin API version (`PluginApi.Version`); DevHub refuses plugins
made for another one.

## Versions (each plugin has its own)

A plugin's version is **N.M.0**:

- **N (major)** is the highest tag `<plugin-id>/vN`, e.g. `git tag naurid.hello-world/v2 && git push origin naurid.hello-world/v2`.
- **M (minor)** is the number of pushes or merges on `main` since that tag *that touched the
  plugin's folder*. Changing one plugin never changes another's version.
- Before the first major tag the version is `0.M.0`.

`pwsh build/Get-PluginVersion.ps1 -Folder plugins/HelloWorld` shows the version it would get.

## Publishing (automatic)

On every push to `main` (and on a plugin's major tag), `publish.yml`:

1. runs CI;
2. for each plugin, computes its version; if the release `<id>-<version>` doesn't exist yet, it
   builds and packs the plugin and creates that release with the zip;
3. replaces the plugin's entry in **`index.json`**, a file on the release named `index` (version,
   download URL, SHA-256, size).

DevHub reads `https://github.com/Naurid/DevHub-Plugins/releases/download/index/index.json`.

## One-time GitHub settings (like DevHub's, see DevHub's docs/RELEASING.md)

- Public repository.
- Ruleset on `main` (pull requests and the CI check required; you on the bypass list).
- Ruleset on tags `*/v*` (only you).
- Actions: read-only by default, approval for first-time contributors.
- Dependabot alerts and secret scanning on.
