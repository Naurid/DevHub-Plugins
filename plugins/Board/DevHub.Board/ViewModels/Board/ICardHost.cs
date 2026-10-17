using System.Windows.Input;

namespace DevHub.ViewModels.Board;

/// <summary>
/// What the shared card and row templates (Views/Board/BoardTemplates.axaml) call on the page they're in, through
/// the card's own commands: select an issue (the issue panel shows it), open it in its own tab, and the card menu.
/// A page leaves a menu command null when it doesn't offer it (the item is hidden). Board.Scrum's pages implement
/// it too. Every command gets the card (<see cref="IssueItemViewModel"/>) as its parameter.
/// </summary>
public interface ICardHost
{
    ICommand SelectCommand { get; }
    ICommand OpenCommand { get; }
    ICommand? AssignToMeCommand => null;
    ICommand? DeleteCommand => null;
    ICommand? MoveToTopCommand => null;
    ICommand? MoveToBottomCommand => null;
    ICommand? NextColumnCommand => null;
    ICommand? PreviousColumnCommand => null;

    /// <summary>Ctrl+click: adds the card to (or takes it out of) a selection of several; null where a page has none.</summary>
    ICommand? PickCommand => null;
}
