using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core;

/// <summary>Renames the project (its key stays).</summary>
public sealed record RenameProject(string Name) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Definitions.RequireName(Name, "project");
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { Name = name });
        return change.Build(L.T($"Project renamed to {name}"));
    }
}

/// <summary>
/// Changes the project key (DEV → APP): every issue gets its new key, and keeps the old one in
/// <see cref="Issue.OldKeys"/> so branch names and commit messages that mention it still link.
/// </summary>
public sealed record ChangeProjectKey(string Key) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var key = Key.Trim().ToUpperInvariant();
        if (!IssueKey.IsValidProjectKey(key))
            throw new BoardRuleException(L.T("A project key is 1–10 capital letters or digits, starting with a letter (like DEV)."));
        if (key == board.Project.Key)
            return new BoardChange(L.T("No change"), new Dictionary<string, byte[]?>());
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { Key = key });
        foreach (var issue in board.Issues.Values)
        {
            if (!IssueKey.TryParse(issue.Key, out _, out var number))
                continue;
            var renamed = issue with
            {
                Key = IssueKey.Format(key, number),
                OldKeys = issue.OldKeys.Contains(issue.Key) ? issue.OldKeys : [.. issue.OldKeys, issue.Key],
            };
            change.WriteIssue(issue, renamed);
            change.AddHistory(issue.Id, "key", issue.Key, renamed.Key);
        }
        return change.Build(L.T($"Project key {board.Project.Key} changed to {key}"));
    }
}

/// <summary>How many hours a day and days a week are in time tracking.</summary>
public sealed record SetTimeTracking(double HoursPerDay, double DaysPerWeek) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        if (HoursPerDay is <= 0 or > 24 || DaysPerWeek is <= 0 or > 7)
            throw new BoardRuleException(L.T("A working day is 1–24 hours and a week 1–7 days."));
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { Settings = board.Project.Settings with { HoursPerDay = HoursPerDay, DaysPerWeek = DaysPerWeek } });
        return change.Build(L.T("Time tracking settings changed"));
    }
}

// ---- Issue types ----------------------------------------------------------------------------------------------

public sealed record AddIssueType(string Name, IssueTypeLevel Level, string Icon = "task") : IBoardOperation
{
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Definitions.RequireName(Name, L.T("issue type"));
        Definitions.RequireUnique(board.Project.IssueTypes, t => t.Name, name, L.T("issue type"));
        var change = new ChangeBuilder(board, context);
        var type = new IssueType { Id = Definitions.NewIdFor(change, Id, board.Project.IssueTypes.Select(t => t.Id)), Name = name, Level = Level, Icon = Icon };
        change.WriteProject(board.Project with { IssueTypes = [.. board.Project.IssueTypes, type] });
        return change.Build(L.T($"Issue type {name} added"));
    }
}

/// <summary>Renames an issue type and / or changes its icon.</summary>
public sealed record EditIssueType(string IssueTypeId, string Name, string Icon) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var type = board.Project.IssueType(IssueTypeId) ?? throw new BoardRuleException(L.T("That issue type doesn't exist any more."));
        var name = Definitions.RequireName(Name, L.T("issue type"));
        Definitions.RequireUnique(board.Project.IssueTypes, t => t.Name, name, L.T("issue type"), t => t.Id == IssueTypeId);
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with
        {
            IssueTypes = Definitions.Replace(board.Project.IssueTypes, t => t.Id == IssueTypeId, t => t with { Name = name, Icon = Icon }),
        });
        return change.Build(L.T($"Issue type {type.Name} changed"));
    }
}

/// <summary>Deletes an issue type; its issues become <see cref="ReplaceWith"/> (a type of the same level).</summary>
public sealed record DeleteIssueType(string IssueTypeId, string? ReplaceWith) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var type = board.Project.IssueType(IssueTypeId) ?? throw new BoardRuleException(L.T("That issue type doesn't exist any more."));
        var affected = board.Issues.Values.Where(i => i.Type == IssueTypeId).ToList();
        IssueType? replacement = null;
        if (affected.Count > 0)
        {
            replacement = board.Project.IssueType(ReplaceWith);
            if (replacement is null || replacement.Id == IssueTypeId)
                throw new BoardRuleException(L.T($"{affected.Count} issues are {type.Name}s: choose a type for them."));
            if (replacement.Level != type.Level)
                throw new BoardRuleException(L.T($"{replacement.Name} is on another level than {type.Name} (epic, standard, sub-task)."));
        }
        if (type.Level == IssueTypeLevel.Standard && board.Project.IssueTypes.Count(t => t.Level == IssueTypeLevel.Standard) == 1)
            throw new BoardRuleException(L.T("A board needs at least one standard issue type (like Task)."));
        var change = new ChangeBuilder(board, context);
        var project = board.Project with { IssueTypes = board.Project.IssueTypes.Where(t => t.Id != IssueTypeId).ToList() };
        change.WriteProject(project);
        var updated = BoardSnapshotView.WithProject(board, project);
        foreach (var issue in affected)
        {
            var workflow = updated.WorkflowFor(replacement!.Id);
            var status = workflow is null || workflow.Statuses.Contains(issue.Status) ? issue.Status : workflow.InitialStatus ?? issue.Status;
            change.WriteIssue(issue, CreateIssue.WithDoneState(updated, issue with { Type = replacement.Id }, status, null, context));
        }
        return change.Build(L.T($"Issue type {type.Name} deleted"));
    }
}

// ---- Priorities, resolutions --------------------------------------------------------------------------------

public sealed record AddPriority(string Name, string Icon = "medium") : IBoardOperation
{
    public string? Id { get; init; }

    /// <summary>Position in the list (0 = highest); null = last.</summary>
    public int? Index { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Definitions.RequireName(Name, "priority");
        Definitions.RequireUnique(board.Project.Priorities, p => p.Name, name, "priority");
        var change = new ChangeBuilder(board, context);
        var priority = new Priority { Id = Definitions.NewIdFor(change, Id, board.Project.Priorities.Select(p => p.Id)), Name = name, Icon = Icon };
        var list = board.Project.Priorities.ToList();
        list.Insert(Math.Clamp(Index ?? list.Count, 0, list.Count), priority);
        change.WriteProject(board.Project with { Priorities = list });
        return change.Build(L.T($"Priority {name} added"));
    }
}

public sealed record EditPriority(string PriorityId, string Name, string Icon) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var priority = board.Project.Priority(PriorityId) ?? throw new BoardRuleException(L.T("That priority doesn't exist any more."));
        var name = Definitions.RequireName(Name, "priority");
        Definitions.RequireUnique(board.Project.Priorities, p => p.Name, name, "priority", p => p.Id == PriorityId);
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with
        {
            Priorities = Definitions.Replace(board.Project.Priorities, p => p.Id == PriorityId, p => p with { Name = name, Icon = Icon }),
        });
        return change.Build(L.T($"Priority {priority.Name} changed"));
    }
}

/// <summary>Moves a priority up or down the ranking (index 0 = highest).</summary>
public sealed record MovePriority(string PriorityId, int Index) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { Priorities = Definitions.Move(board.Project.Priorities, p => p.Id == PriorityId, Index) });
        return change.Build(L.T("Priorities reordered"));
    }
}

public sealed record SetDefaultPriority(string PriorityId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var priority = board.Project.Priority(PriorityId) ?? throw new BoardRuleException(L.T("That priority doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { DefaultPriority = PriorityId });
        return change.Build(L.T($"New issues are {priority.Name}"));
    }
}

/// <summary>Deletes a priority; issues with it get <see cref="ReplaceWith"/> (or none).</summary>
public sealed record DeletePriority(string PriorityId, string? ReplaceWith) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var priority = board.Project.Priority(PriorityId) ?? throw new BoardRuleException(L.T("That priority doesn't exist any more."));
        if (ReplaceWith is not null && (ReplaceWith == PriorityId || board.Project.Priority(ReplaceWith) is null))
            throw new BoardRuleException(L.T("Choose another priority for its issues."));
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with
        {
            Priorities = board.Project.Priorities.Where(p => p.Id != PriorityId).ToList(),
            DefaultPriority = board.Project.DefaultPriority == PriorityId ? ReplaceWith : board.Project.DefaultPriority,
        });
        foreach (var issue in board.Issues.Values.Where(i => i.Priority == PriorityId))
            change.WriteIssue(issue, issue with { Priority = ReplaceWith });
        return change.Build(L.T($"Priority {priority.Name} deleted"));
    }
}

public sealed record AddResolution(string Name) : IBoardOperation
{
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Definitions.RequireName(Name, "resolution");
        Definitions.RequireUnique(board.Project.Resolutions, r => r.Name, name, "resolution");
        var change = new ChangeBuilder(board, context);
        var resolution = new Resolution { Id = Definitions.NewIdFor(change, Id, board.Project.Resolutions.Select(r => r.Id)), Name = name };
        change.WriteProject(board.Project with { Resolutions = [.. board.Project.Resolutions, resolution] });
        return change.Build(L.T($"Resolution {name} added"));
    }
}

public sealed record RenameResolution(string ResolutionId, string Name) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var resolution = board.Project.Resolution(ResolutionId) ?? throw new BoardRuleException(L.T("That resolution doesn't exist any more."));
        var name = Definitions.RequireName(Name, "resolution");
        Definitions.RequireUnique(board.Project.Resolutions, r => r.Name, name, "resolution", r => r.Id == ResolutionId);
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with
        {
            Resolutions = Definitions.Replace(board.Project.Resolutions, r => r.Id == ResolutionId, r => r with { Name = name }),
        });
        return change.Build(L.T($"Resolution {resolution.Name} renamed to {name}"));
    }
}

/// <summary>Deletes a resolution; issues with it get <see cref="ReplaceWith"/>. The last one can't go.</summary>
public sealed record DeleteResolution(string ResolutionId, string? ReplaceWith) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var resolution = board.Project.Resolution(ResolutionId) ?? throw new BoardRuleException(L.T("That resolution doesn't exist any more."));
        if (board.Project.Resolutions.Count == 1)
            throw new BoardRuleException(L.T("Done issues need at least one resolution."));
        var affected = board.Issues.Values.Where(i => i.Resolution == ResolutionId).ToList();
        if (affected.Count > 0 && (ReplaceWith is null || ReplaceWith == ResolutionId || board.Project.Resolution(ReplaceWith) is null))
            throw new BoardRuleException(L.T($"{affected.Count} issues are resolved as {resolution.Name}: choose another resolution for them."));
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { Resolutions = board.Project.Resolutions.Where(r => r.Id != ResolutionId).ToList() });
        foreach (var issue in affected)
            change.WriteIssue(issue, issue with { Resolution = ReplaceWith });
        return change.Build(L.T($"Resolution {resolution.Name} deleted"));
    }
}

// ---- Custom fields, components, link types, people ------------------------------------------------------

public sealed record AddCustomField(string Name, FieldType Type) : IBoardOperation
{
    public string? Id { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Definitions.RequireName(Name, "field");
        Definitions.RequireUnique(board.Project.CustomFields, f => f.Name, name, "field");
        var options = CleanOptions(Type, Options);
        var change = new ChangeBuilder(board, context);
        var field = new FieldDefinition { Id = Definitions.NewIdFor(change, Id, board.Project.CustomFields.Select(f => f.Id)), Name = name, Type = Type, Options = options };
        change.WriteProject(board.Project with { CustomFields = [.. board.Project.CustomFields, field] });
        return change.Build(L.T($"Field {name} added"));
    }

    internal static IReadOnlyList<string> CleanOptions(FieldType type, IReadOnlyList<string> options)
    {
        var clean = options.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (type == FieldType.Select && clean.Count == 0)
            throw new BoardRuleException(L.T("A choice field needs at least one choice."));
        return type == FieldType.Select ? clean : [];
    }
}

/// <summary>Renames a custom field and / or changes a choice field's choices (values no longer offered are cleared).</summary>
public sealed record EditCustomField(string FieldId, string Name, IReadOnlyList<string> Options) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var field = board.Project.CustomField(FieldId) ?? throw new BoardRuleException(L.T("That field doesn't exist any more."));
        var name = Definitions.RequireName(Name, "field");
        Definitions.RequireUnique(board.Project.CustomFields, f => f.Name, name, "field", f => f.Id == FieldId);
        var options = AddCustomField.CleanOptions(field.Type, Options);
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with
        {
            CustomFields = Definitions.Replace(board.Project.CustomFields, f => f.Id == FieldId, f => f with { Name = name, Options = options }),
        });
        if (field.Type == FieldType.Select)
        {
            foreach (var issue in board.Issues.Values.Where(i => i.Custom.TryGetValue(FieldId, out var v) && !options.Contains(v)))
            {
                var custom = new SortedDictionary<string, string>(issue.Custom, StringComparer.Ordinal);
                custom.Remove(FieldId);
                change.WriteIssue(issue, issue with { Custom = custom });
            }
        }
        return change.Build(L.T($"Field {field.Name} changed"));
    }
}

/// <summary>Deletes a custom field and its values on every issue.</summary>
public sealed record DeleteCustomField(string FieldId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var field = board.Project.CustomField(FieldId) ?? throw new BoardRuleException(L.T("That field doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { CustomFields = board.Project.CustomFields.Where(f => f.Id != FieldId).ToList() });
        foreach (var issue in board.Issues.Values.Where(i => i.Custom.ContainsKey(FieldId)))
        {
            var custom = new SortedDictionary<string, string>(issue.Custom, StringComparer.Ordinal);
            custom.Remove(FieldId);
            change.WriteIssue(issue, issue with { Custom = custom });
        }
        return change.Build(L.T($"Field {field.Name} deleted"));
    }
}

public sealed record AddComponent(string Name, string? Description = null) : IBoardOperation
{
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Definitions.RequireName(Name, "component");
        Definitions.RequireUnique(board.Project.Components, c => c.Name, name, "component");
        var change = new ChangeBuilder(board, context);
        var component = new Component
        {
            Id = Definitions.NewIdFor(change, Id, board.Project.Components.Select(c => c.Id)),
            Name = name,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        };
        change.WriteProject(board.Project with { Components = [.. board.Project.Components, component] });
        return change.Build(L.T($"Component {name} added"));
    }
}

public sealed record EditComponent(string ComponentId, string Name, string? Description) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var component = board.Project.Component(ComponentId) ?? throw new BoardRuleException(L.T("That component doesn't exist any more."));
        var name = Definitions.RequireName(Name, "component");
        Definitions.RequireUnique(board.Project.Components, c => c.Name, name, "component", c => c.Id == ComponentId);
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with
        {
            Components = Definitions.Replace(board.Project.Components, c => c.Id == ComponentId,
                c => c with { Name = name, Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim() }),
        });
        return change.Build(L.T($"Component {component.Name} changed"));
    }
}

/// <summary>Deletes a component; issues filed under it lose it.</summary>
public sealed record DeleteComponent(string ComponentId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var component = board.Project.Component(ComponentId) ?? throw new BoardRuleException(L.T("That component doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { Components = board.Project.Components.Where(c => c.Id != ComponentId).ToList() });
        foreach (var issue in board.Issues.Values.Where(i => i.Components.Contains(ComponentId)))
            change.WriteIssue(issue, issue with { Components = issue.Components.Where(c => c != ComponentId).ToList() });
        return change.Build(L.T($"Component {component.Name} deleted"));
    }
}

public sealed record AddLinkType(string Outward, string Inward) : IBoardOperation
{
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var outward = Definitions.RequireName(Outward, L.T("link type"));
        var inward = string.IsNullOrWhiteSpace(Inward) ? outward : Inward.Trim();
        Definitions.RequireUnique(board.Project.LinkTypes, l => l.Outward, outward, L.T("link type"));
        var change = new ChangeBuilder(board, context);
        var type = new LinkType { Id = Definitions.NewIdFor(change, Id, board.Project.LinkTypes.Select(l => l.Id)), Outward = outward, Inward = inward };
        change.WriteProject(board.Project with { LinkTypes = [.. board.Project.LinkTypes, type] });
        return change.Build(L.T($"Link type \"{outward}\" added"));
    }
}

public sealed record EditLinkType(string LinkTypeId, string Outward, string Inward) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        _ = board.Project.LinkType(LinkTypeId) ?? throw new BoardRuleException(L.T("That link type doesn't exist any more."));
        var outward = Definitions.RequireName(Outward, L.T("link type"));
        var inward = string.IsNullOrWhiteSpace(Inward) ? outward : Inward.Trim();
        Definitions.RequireUnique(board.Project.LinkTypes, l => l.Outward, outward, L.T("link type"), l => l.Id == LinkTypeId);
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with
        {
            LinkTypes = Definitions.Replace(board.Project.LinkTypes, l => l.Id == LinkTypeId, l => l with { Outward = outward, Inward = inward }),
        });
        return change.Build(L.T($"Link type \"{outward}\" changed"));
    }
}

/// <summary>Deletes a link type and every link of that type.</summary>
public sealed record DeleteLinkType(string LinkTypeId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var type = board.Project.LinkType(LinkTypeId) ?? throw new BoardRuleException(L.T("That link type doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { LinkTypes = board.Project.LinkTypes.Where(l => l.Id != LinkTypeId).ToList() });
        foreach (var issue in board.Issues.Values.Where(i => i.Links.Any(l => l.Type == LinkTypeId)))
            change.WriteIssue(issue, issue with { Links = issue.Links.Where(l => l.Type != LinkTypeId).ToList() });
        return change.Build(L.T($"Link type \"{type.Outward}\" deleted"));
    }
}

public sealed record EditPerson(string PersonId, string Name, string? Email) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var person = board.Project.Person(PersonId) ?? throw new BoardRuleException(L.T("That person isn't on the board any more."));
        var name = Definitions.RequireName(Name, "person");
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with
        {
            People = Definitions.Replace(board.Project.People, p => p.Id == PersonId,
                p => p with { Name = name, Email = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim() }),
        });
        return change.Build($"{person.Name} changed");
    }
}

/// <summary>Takes someone off the board; issues assigned to them become unassigned (reporter, comments and history keep their id).</summary>
public sealed record RemovePerson(string PersonId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var person = board.Project.Person(PersonId) ?? throw new BoardRuleException(L.T("That person isn't on the board any more."));
        if (PersonId == context.Who)
            throw new BoardRuleException(L.T("You can't take yourself off the board."));
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { People = board.Project.People.Where(p => p.Id != PersonId).ToList() });
        foreach (var issue in board.Issues.Values.Where(i => i.Assignee == PersonId))
            change.WriteIssue(issue, issue with { Assignee = null });
        return change.Build(L.T($"{person.Name} taken off the board"));
    }
}

// ---- Versions ---------------------------------------------------------------------------------------------

/// <summary>Creates a fix version (a release issues can be planned for).</summary>
public sealed record CreateVersion(string Name) : IBoardOperation
{
    public string? Id { get; init; }
    public string Description { get; init; } = "";
    public DateOnly? Start { get; init; }
    public DateOnly? Release { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Definitions.RequireName(Name, "version");
        Definitions.RequireUnique(board.Versions.Values, v => v.Name, name, "version");
        if (Start is { } s && Release is { } r && r < s)
            throw new BoardRuleException(L.T("A version can't be released before it starts."));
        var change = new ChangeBuilder(board, context);
        var version = new FixVersion { Id = Definitions.NewIdFor(change, Id, board.Versions.Keys), Name = name, Description = Description.Trim(), Start = Start, Release = Release };
        change.Write(BoardPaths.Version(version.Id), version);
        return change.Build(L.T($"Version {name} created"));
    }
}

/// <summary>Changes a version's name, description, dates, released or archived state.</summary>
public sealed record EditVersion(string VersionId, string Name, string Description, DateOnly? Start, DateOnly? Release, bool Released, bool Archived) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var version = board.Versions.GetValueOrDefault(VersionId) ?? throw new BoardRuleException(L.T("That version doesn't exist any more."));
        var name = Definitions.RequireName(Name, "version");
        Definitions.RequireUnique(board.Versions.Values, v => v.Name, name, "version", v => v.Id == VersionId);
        if (Start is { } s && Release is { } r && r < s)
            throw new BoardRuleException(L.T("A version can't be released before it starts."));
        var change = new ChangeBuilder(board, context);
        change.Write(BoardPaths.Version(version.Id), version with
        {
            Name = name, Description = Description.Trim(), Start = Start, Release = Release, Released = Released, Archived = Archived,
        });
        return change.Build(L.T($"Version {version.Name} changed"));
    }
}

/// <summary>Deletes a version; issues planned for it lose it (or move to <see cref="MoveTo"/>).</summary>
public sealed record DeleteVersion(string VersionId, string? MoveTo = null) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var version = board.Versions.GetValueOrDefault(VersionId) ?? throw new BoardRuleException(L.T("That version doesn't exist any more."));
        if (MoveTo is not null && (MoveTo == VersionId || !board.Versions.ContainsKey(MoveTo)))
            throw new BoardRuleException(L.T("Choose another version for its issues."));
        var change = new ChangeBuilder(board, context);
        change.Delete(BoardPaths.Version(version.Id));
        foreach (var issue in board.Issues.Values.Where(i => i.FixVersions.Contains(VersionId)))
        {
            var versions = issue.FixVersions.Where(v => v != VersionId).ToList();
            if (MoveTo is not null && !versions.Contains(MoveTo))
                versions.Add(MoveTo);
            change.WriteIssue(issue, issue with { FixVersions = versions });
        }
        return change.Build(L.T($"Version {version.Name} deleted"));
    }
}
