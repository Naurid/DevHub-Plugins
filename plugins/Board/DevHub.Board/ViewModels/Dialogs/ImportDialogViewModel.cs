using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board.Core;
using DevHub.Board.Core.Import;
using DevHub.Integration;
using DevHub.Services.Board;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Dialogs;

/// <summary>
/// Import Issues: from a Jira CSV export, or from a GitHub repository's issues (when the GitHub plugin is on and signed
/// in). Load shows what would happen (issues, new people / components / versions, how types and statuses map);
/// Import does it, as one change (Undo takes it back).
/// </summary>
public sealed partial class ImportDialogViewModel : DialogViewModelBase
{
    private readonly BoardContext _context;
    private readonly IGitHubApi? _gitHub;
    private ImportIssues? _import;

    public ImportDialogViewModel(BoardContext context, IGitHubApi? gitHub)
    {
        _context = context;
        _gitHub = gitHub;
        Sources = gitHub is null ? [L.T("Jira (CSV export)")] : [L.T("Jira (CSV export)"), L.T("GitHub repository issues")];
        _source = Sources[0];
    }

    public override string Title => L.T("Import Issues");
    public override string OkText => L.T("Import");
    public override double Width => 620;
    public override double Height => 560;

    public IReadOnlyList<string> Sources { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsJira))] private string _source;
    public bool IsJira => Source == Sources[0];   // Jira first, then GitHub
    [ObservableProperty] private string _file = "";
    [ObservableProperty] private string _repository = "";
    [ObservableProperty] private string _preview = L.T("Choose what to import, then Load to see what would happen.");
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isLoading;

    partial void OnSourceChanged(string value) => _import = null;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _context.Dialogs.PickFileAsync(L.T("Jira CSV export"), L.T("CSV files"), ["*.csv"]) is { } path)
            File = path;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        Error = null;
        _import = null;
        IsLoading = true;
        try
        {
            IReadOnlyList<ImportedIssue> issues;
            string source;
            if (IsJira)
            {
                if (!System.IO.File.Exists(File))
                {
                    Error = L.T("Choose the CSV file Jira exported (Filters → Export → CSV, all fields).");
                    return;
                }
                issues = JiraCsvImport.Read(await System.IO.File.ReadAllTextAsync(File));
                source = L.T("Jira");
            }
            else
            {
                var repo = Repository.Trim().Trim('/');
                if (repo.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                    repo = repo["https://github.com/".Length..];
                if (repo.Count(c => c == '/') != 1)
                {
                    Error = L.T("Name the repository as owner/name (like naurid/devhub).");
                    return;
                }
                issues = GitHubIssuesImport.Read(await GitHubPagesAsync(repo));
                source = "GitHub " + repo;
            }
            if (issues.Count == 0)
            {
                Error = L.T("No issues were found there.");
                return;
            }
            _import = new ImportIssues(issues, source);
            var preview = _import.Preview(_context.Snapshot, new OperationContext(_context.Me, DateTimeOffset.Now));
            Preview = Describe(preview);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or GitHubApiUnavailableException
                                       or System.Text.Json.JsonException or BoardRuleException)
        {
            Error = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Every page of the repository's issues (up to 20 pages of 100).
    private async Task<IReadOnlyList<string>> GitHubPagesAsync(string repo)
    {
        var pages = new List<string>();
        string? path = $"repos/{repo}/issues?state=all&per_page=100";
        for (var i = 0; path is not null && i < 20; i++)
        {
            var response = await _gitHub!.SendAsync(GitHubApiRequest.Get(path));
            if (!response.IsSuccess)
                throw new FormatException(L.T($"GitHub answered {(int)response.Status}: is the repository name right, and are you signed in with access to it?"));
            pages.Add(response.Body);
            path = response.NextPageUrl;
        }
        return pages;
    }

    private static string Describe(ImportPreview preview)
    {
        var text = new StringBuilder();
        text.Append(preview.Issues).Append(L.T(" issues will be created (new keys; their old keys still find them).\n"));
        if (preview.NewPeople.Count > 0)
            text.Append(L.T("\nPeople added: ")).Append(string.Join(", ", preview.NewPeople)).Append('\n');
        if (preview.NewComponents.Count > 0)
            text.Append(L.T("Components added: ")).Append(string.Join(", ", preview.NewComponents)).Append('\n');
        if (preview.NewVersions.Count > 0)
            text.Append(L.T("Versions added: ")).Append(string.Join(", ", preview.NewVersions)).Append('\n');
        if (preview.Types.Count > 0)
            text.Append("\nTypes: ").Append(string.Join(", ", preview.Types.Select(t => $"{t.Key} → {t.Value}"))).Append('\n');
        if (preview.Statuses.Count > 0)
            text.Append("Statuses: ").Append(string.Join(", ", preview.Statuses.Select(s => $"{s.Key} → {s.Value}"))).Append('\n');
        foreach (var warning in preview.Warnings)
            text.Append("\n• ").Append(warning);
        text.Append(L.T("\n\nSprints aren't imported. Undo (Board menu) takes the whole import back."));
        return text.ToString();
    }

    protected override async Task<bool> ApplyChangesAsync()
    {
        if (_import is null)
        {
            await LoadAsync();
            if (_import is null)
                return false;
        }
        return await _context.RunAsync(_import);
    }
}
