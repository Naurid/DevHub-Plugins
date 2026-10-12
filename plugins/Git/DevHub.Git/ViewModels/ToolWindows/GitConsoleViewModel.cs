using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Git.Core;

namespace DevHub.ViewModels.ToolWindows;

/// <summary>
/// Git Console (bottom tool window): every git command DevHub ran, newest last, for all
/// repositories. Rule from the Git plan: nothing happens behind the user's back.
/// </summary>
public sealed partial class GitConsoleViewModel : ToolWindowViewModelBase, IDisposable
{
    private const int MaxRows = 500;

    private readonly IGitCommandLog _log;

    public override string Id => "gitConsole";
    public override string Title => "Git Console";
    public override string IconKind => Controls.IconKind.Vcs;
    public override ToolWindowSlot Slot => ToolWindowSlot.Bottom;

    /// <summary>One row per command, oldest first.</summary>
    public ObservableCollection<GitConsoleRow> Rows { get; } = new();

    public bool HasRows => Rows.Count > 0;

    public GitConsoleViewModel(IGitCommandLog log)
    {
        _log = log;
        foreach (var entry in _log.Entries)
            Rows.Add(new GitConsoleRow(entry));

        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
        // Unsubscribed in Dispose (DI disposes singletons at shutdown).
        _log.EntryAdded += OnEntryAdded;
    }

    private void OnEntryAdded(object? sender, GitCommandEntry entry)
    {
        // Commands finish on background threads; collections bound to the UI change on the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            Rows.Add(new GitConsoleRow(entry));
            while (Rows.Count > MaxRows)
                Rows.RemoveAt(0);
        });
    }

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        Rows.Clear();
    }

    public void Dispose() => _log.EntryAdded -= OnEntryAdded;
}

/// <summary>A Git Console line, preformatted for display.</summary>
public sealed class GitConsoleRow(GitCommandEntry entry)
{
    public string Time { get; } = entry.Started.ToLocalTime().ToString("HH:mm:ss");
    public string Repository { get; } = Path.GetFileName(entry.RepositoryPath.TrimEnd('\\', '/'));
    public string RepositoryPath { get; } = entry.RepositoryPath;
    public string CommandLine { get; } = entry.CommandLine;
    public string Duration { get; } = $"{entry.Duration.TotalMilliseconds:0} ms";
    /// <summary>A real failure (red). Expected non-zero exits (AllowFailure probes) are not.</summary>
    public bool Failed { get; } = !entry.Succeeded && !entry.FailureExpected;

    /// <summary>"exit 128", "cancelled" or empty for success.</summary>
    public string Result { get; } = entry.ExitCode switch
    {
        0 => "",
        -1 => "cancelled",
        var code => $"exit {code}",
    };

    /// <summary>Output (stderr first), shown as a tooltip.</summary>
    public string Output { get; } = string.IsNullOrWhiteSpace(entry.Output) ? "(no output)" : entry.Output.Trim();

    public override string ToString() => $"{Repository}: {CommandLine}";
}
