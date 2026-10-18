# I18n: the translation helper

A copy of DevHub's tools/I18n (keep the two in step). Here: `dotnet run --project tools/I18n -- check plugins`.

DevHub's text is English with French translations (`L` in `DevHub.Sdk.Core.Localization`, docs/UI_GUIDE.md §5
"Translate text"). This tool keeps the French files in step with the code. Run it from the repository root:

```
dotnet run --project tools/I18n -- keys src          # add new keys to each project's i18n/fr.json (empty French)
dotnet run --project tools/I18n -- check src         # fail on a key without French, French missing a {0}, or a translated id
```

A folder with a `.csproj` is one project; any other folder means every project under it (tests and tools left out).
Keys that are no longer in the code stay in the file (names translated where they're shown, such as menus, are
added by hand), and `keys` says how many there are.

## Wrapping existing text

For code written before the translations (or a plugin that has none yet):

```
dotnet run --project tools/I18n -- xaml <project>    # Text="Apply" → Text="{l:T 'Apply'}" (+ xmlns:l) in every .axaml
dotnet run --project tools/I18n -- list <project>    # C# strings that look like UI text: file:line:col, text, code
dotnet run --project tools/I18n -- wrap <project> skip.txt
```

`list` leaves out what is clearly code (attribute arguments, constants, `case` labels, comparisons, dictionary keys,
paths, `Menu` / `Category` / `Gesture` / `Id` values, developer exceptions). Read the list, put the `file:line:col`
of each string that must stay as it is in `skip.txt` (one per line), then `wrap` puts every other one in `L.T(…)`
and adds the `using`. Build, then `keys` and translate.
