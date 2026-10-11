using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Rendering;

namespace DevHub.ViewModels.Templates;

/// <summary>The steps shown above the pages: Details → Commands → Preview → Done.</summary>
public sealed class StepIndicatorViewModel(string number, string text, bool isCurrent, bool isDone) : ViewModelBase
{
    public string Number { get; } = number;
    public string Text { get; } = text;
    public bool IsCurrent { get; } = isCurrent;
    public bool IsDone { get; } = isDone;
    public bool IsLast => Number == "4";

    public override string ToString() => $"Step {Number}: {Text}{(IsCurrent ? " (current)" : IsDone ? " (done)" : "")}";

    /// <summary>The four steps with <paramref name="current"/> (0-based) highlighted.</summary>
    public static IReadOnlyList<StepIndicatorViewModel> For(int current) =>
        new[] { "Details", "Commands", "Preview", "Done" }
            .Select((text, i) => new StepIndicatorViewModel((i + 1).ToString(), text, i == current, i < current))
            .ToList();
}

/// <summary>
/// Step 2: every command the generation will run, rendered, before anything runs (§4: templates
/// can be shared, and their commands run on your computer).
/// </summary>
public sealed partial class CommandsPageViewModel(string templateName, string targetPath, bool raw,
    IReadOnlyList<RenderedCommand> commands, Action back, Action run) : ViewModelBase
{
    public string TemplateName { get; } = templateName;
    public string TargetPath { get; } = targetPath;

    public IReadOnlyList<CommandRowViewModel> Commands { get; } = commands.Select(c => new CommandRowViewModel(c)).ToList();

    public string Intro => raw
        ? "Raw project: only the base command runs. Nothing runs until you click Run."
        : "These commands run on your computer, in this order. Only continue if you trust this template. Nothing runs until you click Run.";

    [RelayCommand]
    private void Back() => back();

    [RelayCommand]
    private void Run() => run();
}

/// <summary>One command in the confirmation list.</summary>
public sealed class CommandRowViewModel(RenderedCommand command) : ViewModelBase
{
    public string Name { get; } = command.Name;
    public string Text { get; } = command.DisplayText;

    /// <summary>"in the project folder", "in src", or "creates the project folder" for a base command run in the parent.</summary>
    public string Where { get; } = command.WorkingDirectory is "." or "" ? "in the project folder" : "in " + command.WorkingDirectory;

    public bool ContinuesOnError { get; } = command.ContinueOnError;

    public override string ToString() => $"{Name}: {Text}";
}

/// <summary>A live log of command output (stderr lines marked). Keeps the last lines only.</summary>
public sealed class OutputLogViewModel : ViewModelBase
{
    private const int MaxLines = 1000;

    public ObservableCollection<OutputLineViewModel> Lines { get; } = [];

    public void Add(string text, bool isError = false, bool isHeader = false)
    {
        if (Lines.Count >= MaxLines)
            Lines.RemoveAt(0);
        Lines.Add(new OutputLineViewModel(text, isError, isHeader));
    }

    public void Add(CommandOutputLine line) => Add(line.Text, line.IsError);
}

/// <param name="IsHeader">A "▶ dotnet new console" line the log adds before each command's output.</param>
public sealed class OutputLineViewModel(string text, bool isError, bool isHeader) : ViewModelBase
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
    public bool IsHeader { get; } = isHeader;

    public override string ToString() => Text;
}

/// <summary>
/// While something runs (staging, or the post-create commands): what it's doing, the live output,
/// Cancel. When staging fails it stays with the error and the output, and Back returns to the form.
/// </summary>
public sealed partial class RunPageViewModel(string title, OutputLogViewModel log, Action cancel, Action back) : ViewModelBase
{
    public string Title { get; } = title;
    public OutputLogViewModel Log { get; } = log;

    [ObservableProperty] private string _step = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning = true;

    /// <summary>Why it stopped (nothing was written to your folder), or null.</summary>
    [ObservableProperty] private string? _error;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => cancel();

    [RelayCommand]
    private void Back() => back();
}

/// <summary>Step 4: the project is in place; how the post-create commands went; what to do next.</summary>
public sealed partial class DonePageViewModel(string projectPath, IReadOnlyList<CommandResult> results, bool cancelled,
    OutputLogViewModel log, Action? openInDevHub, Action showInExplorer, Action newProject) : ViewModelBase
{
    public string ProjectPath { get; } = projectPath;
    public string ProjectName { get; } = Path.GetFileName(projectPath);

    public IReadOnlyList<CommandResultRowViewModel> Results { get; } = results.Select(r => new CommandResultRowViewModel(r)).ToList();
    public bool HasResults => Results.Count > 0;
    public bool AllSucceeded => !cancelled && results.All(r => r.Succeeded);

    public string Title => AllSucceeded ? $"{ProjectName} is ready" : $"{ProjectName} was created, but not every command finished";

    public string Summary => cancelled ? "You cancelled the post-create commands; the project's files are in place."
        : AllSucceeded ? "The project's files are in place" + (HasResults ? " and every command ran." : ".")
        : "The project's files are in place. A command failed (see below); you can run it yourself in the project folder.";

    public OutputLogViewModel Log { get; } = log;
    public bool HasLog => Log.Lines.Count > 0;

    public bool CanOpenInDevHub => openInDevHub is not null;

    [RelayCommand]
    private void OpenInDevHub() => openInDevHub?.Invoke();

    [RelayCommand]
    private void ShowInExplorer() => showInExplorer();

    [RelayCommand]
    private void NewProject() => newProject();
}

/// <summary>How one post-create command ended.</summary>
public sealed class CommandResultRowViewModel(CommandResult result) : ViewModelBase
{
    public string Name { get; } = result.Command.Name;
    public string Text { get; } = result.Command.DisplayText;
    public bool Succeeded { get; } = result.Succeeded;
    public string? Error { get; } = result.Error;
    public string IconKind { get; } = result.Succeeded ? Controls.IconKind.Success : Controls.IconKind.Error;

    public override string ToString() => Succeeded ? $"{Name}: done" : $"{Name}: {Error}";
}
