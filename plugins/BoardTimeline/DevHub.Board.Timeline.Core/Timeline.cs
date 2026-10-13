using System.Globalization;
using System.Text;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;

namespace DevHub.Board.Timeline.Core;

/// <summary>
/// A row of the roadmap: an epic (or one of its issues) from start to end. Dates not set on the issue are worked out
/// (<see cref="DatesGuessed"/>): from its issues' dates, else from when it was created, two weeks long.
/// </summary>
public sealed record TimelineItem(
    Issue Issue,
    DateOnly Start,
    DateOnly End,
    bool DatesGuessed,
    double Progress,
    bool IsDone,
    bool IsLate,
    IReadOnlyList<Issue> BlockedBy,
    bool HasConflict,
    int Depth)
{
    public int Days => End.DayNumber - Start.DayNumber + 1;
}

/// <summary>The roadmap: rows, and the date range they span (with some room either side).</summary>
public sealed record TimelineResult(IReadOnlyList<TimelineItem> Items, DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>Where a date falls across the range (0 = <see cref="From"/>, 1 = the end of <see cref="To"/>).</summary>
    public double Fraction(DateOnly date) => Days <= 0 ? 0 : (double)(date.DayNumber - From.DayNumber) / Days;
}

/// <summary>Builds the roadmap of epics (docs/BOARD_PLAN.md, B10).</summary>
public static class Roadmap
{
    public const int DefaultLengthDays = 14;

    public static TimelineResult Build(BoardSnapshot board, DateOnly today, bool withIssues = false, bool showDone = true)
    {
        var epics = board.Issues.Values.Where(i => board.LevelOf(i) == IssueTypeLevel.Epic && (showDone || !board.IsDone(i)))
            .OrderBy(i => i.Rank, StringComparer.Ordinal).ToList();
        var items = new List<TimelineItem>();
        foreach (var epic in epics)
        {
            items.Add(ItemFor(board, epic, today, 0));
            if (!withIssues)
                continue;
            foreach (var child in board.ChildrenOf(epic.Id).Where(c => showDone || !board.IsDone(c)))
                items.Add(ItemFor(board, child, today, 1));
        }
        var from = items.Count == 0 ? today : items.Min(i => i.Start);
        var to = items.Count == 0 ? today.AddDays(90) : items.Max(i => i.End);
        if (from > today)
            from = today;
        return new TimelineResult(items, from.AddDays(-3), to.AddDays(7));
    }

    private static TimelineItem ItemFor(BoardSnapshot board, Issue issue, DateOnly today, int depth)
    {
        var children = board.ChildrenOf(issue.Id);
        var created = DateOnly.FromDateTime(issue.Created.LocalDateTime);
        var start = issue.Start ?? children.Select(c => c.Start).Where(d => d is not null).Min() ?? created;
        var end = issue.Due ?? children.Select(c => c.Due).Where(d => d is not null).Max() ?? start.AddDays(DefaultLengthDays - 1);
        if (end < start)
            end = start;
        var guessed = issue.Start is null || issue.Due is null;
        var done = board.IsDone(issue);
        double progress;
        if (children.Count > 0)
        {
            var points = children.Sum(c => c.StoryPoints ?? 0);
            progress = points > 0
                ? children.Where(board.IsDone).Sum(c => c.StoryPoints ?? 0) / points
                : (double)children.Count(board.IsDone) / children.Count;
        }
        else
        {
            progress = done ? 1 : 0;
        }

        // Blockers: issues linked "blocks" to this one (stored on the blocker).
        var blockedBy = board.IncomingLinks(issue.Id).Where(l => l.Link.Type == "blocks" && !board.IsDone(l.From)).Select(l => l.From).ToList();
        var conflict = blockedBy.Any(b => b.Due is { } bd && bd >= start);   // starts before its blocker is due
        return new TimelineItem(issue, start, end, guessed, progress, done, !done && end < today, blockedBy, conflict, depth);
    }
}

/// <summary>A release (fix version) and how far it is.</summary>
public sealed record ReleaseStatus(FixVersion Version, IReadOnlyList<Issue> Issues, int Done, bool IsLate)
{
    public int Open => Issues.Count - Done;
    public double Fraction => Issues.Count == 0 ? 0 : (double)Done / Issues.Count;
}

/// <summary>Releases and their notes.</summary>
public static class Releases
{
    public static IReadOnlyList<ReleaseStatus> All(BoardSnapshot board, DateOnly today, bool showArchived = false) =>
        board.Versions.Values.Where(v => showArchived || !v.Archived)
            .OrderBy(v => v.Released).ThenBy(v => v.Release ?? DateOnly.MaxValue).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(v =>
            {
                var issues = board.Issues.Values.Where(i => i.FixVersions.Contains(v.Id)).OrderBy(i => i.Rank, StringComparer.Ordinal).ToList();
                return new ReleaseStatus(v, issues, issues.Count(board.IsDone), !v.Released && v.Release is { } r && r < today);
            }).ToList();

    /// <summary>
    /// Release notes in Markdown: the version and its date, then its done issues grouped by type (epics and sub-tasks
    /// left out), bugs as "Fixed". Unfinished issues are listed at the end when <paramref name="includeOpen"/>.
    /// </summary>
    public static string Notes(BoardSnapshot board, FixVersion version, bool includeOpen = false)
    {
        var project = board.Project;
        var text = new StringBuilder();
        text.Append("## ").Append(version.Name);
        if (version.Release is { } date)
            text.Append(" (").Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(')');
        text.Append("\n\n");
        if (version.Description.Length > 0)
            text.Append(version.Description).Append("\n\n");
        var issues = board.Issues.Values.Where(i => i.FixVersions.Contains(version.Id) && board.LevelOf(i) == IssueTypeLevel.Standard)
            .OrderBy(i => i.Rank, StringComparer.Ordinal).ToList();
        var done = issues.Where(board.IsDone).ToList();
        foreach (var group in done.GroupBy(i => i.Type).OrderBy(g => g.Key == "bug" ? 1 : 0).ThenBy(g => project.IssueType(g.Key)?.Name))
        {
            var heading = group.Key == "bug" ? "Fixed" : (project.IssueType(group.Key)?.Name ?? group.Key) + (group.Count() == 1 ? "" : "s");
            text.Append("### ").Append(heading).Append('\n');
            foreach (var issue in group)
                text.Append("- ").Append(issue.Summary).Append(" (").Append(issue.Key).Append(")\n");
            text.Append('\n');
        }
        if (done.Count == 0)
            text.Append("Nothing is finished in this version yet.\n\n");
        var open = issues.Where(i => !board.IsDone(i)).ToList();
        if (includeOpen && open.Count > 0)
        {
            text.Append("### Not done yet\n");
            foreach (var issue in open)
                text.Append("- ").Append(issue.Summary).Append(" (").Append(issue.Key).Append(")\n");
        }
        return text.ToString().TrimEnd() + "\n";
    }
}

/// <summary>
/// Releases a version: marks it released (today, unless it has a date), and moves its unfinished issues to another
/// version (or takes the version off them, <see cref="MoveOpenTo"/> null).
/// </summary>
public sealed record ReleaseVersion(string VersionId, DateOnly Today) : IBoardOperation
{
    public string? MoveOpenTo { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var version = board.Versions.GetValueOrDefault(VersionId) ?? throw new BoardRuleException("That version doesn't exist any more.");
        if (version.Released)
            throw new BoardRuleException($"{version.Name} is already released.");
        if (MoveOpenTo is not null && (MoveOpenTo == VersionId || !board.Versions.ContainsKey(MoveOpenTo)))
            throw new BoardRuleException("Choose another version for the unfinished issues.");
        var change = new ChangeBuilder(board, context);
        change.Write(BoardPaths.Version(version.Id), version with { Released = true, Release = version.Release ?? Today });
        var open = board.Issues.Values.Where(i => i.FixVersions.Contains(VersionId) && !board.IsDone(i)).ToList();
        foreach (var issue in open)
        {
            var versions = issue.FixVersions.Where(v => v != VersionId).ToList();
            if (MoveOpenTo is not null && !versions.Contains(MoveOpenTo))
                versions.Add(MoveOpenTo);
            change.WriteIssue(issue, issue with { FixVersions = versions });
        }
        var target = MoveOpenTo is null ? "no version" : board.Versions[MoveOpenTo].Name;
        return change.Build(open.Count == 0 ? $"Version {version.Name} released" : $"Version {version.Name} released, {open.Count} unfinished issues moved to {target}");
    }
}
