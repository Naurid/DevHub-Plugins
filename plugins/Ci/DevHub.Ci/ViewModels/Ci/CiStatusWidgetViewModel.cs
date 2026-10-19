using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Ci.Core;
using DevHub.Sdk.Core.Localization;
using DevHub.Services.Ci;
using DevHub.ViewModels.Shell;

namespace DevHub.ViewModels.Ci;

/// <summary>
/// The status bar's CI mark: the latest runs of the branch checked out in the repository tab in front, as ✓ CI, ✗ CI or
/// ● CI in the status colours (tooltip: each workflow and its state). A click shows the CI tool window.
/// </summary>
public sealed partial class CiStatusWidgetViewModel : StatusBarWidgetViewModelBase, IDisposable
{
    private readonly CiService _service;
    private readonly CiToolWindowViewModel _window;

    public CiStatusWidgetViewModel(CiService service, CiToolWindowViewModel window)
    {
        _service = service;
        _window = window;
        // Unsubscribed in Dispose (DI disposes singletons at shutdown).
        _service.RunsChanged += OnRunsChanged;
        _service.ActiveChanged += OnActiveChanged;
    }

    public override int Order => 40;

    [ObservableProperty] private string? _text;
    [ObservableProperty] private string? _toolTip;
    [ObservableProperty] private bool _isPassed;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isActive;

    public bool IsVisible => Text is not null;

    partial void OnTextChanged(string? value) => OnPropertyChanged(nameof(IsVisible));

    private void OnRunsChanged(object? sender, string folder) => Update();

    private void OnActiveChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var folder = _service.ActiveFolder;
        var repo = _service.Repository(folder);
        var branch = folder is null ? null : GitHubRemote.CurrentBranch(folder);
        var runs = repo is null || branch is null ? [] : CiService.LatestOf(repo.Runs, branch);
        if (runs.Count == 0)
        {
            Text = ToolTip = null;
            IsPassed = IsFailed = IsActive = false;
            return;
        }
        var state = CiService.Combined(runs);
        Text = $"{StateLook.Symbol(state)} CI";
        IsPassed = state == RunState.Passed;
        IsFailed = state == RunState.Failed;
        IsActive = state.IsActive();
        ToolTip = L.T($"CI on {branch}:") + "\n" + string.Join("\n", runs.Select(r => $"{StateLook.Symbol(r.State)} {r.Workflow} #{r.Number}: {CiService.StateText(r.State)}"));
    }

    [RelayCommand]
    private void ShowRuns()
    {
        if (!_window.IsOpen)
            _window.ToggleCommand?.Execute(null);
    }

    public void Dispose()
    {
        _service.RunsChanged -= OnRunsChanged;
        _service.ActiveChanged -= OnActiveChanged;
    }
}
