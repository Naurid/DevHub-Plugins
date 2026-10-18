using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Services.Board;
using DevHub.ViewModels;

namespace DevHub.Board;

// Extension points of the Board plugin for the plugins that require it (Board.Scrum, Board.Git, Board.Reports,
// Board.Timeline). They reference DevHub.Board and register these in their ConfigureServices; a plugin that's off
// registers nothing, so its pages and sections simply aren't there.

/// <summary>
/// A page of a board tab (Board, Backlog, Search are Board's own; Scrum's planning, Reports, Timeline come from
/// plugins). <see cref="Replaces"/> swaps out another page: Board.Scrum's backlog with sprints replaces the plain one.
/// </summary>
public interface IBoardPageProvider
{
    /// <summary>Stable id ("board", "backlog", "search", "reports").</summary>
    string Id { get; }

    string Title { get; }

    /// <summary>Position among the pages (Board 100, Backlog 200, Search 900).</summary>
    int Order { get; }

    /// <summary>The id of a page this one takes the place of, or null.</summary>
    string? Replaces => null;

    /// <summary>Whether the page makes sense for this board (a sprint page only on Scrum boards).</summary>
    bool AppliesTo(BoardDefinition definition) => true;

    BoardPageViewModel Create(BoardPageHost host);
}

/// <summary>What a page gets from its board tab.</summary>
public sealed partial class BoardPageHost : ObservableObject
{
    public BoardPageHost(BoardContext context, BoardDefinition definition)
    {
        Context = context;
        _definition = definition;
    }

    public BoardContext Context { get; }

    /// <summary>The board definition the tab shows (the board selector changes it).</summary>
    [ObservableProperty] private BoardDefinition _definition;

    /// <summary>Shows an issue in the tab's issue panel.</summary>
    public Action<string?> SelectIssue { get; init; } = _ => { };

    /// <summary>Shows several selected issues in the panel (fields shared or "—"); fewer than two puts it away.</summary>
    public Action<IReadOnlyList<string>> SelectIssues { get; init; } = _ => { };

    /// <summary>Opens an issue in its own tab.</summary>
    public Action<string> OpenIssue { get; init; } = _ => { };

    /// <summary>Opens the Create Issue dialog with some fields filled in.</summary>
    public Func<CreateIssueDefaults, Task> CreateIssue { get; init; } = _ => Task.CompletedTask;

    /// <summary>Switches the tab to another page by id.</summary>
    public Action<string> ShowPage { get; init; } = _ => { };
}

/// <summary>Fields the Create Issue dialog starts with.</summary>
public sealed record CreateIssueDefaults
{
    public string? Type { get; init; }
    public string? Status { get; init; }
    public string? Parent { get; init; }
    public string? Sprint { get; init; }
    public string? Assignee { get; init; }
    public string? RankAbove { get; init; }
}

/// <summary>Base of a board page's ViewModel. Pages are recreated when the board definition changes.</summary>
public abstract class BoardPageViewModel(BoardPageHost host) : ViewModelBase, IDisposable
{
    public BoardPageHost Host { get; } = host;
    public BoardContext Context => Host.Context;

    public abstract string Title { get; }

    /// <summary>Called on the UI thread after the board changed (own change or reload).</summary>
    public virtual void OnBoardChanged()
    {
    }

    /// <summary>The tab's search box text (pages that filter by it override this).</summary>
    public virtual void OnSearch(string text)
    {
    }

    /// <summary>The page became visible (true) or hidden.</summary>
    public virtual void OnShown(bool shown)
    {
    }

    public virtual void Dispose()
    {
    }

    public override string ToString() => Title;
}

/// <summary>A section of the issue view contributed by another plugin (Board.Git: Development).</summary>
public interface IIssueSectionProvider
{
    /// <summary>Position among the sections below the fields (Board's sub-tasks 100, links 200, attachments 300).</summary>
    int Order { get; }

    /// <summary>The section for an issue of this board, or null when it has nothing to show here.</summary>
    IssueSectionViewModel? Create(BoardContext context, string issueId);
}

/// <summary>Base of an issue section's ViewModel.</summary>
public abstract class IssueSectionViewModel(BoardContext context, string issueId) : ViewModelBase, IDisposable
{
    public BoardContext Context { get; } = context;
    public string IssueId { get; } = issueId;

    public abstract string Title { get; }

    /// <summary>The issue (or the board) changed.</summary>
    public virtual void Update(Issue issue, BoardSnapshot board)
    {
    }

    public virtual void Dispose()
    {
    }
}

/// <summary>
/// Actions on an issue contributed by other plugins (Board.Git: "Create Branch"), shown in the issue view's
/// header menu and the card's context menu.
/// </summary>
public interface IIssueActionProvider
{
    IEnumerable<IssueAction> GetActions(BoardContext context, Issue issue);
}

/// <summary>An issue action: a label and what it does.</summary>
public sealed record IssueAction(string Text, Func<Task> Run)
{
    public bool IsEnabled { get; init; } = true;
}
