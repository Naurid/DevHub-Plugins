using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Uml.Core;

/// <summary>
/// Use case diagrams (course notation): actors outside the system boundary, use cases (ellipses) inside it,
/// associations actor–use case, «include» from the base to the included case, «extend» from the extension to the base
/// (which may list its extension points), generalization of actors or of use cases, notes.
/// </summary>
public sealed class UseCaseModel : IDiagramKindModel
{
    public const string KindId = "usecase";

    public const string Actor = "actor";
    public const string UseCase = "usecase";
    public const string Boundary = "boundary";
    public const string Note = "note";

    public const string Association = "association";
    public const string Include = "include";
    public const string Extend = "extend";
    public const string Generalization = "generalization";
    public const string NoteLink = "noteLink";

    public string Id => KindId;
    public string Name => L.T("Use case diagram");
    public string Description => L.T("Who uses the system for what: actors, use cases, the system boundary, «include» and «extend».");

    public IReadOnlyList<NodeTypeInfo> NodeTypes { get; } =
    [
        new(Actor, L.T("Actor"), NodeShape.Figure, 60, 92, L.T("Actor"))
            { Description = L.T("A person, a machine or another system that uses this one (outside the boundary).") },
        new(UseCase, L.T("Use case"), NodeShape.Ellipse, 170, 64, L.T("Use case"))
            { Description = L.T("Something the system does for an actor: a verb and its object (\"Book a flight\").") },
        new(Boundary, L.T("System boundary"), NodeShape.Rectangle, 420, 440, L.T("System"))
            { IsContainer = true, Connectable = false, Description = L.T("The system: its use cases go inside, the actors stay outside.") },
        new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note"))
            { Description = L.T("A comment, tied to shapes with a dashed line.") },
    ];

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; } =
    [
        new(Association, L.T("Association"), L.T("An actor takes part in a use case."))
            { Allowed = (a, b) => (a == Actor && b == UseCase) || (a == UseCase && b == Actor) },
        new(Include, L.T("«include»"), L.T("The base use case always does the included one (drag from the base)."))
            { Allowed = (a, b) => a == UseCase && b == UseCase, DefaultText = "«include»" },
        new(Extend, L.T("«extend»"), L.T("The extension adds to the base use case under a condition (drag from the extension to the base)."))
            { Allowed = (a, b) => a == UseCase && b == UseCase, DefaultText = "«extend»" },
        new(Generalization, L.T("Generalization"), L.T("A special kind of actor or use case (drag from the special one to the general one)."))
            { Allowed = (a, b) => a == b && a is Actor or UseCase },
        new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape."))
            { Allowed = (a, b) => a == Note || b == Note },
    ];

    public IReadOnlyList<IDiagramRule> Rules { get; } =
    [
        new Rule(ActorsOutside),
        new Rule(UseCasesInside),
        new Rule(LonelyActors),
        new Rule(ExtendPoints),
        new Rule(IncludeCycles),
        new Rule(d => Duplicates(d, UseCase, L.T("use case"))),
        new Rule(d => Duplicates(d, Actor, L.T("actor"))),
    ];

    public IReadOnlyList<IDiagramTextExporter> TextExporters { get; } = [new UseCasePlantUml()];

    public IReadOnlyList<DiagramSample> Samples => [.. UmlSamples.UseCase, .. UmlShowcase.UseCase];

    public Diagram Create(string title)
    {
        var diagram = new Diagram { Kind = KindId, Title = title };
        var boundary = this.NewNode(diagram, Boundary, new DPoint(420, 260), title);
        diagram.Nodes.Add(boundary);
        return diagram;
    }

    private static IEnumerable<DiagramProblem> ActorsOutside(Diagram d)
    {
        foreach (var actor in d.Nodes.Where(n => n.Type == Actor && n.ParentId is not null))
            yield return new DiagramProblem(ProblemSeverity.Warning,
                L.T($"\"{actor.Text}\" is an actor: it stays outside the system boundary."), [actor.Id]);
    }

    private static IEnumerable<DiagramProblem> UseCasesInside(Diagram d)
    {
        if (!d.Nodes.Any(n => n.Type == Boundary))
            yield break;
        foreach (var u in d.Nodes.Where(n => n.Type == UseCase && n.ParentId is null))
            yield return new DiagramProblem(ProblemSeverity.Warning,
                L.T($"The use case \"{u.Text}\" is outside the system boundary: move it inside."), [u.Id]);
    }

    private static IEnumerable<DiagramProblem> LonelyActors(Diagram d)
    {
        foreach (var actor in d.Nodes.Where(n => n.Type == Actor))
            if (!d.EdgesOf(actor.Id).Any(e => e.Type is Association or Generalization))
                yield return new DiagramProblem(ProblemSeverity.Warning,
                    L.T($"The actor \"{actor.Text}\" takes part in no use case."), [actor.Id]);
    }

    private static IEnumerable<DiagramProblem> ExtendPoints(Diagram d)
    {
        foreach (var e in d.Edges.Where(e => e.Type == Extend))
            if (d.Node(e.To) is { } baseCase && baseCase.Prop("extensionPoints").Trim().Length == 0)
                yield return new DiagramProblem(ProblemSeverity.Info,
                    L.T($"\"{baseCase.Text}\" is extended: name its extension point (Properties → Extension points)."), [baseCase.Id, e.Id]);
    }

    private static IEnumerable<DiagramProblem> IncludeCycles(Diagram d)
    {
        var includes = d.Edges.Where(e => e.Type == Include).ToList();
        foreach (var start in d.Nodes.Where(n => n.Type == UseCase))
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>(includes.Where(e => e.From == start.Id).Select(e => e.To));
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id == start.Id)
                {
                    yield return new DiagramProblem(ProblemSeverity.Error,
                        L.T($"\"{start.Text}\" ends up including itself (a loop of «include»)."), [start.Id]);
                    break;
                }
                if (seen.Add(id))
                    foreach (var e in includes.Where(e => e.From == id))
                        stack.Push(e.To);
            }
        }
    }

    internal static IEnumerable<DiagramProblem> Duplicates(Diagram d, string type, string what)
    {
        foreach (var group in d.Nodes.Where(n => n.Type == type && n.Text.Trim().Length > 0)
                     .GroupBy(n => n.Text.Trim(), StringComparer.CurrentCultureIgnoreCase).Where(g => g.Count() > 1))
            yield return new DiagramProblem(ProblemSeverity.Warning,
                L.T($"Two shapes ({what}) are called \"{group.Key}\": one of them, or name them apart."), [.. group.Select(n => n.Id)]);
    }
}

/// <summary>PlantUML text of a use case diagram (paste into plantuml.com or an IDE plugin).</summary>
public sealed class UseCasePlantUml : IDiagramTextExporter
{
    public string Name => "PlantUML";
    public string Extension => "puml";

    public string Export(Diagram d)
    {
        var w = new System.Text.StringBuilder("@startuml\nleft to right direction\n");
        if (d.Title.Length > 0)
            w.Append($"title {Text.Line(d.Title)}\n");
        foreach (var a in d.Nodes.Where(n => n.Type == UseCaseModel.Actor))
        {
            var stereotype = a.Prop("stereotype");
            w.Append($"actor \"{Text.Quote(a.Text)}\" as {a.Id}{(stereotype.Length > 0 ? $" <<{stereotype}>>" : "")}\n");
        }
        void Cases(string? parent, string indent)
        {
            foreach (var u in d.Nodes.Where(n => n.Type == UseCaseModel.UseCase && n.ParentId == parent))
                w.Append($"{indent}usecase \"{Text.Quote(u.Text)}\" as {u.Id}\n");
        }
        Cases(null, "");
        foreach (var b in d.Nodes.Where(n => n.Type == UseCaseModel.Boundary))
        {
            w.Append($"rectangle \"{Text.Quote(b.Text)}\" {{\n");
            Cases(b.Id, "  ");
            w.Append("}\n");
        }
        foreach (var n in d.Nodes.Where(n => n.Type == UseCaseModel.Note))
            w.Append($"note \"{Text.Quote(n.Text)}\" as {n.Id}\n");
        foreach (var e in d.Edges)
        {
            var line = e.Type switch
            {
                UseCaseModel.Association => $"{e.From} -- {e.To}",
                UseCaseModel.Include => $"{e.From} .> {e.To} : <<include>>",
                UseCaseModel.Extend => $"{e.From} .> {e.To} : <<extend>>",
                UseCaseModel.Generalization => $"{e.From} --|> {e.To}",
                UseCaseModel.NoteLink => $"{e.From} .. {e.To}",
                _ => null,
            };
            if (line is not null)
                w.Append(line).Append('\n');
        }
        return w.Append("@enduml\n").ToString();
    }
}

/// <summary>Text helpers for the exporters.</summary>
internal static class Text
{
    public static string Quote(string s) => s.Replace("\"", "'").Replace("\r", "").Replace("\n", "\\n");

    public static string Line(string s) => s.Replace("\r", "").Replace("\n", " ");
}
