using System.Diagnostics;
using DevHub.Board;

namespace DevHub.Services.Board;

/// <summary>What board tabs and their parts need, in one bundle (registered once, handed to every board ViewModel).</summary>
public sealed class BoardUiServices(
    BoardService boards,
    IDialogService dialogs,
    IStatusService status,
    IEnumerable<IBoardPageProvider> pages,
    IEnumerable<IIssueSectionProvider> sections,
    IEnumerable<IIssueActionProvider> issueActions)
{
    public BoardService Boards { get; } = boards;
    public IDialogService Dialogs { get; } = dialogs;
    public IStatusService Status { get; } = status;

    /// <summary>Board's own pages plus those of plugins, in order (which replace which is decided per board).</summary>
    public IReadOnlyList<IBoardPageProvider> Pages { get; } = pages.OrderBy(p => p.Order).ToList();

    /// <summary>
    /// The pages a board shows: those that apply to it, minus any an applying page replaces (Board.Scrum's sprint
    /// planning replaces the plain backlog on Scrum boards only).
    /// </summary>
    public IReadOnlyList<IBoardPageProvider> PagesFor(DevHub.Board.Core.Model.BoardDefinition definition)
    {
        var applying = Pages.Where(p => p.AppliesTo(definition)).ToList();
        var replaced = applying.Select(p => p.Replaces).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return applying.Where(p => !replaced.Contains(p.Id)).ToList();
    }

    public IReadOnlyList<IIssueSectionProvider> Sections { get; } = sections.OrderBy(s => s.Order).ToList();
    public IReadOnlyList<IIssueActionProvider> IssueActions { get; } = issueActions.ToList();

    /// <summary>Saves an attachment to a temporary file and opens it with its Windows app.</summary>
    public void OpenFile(string fileName, byte[] bytes)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "DevHub-attachments", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Path.GetFileName(fileName));
            File.WriteAllBytes(path, bytes);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status.Show("The file couldn't be opened: " + e.Message);
        }
    }
}
