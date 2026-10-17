using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Dialogs;

/// <summary>A labelled drop-down inside <see cref="TextInputDialogViewModel"/>.</summary>
public sealed partial class ChoiceInput(string label, IReadOnlyList<string> options, string selected) : ObservableObject
{
    public string Label { get; } = label;
    public IReadOnlyList<string> Options { get; } = options;
    [ObservableProperty] private string _selected = selected;

    /// <summary>The position of the chosen option: compare this, not the (translated) text.</summary>
    public int SelectedIndex => Options.ToList().IndexOf(Selected);
}

/// <summary>
/// A small dialog for the board's many one-field questions: a text (rename, name a column), optionally a second
/// text (WIP minimum), up to two drop-downs and an explanation. OK is refused while a required text is empty.
/// </summary>
public sealed partial class TextInputDialogViewModel : DialogViewModelBase
{
    public TextInputDialogViewModel(string title, string label, string text, string okText)
    {
        Title = title;
        Label = label;
        _text = text;
        OkText = okText;
    }

    public override string Title { get; }
    public override string OkText { get; }
    public override bool FitsContent => true;
    public override double Width => 460;
    public override double MinWidth => 360;
    public override double MinHeight => 0;
    public override bool IsDestructive => _destructive;

    private bool _destructive;
    /// <summary>Destructive OK (red): deleting.</summary>
    public bool Destructive { init => _destructive = value; }

    public string Label { get; }
    [ObservableProperty] private string _text;

    public bool ShowText { get; init; } = true;
    public bool AllowEmpty { get; init; }
    public string? Explanation { get; init; }
    public bool HasExplanation => !string.IsNullOrEmpty(Explanation);

    public string? SecondLabel { get; init; }
    [ObservableProperty] private string _secondText = "";
    public bool ShowSecondText => SecondLabel is not null;

    public ChoiceInput? Choice { get; init; }
    public ChoiceInput? SecondChoice { get; init; }
    public bool HasChoice => Choice is not null;
    public bool HasSecondChoice => SecondChoice is not null;

    [ObservableProperty] private string? _error;


    protected override Task<bool> ApplyChangesAsync()
    {
        if (ShowText && !AllowEmpty && string.IsNullOrWhiteSpace(Text))
        {
            Error = L.T($"Enter {Label.ToLowerInvariant()}.");
            return Task.FromResult(false);
        }
        Text = Text.Trim();
        return Task.FromResult(true);
    }
}

/// <summary>A row of <see cref="ChecklistDialogViewModel"/>.</summary>
public sealed partial class ChecklistItem(string id, string name, bool isChecked, string? note = null) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string? Note { get; } = note;
    public bool HasNote => !string.IsNullOrEmpty(Note);
    [ObservableProperty] private bool _isChecked = isChecked;
    public override string ToString() => Name;
}

/// <summary>Pick several things from a list (a column's statuses, the fields on a card).</summary>
public sealed class ChecklistDialogViewModel(string title, string explanation, IReadOnlyList<ChecklistItem> items) : DialogViewModelBase
{
    public override string Title { get; } = title;
    public override string OkText => L.T("Save");
    public override double Width => 460;
    public override double Height => 460;

    public string Explanation { get; } = explanation;
    public ObservableCollection<ChecklistItem> Items { get; } = new(items);

    public IReadOnlyList<string> CheckedIds => Items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
}
