using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Risks.Core;
using DevHub.Sdk.Core.Charts;
using DevHub.Services;
using DevHub.ViewModels.Board;
using DevHub.ViewModels.Dialogs;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Risks;

/// <summary>Every board gets a Risks page.</summary>
public sealed class RisksPageProvider(IExportService export) : IBoardPageProvider
{
    public string Id => "risks";
    public string Title => L.T("Risks");
    public int Order => 680;
    public BoardPageViewModel Create(BoardPageHost host) => new RisksPageViewModel(host, export);
}

/// <summary>A risk in the register list.</summary>
public sealed record RiskRow(Risk Risk, string OwnerName)
{
    public string Key => Risk.Key;
    public string Title => Risk.Title;
    public string Score => $"{Risk.Score} · {Risk.Level}";
    public string Detail => L.T($"P{Risk.Probability} × I{Risk.Impact} · {Risk.Status}") + (OwnerName.Length > 0 ? " · " + OwnerName : "");
    public bool IsClosed => Risk.Status == RiskStatus.Closed;
    public bool IsCritical => Risk.Score >= 16 && !IsClosed;
    public bool IsHigh => Risk.Score is >= 10 and < 16 && !IsClosed;
    public override string ToString() => $"{Key} {Title}, score {Score}";
}

/// <summary>A linked issue in the risk editor.</summary>
public sealed record RiskIssueRow(string Id, string Key, string Summary, IRelayCommand Remove, IRelayCommand Open)
{
    public override string ToString() => $"{Key} {Summary}";
}

/// <summary>
/// The risk register: the list (highest score first), the probability × impact matrix (open risks in their cells, green
/// to red), and the selected risk's editor (scores, status, owner, mitigation, contingency, review date, linked issues).
/// Edits are saved with Save; Export CSV / copy the register, save / copy the matrix.
/// </summary>
public sealed partial class RisksPageViewModel : BoardPageViewModel
{
    private readonly IExportService _export;
    private bool _loading;

    public RisksPageViewModel(BoardPageHost host, IExportService export) : base(host)
    {
        _export = export;
        Refresh();
    }

    public override string Title => L.T("Risks");

    public ObservableCollection<RiskRow> Risks { get; } = [];
    [ObservableProperty] private RiskRow? _selected;
    [ObservableProperty] private ChartData _matrix = ChartData.Empty;
    [ObservableProperty] private string _summary = "";
    public bool HasRisks => Risks.Count > 0;
    public bool HasSelection => Selected is not null;

    public IReadOnlyList<NamedItem> Levels { get; } =
        RiskRegister.Scale.Select((name, i) => new NamedItem((i + 1).ToString(CultureInfo.InvariantCulture), $"{i + 1} · {name}")).ToList();
    public IReadOnlyList<NamedItem> Statuses { get; } = Enum.GetValues<RiskStatus>().Select(s => new NamedItem(s.ToString(), s.ToString())).ToList();
    public IReadOnlyList<string> Categories => RiskRegister.Categories;
    public ObservableCollection<NamedItem> People { get; } = [];

    // The editor (a copy of the selected risk until Save).
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editCategory = "";
    [ObservableProperty] private NamedItem? _editProbability;
    [ObservableProperty] private NamedItem? _editImpact;
    [ObservableProperty] private NamedItem? _editStatus;
    [ObservableProperty] private NamedItem? _editOwner;
    [ObservableProperty] private string _editMitigation = "";
    [ObservableProperty] private string _editContingency = "";
    [ObservableProperty] private DateTime? _editReview;
    [ObservableProperty] private string _editScore = "";
    [ObservableProperty] private string _newIssueKey = "";
    [ObservableProperty] private bool _isDirty;
    public ObservableCollection<RiskIssueRow> EditIssues { get; } = [];
    private List<string> _issueIds = [];

    public override void OnBoardChanged() => Refresh();

    partial void OnSelectedChanged(RiskRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        LoadEditor();
    }

    partial void OnEditTitleChanged(string value) => Touched();
    partial void OnEditDescriptionChanged(string value) => Touched();
    partial void OnEditCategoryChanged(string value) => Touched();
    partial void OnEditProbabilityChanged(NamedItem? value) => Touched();
    partial void OnEditImpactChanged(NamedItem? value) => Touched();
    partial void OnEditStatusChanged(NamedItem? value) => Touched();
    partial void OnEditOwnerChanged(NamedItem? value) => Touched();
    partial void OnEditMitigationChanged(string value) => Touched();
    partial void OnEditContingencyChanged(string value) => Touched();
    partial void OnEditReviewChanged(DateTime? value) => Touched();

    private void Touched()
    {
        if (_loading)
            return;
        IsDirty = true;
        UpdateScore();
    }

    private void UpdateScore()
    {
        var score = int.Parse(EditProbability?.Id ?? "3", CultureInfo.InvariantCulture) * int.Parse(EditImpact?.Id ?? "3", CultureInfo.InvariantCulture);
        EditScore = L.T($"Score {score} · {RiskRegister.LevelOf(score)}");
    }

    private void Refresh()
    {
        var board = Context.Snapshot;
        var keep = Selected?.Risk.Id;
        var all = RiskRegister.All(board);
        People.Clear();
        People.Add(new NamedItem(null, L.T("No owner")));
        foreach (var p in board.Project.People)
            People.Add(NamedItem.Person(p));
        Risks.Clear();
        foreach (var risk in all)
            Risks.Add(new RiskRow(risk, board.Project.Person(risk.Owner)?.Name ?? ""));
        Matrix = RiskRegister.Matrix(all);
        var open = all.Where(r => r.Status != RiskStatus.Closed).ToList();
        Summary = all.Count == 0
            ? L.T("No risk yet. Raise the ones that could stop the project: the team, the technology, the schedule, outside parties.")
            : L.T($"{open.Count} open of {all.Count} · {open.Count(r => r.Score >= 16)} critical, {open.Count(r => r.Score is >= 10 and < 16)} high · ") +
              L.T($"{open.Count(r => r.Owner is null)} without an owner");
        OnPropertyChanged(nameof(HasRisks));
        // Keep the editor's unsaved changes when the board changes for another reason.
        var dirty = IsDirty;
        Selected = Risks.FirstOrDefault(r => r.Risk.Id == keep) ?? (keep is null ? Risks.FirstOrDefault() : null);
        if (!dirty)
            LoadEditor();
    }

    private void LoadEditor()
    {
        _loading = true;
        try
        {
            var risk = Selected?.Risk;
            EditTitle = risk?.Title ?? "";
            EditDescription = risk?.Description ?? "";
            EditCategory = risk?.Category ?? "";
            EditProbability = Levels[(risk?.Probability ?? 3) - 1];
            EditImpact = Levels[(risk?.Impact ?? 3) - 1];
            EditStatus = Statuses.FirstOrDefault(s => s.Id == (risk?.Status ?? RiskStatus.Open).ToString());
            EditOwner = People.FirstOrDefault(p => p.Id == risk?.Owner) ?? People[0];
            EditMitigation = risk?.Mitigation ?? "";
            EditContingency = risk?.Contingency ?? "";
            EditReview = risk?.Review?.ToDateTime(TimeOnly.MinValue);
            _issueIds = risk?.Issues.ToList() ?? [];
            FillIssues();
            UpdateScore();
            IsDirty = false;
        }
        finally
        {
            _loading = false;
        }
    }

    private void FillIssues()
    {
        var board = Context.Snapshot;
        EditIssues.Clear();
        foreach (var id in _issueIds)
        {
            if (board.Issue(id) is not { } issue)
                continue;
            EditIssues.Add(new RiskIssueRow(id, issue.Key, issue.Summary,
                new RelayCommand(() => { _issueIds.Remove(id); FillIssues(); Touched(); }),
                new RelayCommand(() => Host.SelectIssue(id))));
        }
    }

    /// <summary>Links an issue by its key ("SHOP-12"): the work that mitigates the risk.</summary>
    [RelayCommand]
    private void AddIssue()
    {
        if (Context.Snapshot.IssueByKey(NewIssueKey.Trim()) is not { } issue)
        {
            Context.Status.Show(L.T($"There's no issue {NewIssueKey.Trim()} on this board."));
            return;
        }
        if (!_issueIds.Contains(issue.Id))
            _issueIds.Add(issue.Id);
        NewIssueKey = "";
        FillIssues();
        Touched();
    }

    [RelayCommand]
    private async Task AddRiskAsync()
    {
        var dialog = new TextInputDialogViewModel(L.T("Raise a Risk"), L.T("What could go wrong?"), "", L.T("Raise"))
        {
            Choice = new ChoiceInput(L.T("Category"), [.. RiskRegister.Categories], RiskRegister.Categories[0]),
            Explanation = L.T("Then rate its probability and impact, give it an owner and plan the mitigation on the right."),
        };
        if (!await Context.Dialogs.ShowDialogAsync(dialog))
            return;
        var id = BoardIds.New();
        if (await Context.RunAsync(new AddRisk(dialog.Text) { Id = id, Category = dialog.Choice!.Selected }))
            Selected = Risks.FirstOrDefault(r => r.Risk.Id == id);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Selected?.Risk is not { } risk)
            return;
        var edited = risk with
        {
            Title = EditTitle,
            Description = EditDescription,
            Category = EditCategory,
            Probability = int.Parse(EditProbability?.Id ?? "3", CultureInfo.InvariantCulture),
            Impact = int.Parse(EditImpact?.Id ?? "3", CultureInfo.InvariantCulture),
            Status = Enum.TryParse<RiskStatus>(EditStatus?.Id, out var status) ? status : RiskStatus.Open,
            Owner = EditOwner?.Id,
            Mitigation = EditMitigation,
            Contingency = EditContingency,
            Review = EditReview is { } review ? DateOnly.FromDateTime(review) : null,
            Issues = _issueIds,
        };
        IsDirty = false;
        if (!await Context.RunAsync(new EditRisk(edited)))
            IsDirty = true;
    }

    [RelayCommand]
    private void Revert() => LoadEditor();

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected?.Risk is { } risk && await Context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel(L.T("Delete Risk"),
                L.T($"Delete {risk.Key} \"{risk.Title}\"? Closing it (Status: Closed) keeps it in the register."), L.T("Delete"), [risk.ToString()])))
        {
            IsDirty = false;
            await Context.RunAsync(new DeleteRisk(risk.Id));
        }
    }

    private string ExportName => L.T($"Risks {Context.Project.Key}");

    [RelayCommand]
    private Task SaveMatrixAsync() => _export.SaveChartAsync(ChartKind.Heatmap, Matrix, L.T($"Risk matrix {Context.Project.Key}"));

    [RelayCommand]
    private Task CopyMatrixAsync() => _export.CopyChartAsync(ChartKind.Heatmap, Matrix);

    [RelayCommand]
    private Task ExportRegisterAsync() => _export.SaveTableAsync(RiskRegister.Table(Context.Snapshot, RiskRegister.All(Context.Snapshot)), ExportName);

    [RelayCommand]
    private Task CopyRegisterAsync() => _export.CopyTableAsync(RiskRegister.Table(Context.Snapshot, RiskRegister.All(Context.Snapshot)));
}
