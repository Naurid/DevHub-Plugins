using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Uml.Core;

/// <summary>
/// State machine diagrams: the states an object goes through (with entry / do / exit activities), composite states,
/// initial and final states, choice points, fork / join bars, history; transitions "event [guard] / action".
/// </summary>
public sealed class StateModel : IDiagramKindModel
{
    public const string KindId = "state";

    public const string Initial = "initial";
    public const string Final = "final";
    public const string State = "state";
    public const string Composite = "composite";
    public const string Choice = "choice";
    public const string Bar = "bar";
    public const string History = "history";
    public const string Note = "note";

    public const string Transition = "transition";
    public const string NoteLink = "noteLink";

    public string Id => KindId;
    public string Name => L.T("State machine diagram");
    public string Description => L.T("The states something goes through and what makes it change: event [condition] / action.");

    public IReadOnlyList<NodeTypeInfo> NodeTypes { get; } =
    [
        new(Initial, L.T("Initial state"), NodeShape.Ellipse, 24, 24) { Description = L.T("Where it starts.") },
        new(State, L.T("State"), NodeShape.RoundedRectangle, 150, 70, L.T("State"))
            { Description = L.T("A situation it stays in for a while; may list entry / do / exit activities.") },
        new(Composite, L.T("Composite state"), NodeShape.RoundedRectangle, 360, 240, L.T("State"))
            { IsContainer = true, Description = L.T("A state with states inside it.") },
        new(Choice, L.T("Choice"), NodeShape.Diamond, 30, 30) { Description = L.T("Goes one way or another by its [conditions].") },
        new(Bar, L.T("Fork / join"), NodeShape.Bar, 120, 8) { Description = L.T("Into parallel regions, or out of them.") },
        new(History, L.T("History"), NodeShape.Ellipse, 30, 30, "H") { Description = L.T("Back to the last state it was in (H, or H* for deep history).") },
        new(Final, L.T("Final state"), NodeShape.Ellipse, 28, 28) { Description = L.T("Where it ends.") },
        new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note")) { Description = L.T("A comment.") },
    ];

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; } =
    [
        new(Transition, L.T("Transition"), L.T("From one state to another: write event [condition] / action."))
            { Allowed = (a, b) => a is not Note && b is not Note },
        new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
    ];

    public IReadOnlyList<IDiagramRule> Rules { get; } = [new Rule(Check)];

    public IReadOnlyList<IDiagramTextExporter> TextExporters { get; } = [new StatePlantUml(), new StateMermaid()];

    public IReadOnlyList<DiagramSample> Samples => UmlSamples.State;

    public Diagram Create(string title)
    {
        var d = new Diagram { Kind = KindId, Title = title };
        d.Nodes.Add(this.NewNode(d, Initial, new DPoint(80, 80)));
        return d;
    }

    private static IEnumerable<DiagramProblem> Check(Diagram d)
    {
        if (d.Nodes.Any(n => n.Type == State) && !d.Nodes.Any(n => n.Type == Initial))
            yield return new DiagramProblem(ProblemSeverity.Warning, L.T("There's no initial state (a filled dot)."), []);
        foreach (var e in d.Edges.Where(e => e.Type == Transition))
        {
            if (d.Node(e.To)?.Type == Initial)
                yield return new DiagramProblem(ProblemSeverity.Error, L.T("No transition goes into the initial state."), [e.Id]);
            if (d.Node(e.From)?.Type == Final)
                yield return new DiagramProblem(ProblemSeverity.Error, L.T("No transition leaves the final state."), [e.Id]);
        }
        foreach (var s in d.Nodes.Where(n => n.Type == State && !d.Edges.Any(e => e.To == n.Id)))
            yield return new DiagramProblem(ProblemSeverity.Warning, L.T($"No transition leads to the state \"{s.Text}\"."), [s.Id]);
    }
}

public sealed class StatePlantUml : IDiagramTextExporter
{
    public string Name => "PlantUML";
    public string Extension => "puml";

    public string Export(Diagram d)
    {
        var w = new System.Text.StringBuilder("@startuml\n");
        string Ref(DiagramNode n) => n.Type is StateModel.Initial or StateModel.Final ? "[*]" : n.Id;
        void States(string? parent, string indent)
        {
            foreach (var n in d.Nodes.Where(n => n.ParentId == parent))
            {
                switch (n.Type)
                {
                    case StateModel.State:
                        w.Append($"{indent}state \"{Text.Quote(n.Text)}\" as {n.Id}\n");
                        foreach (var line in n.Prop("activities").Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0))
                            w.Append($"{indent}{n.Id} : {line.Trim()}\n");
                        break;
                    case StateModel.Composite:
                        w.Append($"{indent}state \"{Text.Quote(n.Text)}\" as {n.Id} {{\n");
                        States(n.Id, indent + "  ");
                        w.Append($"{indent}}}\n");
                        break;
                    case StateModel.Choice:
                        w.Append($"{indent}state {n.Id} <<choice>>\n");
                        break;
                    case StateModel.Bar:
                        w.Append($"{indent}state {n.Id} <<fork>>\n");
                        break;
                    case StateModel.History:
                        w.Append($"{indent}state {n.Id} <<history>>\n");
                        break;
                }
            }
        }
        States(null, "");
        foreach (var e in d.Edges.Where(e => e.Type == StateModel.Transition))
            if (d.Node(e.From) is { } a && d.Node(e.To) is { } b)
                w.Append($"{Ref(a)} --> {Ref(b)}{(e.Text.Trim().Length > 0 ? " : " + Text.Line(e.Text) : "")}\n");
        return w.Append("@enduml\n").ToString();
    }
}

public sealed class StateMermaid : IDiagramTextExporter
{
    public string Name => "Mermaid";
    public string Extension => "mmd";

    public string Export(Diagram d)
    {
        var w = new System.Text.StringBuilder("stateDiagram-v2\n");
        string Ref(DiagramNode n) => n.Type is StateModel.Initial or StateModel.Final ? "[*]" : n.Id;
        foreach (var n in d.Nodes.Where(n => n.Type is StateModel.State or StateModel.Composite))
            w.Append($"  state \"{Text.Quote(n.Text)}\" as {n.Id}\n");
        foreach (var n in d.Nodes.Where(n => n.Type == StateModel.Choice))
            w.Append($"  state {n.Id} <<choice>>\n");
        foreach (var e in d.Edges.Where(e => e.Type == StateModel.Transition))
            if (d.Node(e.From) is { } a && d.Node(e.To) is { } b)
                w.Append($"  {Ref(a)} --> {Ref(b)}{(e.Text.Trim().Length > 0 ? " : " + Text.Line(e.Text) : "")}\n");
        return w.ToString();
    }
}

/// <summary>
/// Component, deployment and package diagrams: the parts of a system and what they depend on, where they run, how
/// the model is grouped. One model, three palettes.
/// </summary>
public sealed class StructureModel(string kind) : IDiagramKindModel
{
    public const string ComponentKind = "component";
    public const string DeploymentKind = "deployment";
    public const string PackageKind = "package";

    public const string Component = "component";
    public const string ProvidedInterface = "provided";
    public const string Port = "port";
    public const string Node = "node";
    public const string Device = "device";
    public const string Environment = "environment";
    public const string Artifact = "artifact";
    public const string Package = "package";
    public const string Database = "database";
    public const string Note = "note";

    public const string Dependency = "dependency";
    public const string Association = "association";
    public const string Realization = "realization";
    public const string Deploy = "deploy";
    public const string Import = "import";
    public const string NoteLink = "noteLink";

    public string Id => kind;

    public string Name => kind switch
    {
        ComponentKind => L.T("Component diagram"),
        DeploymentKind => L.T("Deployment diagram"),
        _ => L.T("Package diagram"),
    };

    public string Description => kind switch
    {
        ComponentKind => L.T("The parts of the software, the interfaces they offer and use, and what depends on what."),
        DeploymentKind => L.T("Where it runs: servers, devices, environments, and the artifacts deployed on them."),
        _ => L.T("How the model is grouped into packages, and which package uses which."),
    };

    private static readonly NodeTypeInfo NoteType = new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note")) { Description = L.T("A comment.") };

    public IReadOnlyList<NodeTypeInfo> NodeTypes => kind switch
    {
        ComponentKind =>
        [
            new(Component, L.T("Component"), NodeShape.Rectangle, 170, 80, L.T("Component"))
                { IsContainer = true, Description = L.T("A replaceable part of the system (a service, a library, a module).") },
            new(ProvidedInterface, L.T("Interface"), NodeShape.Ellipse, 22, 22, L.T("Interface"))
                { Description = L.T("An interface a component offers (a lollipop); link the users to it.") },
            new(Port, L.T("Port"), NodeShape.Rectangle, 14, 14) { Description = L.T("A connection point on a component's edge.") },
            new(Artifact, L.T("Artifact"), NodeShape.Rectangle, 140, 60, L.T("file.jar")) { Description = L.T("A file the component is built into.") },
            new(Database, L.T("Database"), NodeShape.Rectangle, 100, 80, L.T("Database")) { Description = L.T("A data store.") },
            new(Package, L.T("Package"), NodeShape.Rectangle, 360, 240, L.T("Package")) { IsContainer = true, Description = L.T("A group.") },
            NoteType,
        ],
        DeploymentKind =>
        [
            new(Node, L.T("Node"), NodeShape.Rectangle, 260, 160, L.T("Server")) { IsContainer = true, Description = L.T("A machine or a virtual machine.") },
            new(Device, L.T("Device"), NodeShape.Rectangle, 200, 130, L.T("Device")) { IsContainer = true, Description = L.T("A physical device (a phone, a terminal).") },
            new(Environment, L.T("Execution environment"), NodeShape.Rectangle, 220, 120, L.T("Runtime"))
                { IsContainer = true, Description = L.T("Software that runs others (an OS, a container, a web server).") },
            new(Artifact, L.T("Artifact"), NodeShape.Rectangle, 140, 60, L.T("app.war")) { Description = L.T("A deployed file.") },
            new(Component, L.T("Component"), NodeShape.Rectangle, 150, 70, L.T("Component")) { Description = L.T("A part of the software.") },
            new(Database, L.T("Database"), NodeShape.Rectangle, 100, 80, L.T("Database")) { Description = L.T("A data store.") },
            NoteType,
        ],
        _ =>
        [
            new(Package, L.T("Package"), NodeShape.Rectangle, 220, 140, L.T("Package")) { IsContainer = true, Description = L.T("A group of model elements.") },
            NoteType,
        ],
    };

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes => kind switch
    {
        DeploymentKind =>
        [
            new(Association, L.T("Communication path"), L.T("Two nodes talk (a network, a protocol).")),
            new(Deploy, L.T("«deploy»"), L.T("An artifact is deployed on a node.")) { DefaultText = "«deploy»" },
            new(Dependency, L.T("Dependency"), L.T("One uses the other (dashed arrow).")),
            new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
        ],
        PackageKind =>
        [
            new(Dependency, L.T("Dependency"), L.T("One package uses another.")),
            new(Import, L.T("«import»"), L.T("A package imports another's names.")) { DefaultText = "«import»" },
            new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
        ],
        _ =>
        [
            new(Dependency, L.T("Dependency / use"), L.T("A component uses an interface or another component (dashed arrow).")),
            new(Association, L.T("Assembly"), L.T("Connects what one needs to what another offers.")),
            new(Realization, L.T("Realization"), L.T("A component implements an interface.")),
            new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
        ],
    };

    public IReadOnlyList<IDiagramRule> Rules { get; } = [];

    public IReadOnlyList<IDiagramTextExporter> TextExporters => [new StructurePlantUml()];

    public Diagram Create(string title) => new() { Kind = kind, Title = title };
}

public sealed class StructurePlantUml : IDiagramTextExporter
{
    public string Name => "PlantUML";
    public string Extension => "puml";

    public string Export(Diagram d)
    {
        var w = new System.Text.StringBuilder("@startuml\n");
        void Write(string? parent, string indent)
        {
            foreach (var n in d.Nodes.Where(n => n.ParentId == parent))
            {
                var keyword = n.Type switch
                {
                    StructureModel.Component => "component",
                    StructureModel.ProvidedInterface => "interface",
                    StructureModel.Port => "port",
                    StructureModel.Node => "node",
                    StructureModel.Device => "node",
                    StructureModel.Environment => "node",
                    StructureModel.Artifact => "artifact",
                    StructureModel.Database => "database",
                    StructureModel.Package => "package",
                    _ => null,
                };
                if (keyword is null)
                    continue;
                var stereotype = n.Type switch { StructureModel.Device => " <<device>>", StructureModel.Environment => " <<executionEnvironment>>", _ => "" };
                var children = d.Children(n.Id).Any();
                w.Append($"{indent}{keyword} \"{Text.Quote(n.Text)}\" as {n.Id}{stereotype}{(children ? " {" : "")}\n");
                if (children)
                {
                    Write(n.Id, indent + "  ");
                    w.Append($"{indent}}}\n");
                }
            }
        }
        Write(null, "");
        foreach (var e in d.Edges)
        {
            var label = e.Text.Trim().Length > 0 ? " : " + Text.Line(e.Text) : "";
            var arrow = e.Type switch
            {
                StructureModel.Dependency or StructureModel.Deploy or StructureModel.Import => "..>",
                StructureModel.Realization => "..|>",
                StructureModel.NoteLink => "..",
                _ => "--",
            };
            w.Append($"{e.From} {arrow} {e.To}{label}\n");
        }
        return w.Append("@enduml\n").ToString();
    }
}
