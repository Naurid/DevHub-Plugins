using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Git.Core;
using DevHub.Services;
using DevHub.ViewModels.Dialogs;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Git;

/// <summary>
/// Manage Remotes (JetBrains' Git → Manage Remotes): the repository's remotes with their URLs;
/// Add…, Edit… (name and URL), Remove… (asks). Each change happens right away; Close ends it.
/// </summary>
public sealed partial class RemotesDialogViewModel : DialogViewModelBase
{
    private readonly GitRepositoryClient _client;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;

    public RemotesDialogViewModel(GitRepositoryClient client, IDialogService dialogs, IStatusService status)
    {
        _client = client;
        _dialogs = dialogs;
        _status = status;
        _ = LoadAsync();
    }

    public override string Title => L.T("Remotes");
    public override string OkText => L.T("Close");
    public override bool ShowCancel => false;
    public override double Width => 640;
    public override double Height => 380;
    public override double MinHeight => 260;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemotes))]
    private IReadOnlyList<GitRemote> _remotes = [];

    public bool HasRemotes => Remotes.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(RemoveCommand))]
    private GitRemote? _selected;

    [ObservableProperty] private string? _error;

    /// <summary>A remote was added: the caller fetches afterwards, so its branches show up.</summary>
    public bool AddedRemote { get; private set; }

    /// <summary>Anything changed (the caller refreshes the repository).</summary>
    public bool Changed { get; private set; }

    private async Task LoadAsync(string? select = null)
    {
        try
        {
            var keep = select ?? Selected?.Name;
            Remotes = await _client.GetRemoteDetailsAsync();
            Selected = Remotes.FirstOrDefault(r => r.Name == keep) ?? Remotes.FirstOrDefault();
            Error = null;
        }
        catch (Exception ex)
        {
            Error = L.T("Couldn't read the remotes: ") + Describe(ex);
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var dialog = new RemoteEditDialogViewModel(_client, Remotes, null);
        if (!await _dialogs.ShowDialogAsync(dialog))
            return;
        await RunAsync(async () =>
        {
            await _client.AddRemoteAsync(dialog.Name, dialog.Url);
            AddedRemote = true;
            _status.Show(L.T($"Added the remote {dialog.Name}."));
        }, select: dialog.Name);
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        var remote = Selected!;
        var dialog = new RemoteEditDialogViewModel(_client, Remotes, remote);
        if (!await _dialogs.ShowDialogAsync(dialog))
            return;
        await RunAsync(async () =>
        {
            if (dialog.Name != remote.Name)
                await _client.RenameRemoteAsync(remote.Name, dialog.Name);
            if (dialog.Url != remote.FetchUrl)
                await _client.SetRemoteUrlAsync(dialog.Name, dialog.Url);
            _status.Show(L.T($"Saved the remote {dialog.Name}."));
        }, select: dialog.Name);
    }

    /// <summary>Removes the remote here (asks): its remote branches disappear locally; the server is untouched.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        var remote = Selected!;
        var confirm = new ConfirmDialogViewModel(L.T("Remove Remote"),
            L.T($"Remove the remote {remote.Name} ({remote.FetchUrl})? Its remote branches disappear from this repository and your branches stop tracking them. Nothing changes on the server; you can add it again later."), L.T("Remove"));
        if (!await _dialogs.ShowDialogAsync(confirm))
            return;
        await RunAsync(async () =>
        {
            await _client.RemoveRemoteAsync(remote.Name);
            _status.Show(L.T($"Removed the remote {remote.Name}."));
        });
    }

    private async Task RunAsync(Func<Task> operation, string? select = null)
    {
        try
        {
            await operation();
            Changed = true;
            Error = null;
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
        }
        await LoadAsync(select);
    }

    private static string Describe(Exception ex) =>
        ex is GitException git && git.StandardError.Trim() is { Length: > 0 } e ? e.Split('\n')[0].Trim() : ex.Message;
}

/// <summary>
/// Add Remote / Edit Remote: a name (checked while typing: empty, spaces, already used; OK then
/// runs git's own rules) and a URL (https://…, git@host:…, or a folder).
/// </summary>
public sealed partial class RemoteEditDialogViewModel : DialogViewModelBase
{
    private readonly GitRepositoryClient _client;
    private readonly IReadOnlyList<GitRemote> _existing;
    private readonly GitRemote? _editing;

    /// <param name="editing">The remote being edited, or null to add one.</param>
    public RemoteEditDialogViewModel(GitRepositoryClient client, IReadOnlyList<GitRemote> existing, GitRemote? editing)
    {
        _client = client;
        _existing = existing;
        _editing = editing;
        _name = editing?.Name ?? (existing.Any(r => r.Name == "origin") ? "" : "origin");
        _url = editing?.FetchUrl ?? "";
    }

    public override string Title => _editing is null ? L.T("Add Remote") : L.T($"Edit Remote {_editing.Name}");
    public override string OkText => _editing is null ? L.T("Add") : L.T("Save");
    public override bool FitsContent => true;
    public override double Width => 520;
    public override double MinWidth => 400;
    public override double MinHeight => 0;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string? _error;

    partial void OnNameChanged(string value) => Validate();

    private void Validate()
    {
        var name = Name.Trim();
        Error = name.Length == 0 ? null
            : name.Any(char.IsWhiteSpace) ? L.T("Remote names can't contain spaces.")
            : _existing.Any(r => r.Name == name && r != _editing) ? L.T($"A remote named {name} already exists.")
            : null;
    }

    protected override async Task<bool> ApplyChangesAsync()
    {
        Name = Name.Trim();
        Url = Url.Trim();
        Validate();
        if (Error is not null)
            return false;
        if (Name.Length == 0 || Url.Length == 0)
        {
            Error = Name.Length == 0 ? L.T("Enter a name (usually \"origin\").") : L.T("Enter the URL (from GitHub: Code → HTTPS).");
            return false;
        }
        if (!await _client.IsValidRemoteNameAsync(Name))
        {
            Error = L.T("git doesn't allow that name (letters, digits, \"-\" and \"_\" are safe).");
            return false;
        }
        return true;
    }
}
