using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Services.Board;
using DevHub.ViewModels.Dialogs;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Board;

/// <summary>Issue operations several screens offer (card menu, issue view, backlog), with their confirmations.</summary>
public static class IssueCommands
{
    /// <summary>Asks, then deletes an issue (and its sub-tasks, which the question names).</summary>
    public static async Task<bool> DeleteAsync(BoardContext context, string issueId)
    {
        var snapshot = context.Snapshot;
        if (snapshot.Issue(issueId) is not { } issue)
            return false;
        var children = snapshot.ChildrenOf(issue.Id);
        var subtasks = children.Where(c => snapshot.LevelOf(c) == IssueTypeLevel.Subtask).ToList();
        var epicIssues = children.Count - subtasks.Count;
        var text = L.T($"Delete {issue.Key} \"{issue.Summary}\" with its comments, work log, attachments and history?");
        if (subtasks.Count > 0)
            text += L.T($" Its {subtasks.Count} sub-task(s) are deleted too.");
        if (epicIssues > 0)
            text += L.T($" Its {epicIssues} issue(s) stay, without an epic.");
        text += L.T(" Undo (Board menu) brings it back right after.");
        var ok = await context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel(L.T("Delete Issue"), text, L.T("Delete"),
            [$"{issue.Key} {issue.Summary}", .. subtasks.Select(s => $"{s.Key} {s.Summary}")]));
        return ok && await context.RunAsync(new DeleteIssue(issue.Id, WithSubtasks: subtasks.Count > 0));
    }

    /// <summary>Makes sure you're on the board (adds you), returning your person id.</summary>
    public static async Task<string?> MeAsync(BoardContext context)
    {
        if (context.Me is null)
            await context.RunAsync(new AddPerson(context.Service.User.Name, context.Service.User.Email));
        return context.Me;
    }
}
