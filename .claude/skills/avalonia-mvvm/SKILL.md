---
name: avalonia-mvvm
description: Conventions for DevHub plugin UI projects (this repository: plugins/<Name>/DevHub.<Name>) — Avalonia 12 views, ViewModels with CommunityToolkit.Mvvm, DI registration, ViewLocator, compiled bindings, design tokens and components, actions, documents/tool windows/dialogs, threading, and how to verify UI changes. Use when adding or changing any view, ViewModel, style, action or service in a plugin UI project under plugins/.
---

# DevHub UI (App + Sdk) — Avalonia + MVVM conventions

**Read DevHub's `docs/UI_GUIDE.md` first** (`C:\P\DevHub\DevHub\docs\UI_GUIDE.md`): rules, design tokens, components, and
recipes for adding an icon, token, action, document (editor tab), tool window, dialog or
Settings page. DevHub's `docs/ACTIONS_AND_FUZZY_MATCHING.md` covers actions and Find Action.

**Avalonia 12:** verify APIs you aren't sure of against the 12.x source
(github.com/AvaloniaUI/Avalonia, tag `12.1.3`) or docs.avaloniaui.net; many online
snippets are 11.x or older. Don't guess on build errors. Known 12.x traps are listed in
UI_GUIDE §8 (e.g. `KeyGesture.Parse("Alt+1")`, drawn caption buttons, `PlaceholderText`).

## Where new screens go
- Editor tab → `DocumentViewModelBase` (ViewModels/Documents), registered as
  `DocumentViewModelBase`; gets a `Navigate.<Id>` action automatically.
- Docked panel → `ToolWindowViewModelBase` (ViewModels/ToolWindows), registered as itself
  and as the base type; gets a stripe button and Alt+N.
- Modal → `DialogViewModelBase` (ViewModels/Dialogs), transient + `Func<>` factory, opened
  with `IDialogService.ShowDialogAsync`.
- ViewLocator maps `X.ViewModels.….FooViewModel` → `X.Views.….FooView` by name, in the
  ViewModel's own assembly (so a View lives next to its ViewModel); views are
  `UserControl`s with `x:DataType` and only `InitializeComponent()` in code-behind.
- User operations → an `AppAction` in an `IActionProvider` (menus, Find Action, shortcuts).
- Shared building blocks (base classes, Controls, Themes/tokens, action types, service
  interfaces, icons) live in `src/DevHub.Sdk`; shell-only code in `src/DevHub.App`; a feature's
  screens in its plugin's UI project (`plugins/Git/DevHub.Git/` …), registered in its plugin class. XAML
  namespaces use `using:` (e.g. `xmlns:c="using:DevHub.Controls"`), never `clr-namespace:`
  without `;assembly=`, which can't see other assemblies.

## ViewModel style
```csharp
public partial class TemplateListViewModel(ITemplateLibrary library) : ViewModelBase
{
    [ObservableProperty] private string _searchText = "";
    public ObservableCollection<TemplateItemViewModel> Templates { get; } = [];

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync(CancellationToken ct) { /* await library... */ }
}
```
- `[ObservableProperty]` on fields (`_camelCase`), `[RelayCommand]` on methods;
  async commands get `IsRunning` and cancellation for free.
- `[NotifyCanExecuteChangedFor(nameof(XCommand))]` when a property affects whether a
  command can run. Actions reuse these commands, so CanExecute is shared everywhere.
- Wrap Core models in small item ViewModels for lists; give them `ToString()` = label
  (accessible name).
- No Avalonia control/window/Application types in ViewModels; colors as "#RRGGBB" strings.
- DI cycles (something needs the ActionRegistry that is built from providers): inject a
  `Func<T>` factory instead of `T`.

## Services (ViewModels depend on the interfaces only)
`IDialogService` (ShowDialogAsync, PickFolderAsync, PickFileAsync), `IThemeService`
(Mode, IsDark, IsCompact, SetAccent, Changed; saves itself), `IStatusService` (Show).
Settings: `ISettingsStore` + `store.Update(s => s.Section = …)`.

## XAML
- Compiled bindings on by default; every view and `DataTemplate` declares `x:DataType`.
- Tokens only, via `DynamicResource`; match the token type to the property.
- Restyling stock controls: Styles in `Themes/Controls.axaml`; our controls get a
  ControlTheme in `Themes/Components/` (+ include in `DevHubTheme.axaml`).
- `Grid` (`Auto,*,Auto`) for rows that must fit; horizontal `StackPanel`s don't constrain width.

## Threading
- `await` Core calls from async commands; UI-bound properties are set after the `await`.
- Background callbacks → `Dispatcher.UIThread.Post` (or `Progress<T>` created on the UI thread).
- Never `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` in the App.

## Events
Any `+=` has a matching `-=` (paired lifecycle hook, `Dispose`, or `Closed`). Prefer
bindings and commands over code-behind events.

## Verifying UI changes
Build to a scratch output folder if the normal one is locked
(a plugin runs in DevHub through `DEVHUB_PLUGIN_PATHS` = its build folder: F5's "DevHub" profile does that), then:
- **Never against the user's real settings:** set `DEVHUB_DATA_DIR` to a scratch folder
  for the launched process (the `APPDATA` variable is ignored by Windows' folder lookup).
- Drive it with UI Automation (Invoke/SelectionItem/Value patterns) and screenshot with
  `PrintWindow` (popups/menus need a screen capture of the window area).
- **Before sending keystrokes (SendKeys), check that the foreground window belongs to the
  test process**; otherwise the keys go to whatever app the user has focused.
- Capture stderr (`-RedirectStandardError`) to see crash stack traces.
- Plugin changes: `pwsh build/Test-Plugins.ps1` runs your plugins together and each alone
  (Debug builds validate DI at startup and print "DevHub plugins: found …; loaded …" to stdout).
- "No precompiled XAML found" at startup = a half-finished build (locked DLL): `dotnet build --no-incremental`.
- Stop only the process you started (by PID), never every `DevHub.App`.
- End with a short manual check list for the user (what to click, what should appear,
  Dark/Light/Compact, empty lists, long names).
