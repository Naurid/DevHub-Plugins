using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Services;
using DevHub.ViewModels.Board;
using DevHub.ViewModels.Dialogs;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Scrum;

/// <summary>Scrum boards get a Retrospectives page: one retrospective per sprint.</summary>
public sealed class RetrospectivePageProvider(IExportService export) : IBoardPageProvider
{
    public const string PageId = "retrospectives";

    public string Id => PageId;
    public string Title => L.T("Retrospectives");
    public int Order => 650;
    public bool AppliesTo(BoardDefinition definition) => definition.Kind == BoardKind.Scrum;
    public BoardPageViewModel Create(BoardPageHost host) => new RetrospectivePageViewModel(host, export);
}

/// <summary>A sticky note in a column; its text saves when the box loses focus.</summary>
public sealed partial class RetroNoteRow(string id, string text, Func<Task> save, Func<RetroNoteRow, Task> remove) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _text = text;
    private string _saved = text;

    public IAsyncRelayCommand RemoveCommand => new AsyncRelayCommand(() => remove(this));

    /// <summary>Called when the text box loses focus (bound with LostFocus): saves an edited note.</summary>
    partial void OnTextChanged(string value)
    {
        if (value.Trim() == _saved.Trim())
            return;
        _saved = value;
        _ = save();
    }

    public override string ToString() => Text;
}

/// <summary>A column of notes: what went well, what to improve, problems.</summary>
public sealed partial class RetroColumnViewModel(string title, string hint, Func<Task> save) : ObservableObject
{
    public string Title { get; } = title;
    public string Hint { get; } = hint;
    public ObservableCollection<RetroNoteRow> Notes { get; } = [];

    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [ObservableProperty] private string _newText = "";

    public void Load(IEnumerable<RetroNote> notes)
    {
        Notes.Clear();
        foreach (var note in notes)
            Notes.Add(new RetroNoteRow(note.Id, note.Text, save, Remove));
    }

    public IReadOnlyList<RetroNote> ToNotes() => Notes.Select(n => new RetroNote { Id = n.Id, Text = n.Text }).ToList();

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewText);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        Notes.Add(new RetroNoteRow("", NewText.Trim(), save, Remove));
        NewText = "";
        await save();
    }

    private async Task Remove(RetroNoteRow row)
    {
        Notes.Remove(row);
        await save();
    }

    public override string ToString() => Title;
}

/// <summary>An improvement action: text, owner, done, and the issue that tracks it.</summary>
public sealed partial class RetroActionRow : ObservableObject
{
    private readonly Func<Task> _save;
    private readonly bool _loading;
    private string _saved;

    public RetroActionRow(RetroAction action, IReadOnlyList<NamedItem> people, BoardSnapshot board, Func<Task> save)
    {
        _loading = true;
        Id = action.Id;
        People = people;
        _save = save;
        _text = action.Text;
        _saved = action.Text;
        _owner = people.FirstOrDefault(p => p.Id == action.Owner) ?? people[0];
        _done = action.Done;
        IssueId = board.Issue(action.Issue)?.Id;
        IssueKey = board.Issue(action.Issue)?.Key;
        _loading = false;
    }

    public string Id { get; }
    public IReadOnlyList<NamedItem> People { get; }
    public string? IssueId { get; set; }
    public string? IssueKey { get; }
    public bool HasIssue => IssueKey is not null;

    [ObservableProperty] private string _text;
    [ObservableProperty] private NamedItem _owner;
    [ObservableProperty] private bool _done;

    public IAsyncRelayCommand? RemoveCommand { get; init; }
    public IAsyncRelayCommand? CreateIssueCommand { get; init; }
    public IRelayCommand? OpenIssueCommand { get; init; }

    partial void OnTextChanged(string value)
    {
        if (_loading || value.Trim() == _saved.Trim())
            return;
        _saved = value;
        _ = _save();
    }

    partial void OnOwnerChanged(NamedItem value)
    {
        if (!_loading)
            _ = _save();
    }

    partial void OnDoneChanged(bool value)
    {
        if (!_loading)
            _ = _save();
    }

    public RetroAction ToAction() => new() { Id = Id, Text = Text, Owner = Owner.Id, Done = Done, Issue = IssueId };

    public override string ToString() => Text;
}

/// <summary>An action from an earlier retrospective that isn't done yet: shown for follow-up, ticked off here.</summary>
public sealed partial class FollowUpRow : ObservableObject
{
    private readonly Func<bool, Task> _setDone;

    public FollowUpRow(string text, string from, Func<bool, Task> setDone)
    {
        Text = text;
        From = from;
        _setDone = setDone;
    }

    public string Text { get; }
    public string From { get; }
    [ObservableProperty] private bool _done;

    partial void OnDoneChanged(bool value) => _ = _setDone(value);

    public override string ToString() => $"{Text} ({From})";
}

/// <summary>
/// A sprint's retrospective: findings in three columns (went well, to improve, problems), the improvement actions with
/// their owners (an action can become an issue), how they're followed up, and earlier sprints' actions still open.
/// Every change saves at once (and can be undone); Save as Markdown / Copy for the report.
/// </summary>
public sealed partial class RetrospectivePageViewModel : BoardPageViewModel
{
    private readonly IExportService _export;
    private bool _loading;

    public RetrospectivePageViewModel(BoardPageHost host, IExportService export) : base(host)
    {
        _export = export;
        WentWell = new RetroColumnViewModel(L.T("What went well"), L.T("Add: what helped (Enter)"), SaveAsync);
        ToImprove = new RetroColumnViewModel(L.T("What to improve"), L.T("Add: what slowed us down (Enter)"), SaveAsync);
        Problems = new RetroColumnViewModel(L.T("Problems"), L.T("Add: a problem behind it (Enter)"), SaveAsync);
        Columns = [WentWell, ToImprove, Problems];
        LoadSprints();
    }

    public override string Title => L.T("Retrospectives");

    public ObservableCollection<NamedItem> Sprints { get; } = [];
    [ObservableProperty] private NamedItem? _sprint;
    [ObservableProperty] private string _sprintSummary = "";
    [ObservableProperty] private bool _hasSprint;

    public RetroColumnViewModel WentWell { get; }
    public RetroColumnViewModel ToImprove { get; }
    public RetroColumnViewModel Problems { get; }
    public IReadOnlyList<RetroColumnViewModel> Columns { get; }

    public ObservableCollection<RetroActionRow> Actions { get; } = [];
    public ObservableCollection<FollowUpRow> FollowUps { get; } = [];
    public bool HasFollowUps => FollowUps.Count > 0;

    [NotifyCanExecuteChangedFor(nameof(AddActionCommand))]
    [ObservableProperty] private string _newAction = "";

    [ObservableProperty] private string _followUp = "";
    private string _savedFollowUp = "";

    public override void OnBoardChanged()
    {
        LoadSprints();
    }

    partial void OnSprintChanged(NamedItem? value)
    {
        if (!_loading)
            LoadRetro();
    }

    partial void OnFollowUpChanged(string value)
    {
        if (_loading || value.Trim() == _savedFollowUp.Trim())
            return;
        _savedFollowUp = value;
        _ = SaveAsync();
    }

    // The sprints that have run (active and closed), the latest first; the selection stays when it can.
    private void LoadSprints()
    {
        var board = Context.Snapshot;
        var selected = Sprint?.Id;
        _loading = true;
        try
        {
            Sprints.Clear();
            foreach (var s in board.Sprints.Values.Where(s => s.State != SprintState.Future)
                         .OrderByDescending(s => s.State == SprintState.Closed ? 0 : 1)
                         .ThenByDescending(s => s.Completed ?? s.Start ?? DateTimeOffset.MinValue))
                Sprints.Add(new NamedItem(s.Id, s.State == SprintState.Active ? $"{s.Name} (running)" : s.Name));
            // By default the last sprint that finished: the one a retrospective is about.
            Sprint = Sprints.FirstOrDefault(s => s.Id == selected)
                     ?? Sprints.FirstOrDefault(s => board.Sprints[s.Id!].State == SprintState.Closed)
                     ?? Sprints.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
        LoadRetro();
    }

    private void LoadRetro()
    {
        var board = Context.Snapshot;
        HasSprint = Sprint?.Id is not null;
        _loading = true;
        try
        {
            Actions.Clear();
            FollowUps.Clear();
            if (Sprint?.Id is not { } id || board.Sprints.GetValueOrDefault(id) is not { } sprint)
            {
                WentWell.Load([]);
                ToImprove.Load([]);
                Problems.Load([]);
                FollowUp = _savedFollowUp = "";
                SprintSummary = L.T("No sprint has started yet: a retrospective follows a sprint.");
                return;
            }
            var retro = Retrospectives.Of(board, id);
            WentWell.Load(retro.WentWell);
            ToImprove.Load(retro.ToImprove);
            Problems.Load(retro.Problems);
            IReadOnlyList<NamedItem> people = [new NamedItem(null, L.T("No owner")), .. board.Project.People.Select(NamedItem.Person)];
            foreach (var action in retro.Actions)
                Actions.Add(ActionRow(action, people, board));
            FollowUp = _savedFollowUp = retro.FollowUp;

            foreach (var (from, action) in Retrospectives.OpenActionsBefore(board, id))
            {
                var fromSprint = board.Sprints[from.Sprint].Name;
                var owner = board.Project.Person(action.Owner)?.Name;
                FollowUps.Add(new FollowUpRow(action.Text, owner is null ? fromSprint : $"{fromSprint} · {owner}",
                    done => Context.RunAsync(new SetRetroActionDone(from.Sprint, action.Id, done))));
            }

            SprintSummary = string.Join(" · ", new[]
            {
                sprint.Goal.Length > 0 ? "Goal: " + sprint.Goal : null,
                sprint.State == SprintState.Closed ? $"committed {sprint.CommittedPoints:0.##} points, completed {sprint.CompletedPoints:0.##}" : L.T("still running"),
                retro.Updated == default ? L.T("not held yet") : null,
            }.OfType<string>());
        }
        finally
        {
            _loading = false;
            OnPropertyChanged(nameof(HasFollowUps));
        }
    }

    private RetroActionRow ActionRow(RetroAction action, IReadOnlyList<NamedItem> people, BoardSnapshot board)
    {
        RetroActionRow? row = null;
        row = new RetroActionRow(action, people, board, SaveAsync)
        {
            RemoveCommand = new AsyncRelayCommand(async () =>
            {
                Actions.Remove(row!);
                await SaveAsync();
            }),
            CreateIssueCommand = new AsyncRelayCommand(() => CreateIssueAsync(row!)),
            OpenIssueCommand = new RelayCommand(() => { if (row!.IssueId is { } issue) Host.SelectIssue(issue); }),
        };
        return row;
    }

    private bool CanAddAction() => !string.IsNullOrWhiteSpace(NewAction);

    [RelayCommand(CanExecute = nameof(CanAddAction))]
    private async Task AddActionAsync()
    {
        IReadOnlyList<NamedItem> people = [new NamedItem(null, L.T("No owner")), .. Context.Project.People.Select(NamedItem.Person)];
        Actions.Add(ActionRow(new RetroAction { Text = NewAction.Trim() }, people, Context.Snapshot));
        NewAction = "";
        await SaveAsync();
    }

    /// <summary>A task for the action (assigned to its owner, in the backlog), linked from the action.</summary>
    private async Task CreateIssueAsync(RetroActionRow row)
    {
        var project = Context.Project;
        var type = project.IssueTypes.FirstOrDefault(t => t.Id == "task" && t.Level == IssueTypeLevel.Standard)
                   ?? project.IssueTypes.FirstOrDefault(t => t.Level == IssueTypeLevel.Standard);
        if (type is null || row.HasIssue)
            return;
        var id = BoardIds.New();
        IReadOnlyList<(string, FieldValue)> fields = row.Owner.Id is { } owner
            ? [(IssueFields.Assignee, new FieldValue.Text(owner))]
            : [];
        if (!await Context.RunAsync(new CreateIssue(type.Id, row.Text.Trim())
            {
                Id = id,
                Description = L.T($"Improvement action from the retrospective of {Sprint?.Name}."),
                Fields = fields,
            }))
            return;
        row.IssueId = id;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (_loading || Sprint?.Id is not { } id)
            return;
        await Context.RunAsync(new SaveRetrospective(new Retrospective
        {
            Sprint = id,
            WentWell = WentWell.ToNotes(),
            ToImprove = ToImprove.ToNotes(),
            Problems = Problems.ToNotes(),
            Actions = Actions.Select(a => a.ToAction()).ToList(),
            FollowUp = FollowUp,
        }));
    }

    private string? Markdown() =>
        Sprint?.Id is { } id ? Retrospectives.ToMarkdown(Context.Snapshot, Retrospectives.Of(Context.Snapshot, id)) : null;

    [RelayCommand]
    private async Task SaveMarkdownAsync()
    {
        if (Markdown() is not { } markdown)
            return;
        var path = await Context.Dialogs.PickSaveFileAsync(L.T("Save Retrospective"), L.T($"Retrospective {Context.Snapshot.Sprints[Sprint!.Id!].Name}.md"), L.T("Markdown"), "md");
        if (path is null)
            return;
        await File.WriteAllTextAsync(path, markdown);
        Context.Status.Show(L.T($"Saved {Path.GetFileName(path)}"));
    }

    [RelayCommand]
    private async Task CopyMarkdownAsync()
    {
        if (Markdown() is { } markdown)
        {
            await _export.CopyTextAsync(markdown);
            Context.Status.Show(L.T("Retrospective copied as Markdown"));
        }
    }
}
