using System.Text.RegularExpressions;
using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Uml.Core;

/// <summary>One attribute or operation line of a class box, read from text like <c>+ size : Area = (100,100)</c>.</summary>
/// <param name="Visibility">'+', '#', '-', '~', or '\0' when none is written.</param>
/// <param name="IsStatic">Written <c>static</c> (or between underscores): class scope, drawn underlined.</param>
/// <param name="IsAbstract">Written <c>abstract</c>: drawn in italics.</param>
public sealed record ClassMember(char Visibility, string Name, string? Parameters, string Type, string Default, bool IsStatic, bool IsAbstract)
{
    public bool IsOperation => Parameters is not null;

    /// <summary>How it's shown: without the words static / abstract (they become underline and italics).</summary>
    public string Display
    {
        get
        {
            var text = (Visibility == '\0' ? "" : Visibility.ToString()) + Name;
            if (Parameters is not null)
                text += "(" + Parameters + ")";
            if (Type.Length > 0)
                text += " : " + Type;
            if (Default.Length > 0)
                text += " = " + Default;
            return text;
        }
    }

    private static readonly Regex Shape = new(
        @"^(?<vis>[+\-#~])?\s*(?<mods>(?:(?:static|abstract)\s+)*)(?<name>[^\s(:=]+(?:\s+[^\s(:=]+)*?)\s*(?:\((?<params>[^)]*)\))?\s*(?::\s*(?<type>[^=]+?))?\s*(?:=\s*(?<def>.+))?$",
        RegexOptions.Compiled);

    /// <summary>The member a line describes, or null for a line that isn't one (empty, or no name).</summary>
    public static ClassMember? Parse(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
            return null;
        var underscored = text.Length > 2 && text[0] == '_' && text[^1] == '_';
        if (underscored)
            text = text[1..^1].Trim();
        var m = Shape.Match(text);
        if (!m.Success || m.Groups["name"].Value.Length == 0)
            return null;
        var mods = m.Groups["mods"].Value;
        return new ClassMember(
            m.Groups["vis"].Success ? m.Groups["vis"].Value[0] : '\0',
            m.Groups["name"].Value.Trim(),
            m.Groups["params"].Success ? m.Groups["params"].Value.Trim() : null,
            m.Groups["type"].Success ? m.Groups["type"].Value.Trim() : "",
            m.Groups["def"].Success ? m.Groups["def"].Value.Trim() : "",
            underscored || mods.Contains("static"),
            mods.Contains("abstract"));
    }

    /// <summary>The lines of a compartment (attributes or operations), one per line.</summary>
    public static IEnumerable<string> Lines(string text) =>
        text.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0);
}

/// <summary>
/// Class diagrams: classes (attributes, operations, abstract, {properties}), interfaces, enumerations, datatypes,
/// notes; association (name, roles, multiplicities, navigability), aggregation, composition, generalization,
/// realization, dependency. Object diagrams use the same model with <see cref="ObjectKindId"/>.
/// </summary>
public sealed class ClassModel(bool objects = false) : IDiagramKindModel
{
    public const string KindId = "class";
    public const string ObjectKindId = "object";

    public const string Class = "class";
    public const string Interface = "interface";
    public const string Enumeration = "enumeration";
    public const string DataType = "datatype";
    public const string Instance = "instance";
    public const string Package = "package";
    public const string Note = "note";

    public const string Association = "association";
    public const string Aggregation = "aggregation";
    public const string Composition = "composition";
    public const string Generalization = "generalization";
    public const string Realization = "realization";
    public const string Dependency = "dependency";
    public const string Link = "link";
    public const string NoteLink = "noteLink";

    public string Id => objects ? ObjectKindId : KindId;
    public string Name => objects ? L.T("Object diagram") : L.T("Class diagram");
    public string Description => objects
        ? L.T("A snapshot: objects (instances of classes) with their values, and the links between them.")
        : L.T("The structure: classes with attributes and operations, interfaces, enumerations and their relations.");

    public IReadOnlyList<NodeTypeInfo> NodeTypes => objects ? ObjectNodes : ClassNodes;

    private static readonly IReadOnlyList<NodeTypeInfo> ClassNodes =
    [
        new(Class, L.T("Class"), NodeShape.Rectangle, 170, 90, L.T("Class"))
            { Description = L.T("A kind of thing: its name, attributes and operations.") },
        new(Interface, L.T("Interface"), NodeShape.Rectangle, 170, 70, L.T("Interface"))
            { Description = L.T("What a class promises to do: operations only.") },
        new(Enumeration, L.T("Enumeration"), NodeShape.Rectangle, 150, 90, L.T("Enumeration"))
            { Description = L.T("A list of possible values (a status, a gender).") },
        new(DataType, L.T("Datatype"), NodeShape.Rectangle, 150, 80, L.T("Datatype"))
            { Description = L.T("A value without identity (an address, a date).") },
        new(Package, L.T("Package"), NodeShape.Rectangle, 360, 260, L.T("Package"))
            { IsContainer = true, Description = L.T("A group of classes.") },
        new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note")) { Description = L.T("A comment or a constraint, tied with a dashed line.") },
    ];

    private static readonly IReadOnlyList<NodeTypeInfo> ObjectNodes =
    [
        new(Instance, L.T("Object"), NodeShape.Rectangle, 170, 80, L.T("object : Class"))
            { Description = L.T("One instance: \"name : Class\" (underlined) and its attribute values.") },
        new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note")) { Description = L.T("A comment, tied with a dashed line.") },
    ];

    private static bool Classifier(string t) => t is Class or Interface or Enumeration or DataType;

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes => objects ? ObjectEdges : ClassEdges;

    private static readonly IReadOnlyList<EdgeTypeInfo> ClassEdges =
    [
        new(Association, L.T("Association"), L.T("Instances are linked; set the multiplicities (1, 0..1, *, 1..*) and roles at each end."))
            { Allowed = (a, b) => Classifier(a) && Classifier(b) },
        new(Aggregation, L.T("Aggregation"), L.T("A whole and its parts that can live on their own (drag from the whole)."))
            { Allowed = (a, b) => Classifier(a) && Classifier(b) },
        new(Composition, L.T("Composition"), L.T("A whole owning its parts: they go with it (drag from the whole)."))
            { Allowed = (a, b) => Classifier(a) && Classifier(b) },
        new(Generalization, L.T("Inheritance"), L.T("A subclass of another class (drag from the subclass to the parent)."))
            { Allowed = (a, b) => Classifier(a) && Classifier(b) },
        new(Realization, L.T("Realization"), L.T("A class implements an interface (drag from the class)."))
            { Allowed = (a, b) => Classifier(a) && b == Interface },
        new(Dependency, L.T("Dependency"), L.T("One uses the other (dashed arrow)."))
            { Allowed = (a, b) => a != Note && b != Note },
        new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
    ];

    private static readonly IReadOnlyList<EdgeTypeInfo> ObjectEdges =
    [
        new(Link, L.T("Link"), L.T("Two objects linked (an instance of an association).")) { Allowed = (a, b) => a == Instance && b == Instance },
        new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
    ];

    public IReadOnlyList<IDiagramRule> Rules => objects ? [] : ClassRules;

    private static readonly IReadOnlyList<IDiagramRule> ClassRules =
    [
        new Rule(UnreadableMembers),
        new Rule(d => UseCaseModel.Duplicates(d, Class, L.T("class"))),
        new Rule(InheritanceCycles),
        new Rule(Multiplicities),
        new Rule(InterfaceAttributes),
    ];

    public IReadOnlyList<IDiagramTextExporter> TextExporters => objects ? [] : [new ClassPlantUml(), new ClassMermaid()];

    public IReadOnlyList<DiagramSample> Samples => objects ? UmlShowcase.Object : [.. UmlSamples.Class, .. UmlShowcase.Class];

    public Diagram Create(string title) => new() { Kind = Id, Title = title };

    private static IEnumerable<DiagramProblem> UnreadableMembers(Diagram d)
    {
        foreach (var n in d.Nodes.Where(n => n.Type is Class or Interface or DataType))
        {
            foreach (var key in new[] { "attributes", "operations" })
            {
                foreach (var line in ClassMember.Lines(n.Prop(key)))
                {
                    if (ClassMember.Parse(line) is null)
                        yield return new DiagramProblem(ProblemSeverity.Warning,
                            L.T($"In \"{n.Text}\", \"{line.Trim()}\" isn't read as a member: write it like + name : Type."), [n.Id]);
                }
            }
        }
    }

    private static IEnumerable<DiagramProblem> InheritanceCycles(Diagram d)
    {
        var parents = d.Edges.Where(e => e.Type == Generalization).ToLookup(e => e.From, e => e.To);
        foreach (var n in d.Nodes.Where(n => Classifier(n.Type)))
        {
            var seen = new HashSet<string>();
            var current = new Stack<string>(parents[n.Id]);
            while (current.Count > 0)
            {
                var id = current.Pop();
                if (id == n.Id)
                {
                    yield return new DiagramProblem(ProblemSeverity.Error, L.T($"\"{n.Text}\" inherits from itself (a loop of inheritance)."), [n.Id]);
                    break;
                }
                if (seen.Add(id))
                    foreach (var p in parents[id])
                        current.Push(p);
            }
        }
    }

    private static readonly Regex Multiplicity = new(@"^(\*|\d+|\d+\s*\.\.\s*(\d+|\*|n)|n|0\.\.n|1\.\.n)$", RegexOptions.Compiled);

    private static IEnumerable<DiagramProblem> Multiplicities(Diagram d)
    {
        foreach (var e in d.Edges.Where(e => e.Type is Association or Aggregation or Composition))
        {
            foreach (var key in new[] { "fromMultiplicity", "toMultiplicity" })
            {
                var value = e.Prop(key).Trim();
                if (value.Length > 0 && !Multiplicity.IsMatch(value))
                    yield return new DiagramProblem(ProblemSeverity.Warning,
                        L.T($"\"{value}\" isn't a multiplicity: write 1, 0..1, *, 1..* or 2..5."), [e.Id]);
            }
        }
    }

    private static IEnumerable<DiagramProblem> InterfaceAttributes(Diagram d)
    {
        foreach (var n in d.Nodes.Where(n => n.Type == Interface && ClassMember.Lines(n.Prop("attributes")).Any()))
            yield return new DiagramProblem(ProblemSeverity.Info, L.T($"\"{n.Text}\" is an interface: it usually has operations only."), [n.Id]);
    }
}

/// <summary>PlantUML class diagram.</summary>
public sealed class ClassPlantUml : IDiagramTextExporter
{
    public string Name => "PlantUML";
    public string Extension => "puml";

    public string Export(Diagram d)
    {
        var w = new System.Text.StringBuilder("@startuml\n");
        if (d.Title.Length > 0)
            w.Append($"title {Text.Line(d.Title)}\n");
        foreach (var n in d.Nodes)
        {
            var keyword = n.Type switch
            {
                ClassModel.Class => n.Flag("abstract") ? "abstract class" : "class",
                ClassModel.Interface => "interface",
                ClassModel.Enumeration => "enum",
                ClassModel.DataType => "class",
                ClassModel.Note => null,
                ClassModel.Package => null,
                _ => null,
            };
            if (n.Type == ClassModel.Note)
            {
                w.Append($"note \"{Text.Quote(n.Text)}\" as {n.Id}\n");
                continue;
            }
            if (keyword is null)
                continue;
            var stereotype = n.Type == ClassModel.DataType ? " <<datatype>>" : n.Prop("stereotype") is { Length: > 0 } s ? $" <<{s}>>" : "";
            w.Append($"{keyword} \"{Text.Quote(n.Text)}\" as {n.Id}{stereotype} {{\n");
            foreach (var line in ClassMember.Lines(n.Prop("attributes")))
                w.Append("  ").Append(Member(line, n.Type == ClassModel.Enumeration)).Append('\n');
            foreach (var line in ClassMember.Lines(n.Prop("operations")))
                w.Append("  ").Append(Member(line, false)).Append('\n');
            w.Append("}\n");
        }
        foreach (var e in d.Edges)
        {
            string M(string key) => e.Prop(key).Trim() is { Length: > 0 } m ? $" \"{m}\"" : "";
            var label = e.Text.Trim().Length > 0 ? $" : {Text.Line(e.Text)}" : "";
            var line = e.Type switch
            {
                ClassModel.Association => $"{e.From}{M("fromMultiplicity")} {(e.Prop("navigable") == "true" ? "-->" : "--")}{M("toMultiplicity")} {e.To}{label}",
                ClassModel.Aggregation => $"{e.From}{M("fromMultiplicity")} o--{M("toMultiplicity")} {e.To}{label}",
                ClassModel.Composition => $"{e.From}{M("fromMultiplicity")} *--{M("toMultiplicity")} {e.To}{label}",
                ClassModel.Generalization => $"{e.To} <|-- {e.From}",
                ClassModel.Realization => $"{e.To} <|.. {e.From}",
                ClassModel.Dependency => $"{e.From} ..> {e.To}{label}",
                ClassModel.NoteLink => $"{e.From} .. {e.To}",
                _ => null,
            };
            if (line is not null)
                w.Append(line).Append('\n');
        }
        return w.Append("@enduml\n").ToString();
    }

    private static string Member(string line, bool literal)
    {
        if (literal || ClassMember.Parse(line) is not { } m)
            return line.Trim();
        var text = m.Display;
        if (m.IsStatic)
            text = "{static} " + text;
        if (m.IsAbstract)
            text = "{abstract} " + text;
        return text;
    }
}

/// <summary>Mermaid class diagram.</summary>
public sealed class ClassMermaid : IDiagramTextExporter
{
    public string Name => "Mermaid";
    public string Extension => "mmd";

    public string Export(Diagram d)
    {
        var w = new System.Text.StringBuilder("classDiagram\n");
        foreach (var n in d.Nodes.Where(n => n.Type is ClassModel.Class or ClassModel.Interface or ClassModel.Enumeration or ClassModel.DataType))
        {
            w.Append($"  class {n.Id}[\"{Text.Quote(n.Text)}\"] {{\n");
            var stereotype = n.Type switch
            {
                ClassModel.Interface => "interface",
                ClassModel.Enumeration => "enumeration",
                ClassModel.DataType => "datatype",
                _ when n.Flag("abstract") => "abstract",
                _ => null,
            };
            if (stereotype is not null)
                w.Append($"    <<{stereotype}>>\n");
            foreach (var line in ClassMember.Lines(n.Prop("attributes")).Concat(ClassMember.Lines(n.Prop("operations"))))
            {
                var m = n.Type == ClassModel.Enumeration ? null : ClassMember.Parse(line);
                var text = m is null ? line.Trim() : m.Display + (m.IsStatic ? "$" : "") + (m.IsAbstract ? "*" : "");
                w.Append("    ").Append(text.Replace("{", "(").Replace("}", ")")).Append('\n');
            }
            w.Append("  }\n");
        }
        foreach (var e in d.Edges)
        {
            string M(string key) => e.Prop(key).Trim() is { Length: > 0 } m ? $"\"{m}\" " : "";
            string MAfter(string key) => e.Prop(key).Trim() is { Length: > 0 } m ? $" \"{m}\"" : "";
            var label = e.Text.Trim().Length > 0 ? $" : {Text.Line(e.Text)}" : "";
            var line = e.Type switch
            {
                ClassModel.Association => $"{e.From} {M("fromMultiplicity")}{(e.Prop("navigable") == "true" ? "-->" : "--")}{MAfter("toMultiplicity")} {e.To}{label}",
                ClassModel.Aggregation => $"{e.From} {M("fromMultiplicity")}o--{MAfter("toMultiplicity")} {e.To}{label}",
                ClassModel.Composition => $"{e.From} {M("fromMultiplicity")}*--{MAfter("toMultiplicity")} {e.To}{label}",
                ClassModel.Generalization => $"{e.To} <|-- {e.From}",
                ClassModel.Realization => $"{e.To} <|.. {e.From}",
                ClassModel.Dependency => $"{e.From} ..> {e.To}{label}",
                _ => null,
            };
            if (line is not null)
                w.Append("  ").Append(line).Append('\n');
        }
        return w.ToString();
    }
}
