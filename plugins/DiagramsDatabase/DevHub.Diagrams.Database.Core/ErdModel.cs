using System.Text;
using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Database.Core;

/// <summary>
/// The tables of a database: the Merise MLD (# foreign keys, underlined keys) or an entity-relationship diagram in
/// crow's-foot notation, with one line of text per column. Exports SQL; an SQL script imports into one.
/// </summary>
public sealed class ErdModel : IDiagramKindModel
{
    public const string KindId = "erd";

    public const string Table = "table";
    public const string Note = "note";
    public const string Relationship = "relationship";
    public const string NoteLink = "noteLink";

    public const string NotationMerise = "merise";
    public const string NotationCrowsFoot = "crowsfoot";

    /// <summary>The ends of a relationship (crow's foot).</summary>
    public static readonly string[] Ends = ["one", "zeroOrOne", "oneOrMany", "zeroOrMany"];

    public string Id => KindId;
    public string Name => L.T("Database tables (MLD / ERD)");
    public string Description => L.T("Tables, keys and relationships, as a Merise MLD or in crow's-foot notation; SQL export and import.");

    public IReadOnlyList<NodeTypeInfo> NodeTypes { get; } =
    [
        new(Table, L.T("Table"), NodeShape.Rectangle, 200, 110, L.T("table"))
        {
            Description = L.T("A table with one column per line: PK id INT, FK client_id INT -> client.id, nom VARCHAR(50) NOT NULL."),
            Props = new Dictionary<string, string> { ["columns"] = "PK id INT AI" },
        },
        new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note")) { Description = L.T("A comment, tied to shapes with a dashed line.") },
    ];

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; } =
    [
        new(Relationship, L.T("Relationship"), L.T("From the table holding the foreign key to the table it references."))
        {
            Allowed = (a, b) => a == Table && b == Table,
            Routing = EdgeRouting.Orthogonal,
            Props = new Dictionary<string, string> { ["fromEnd"] = "zeroOrMany", ["toEnd"] = "one" },
        },
        new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
    ];

    public IReadOnlyList<IDiagramRule> Rules { get; } = [new Rule(Keys), new Rule(Names), new Rule(References)];

    public IReadOnlyList<IDiagramTextExporter> TextExporters { get; } =
    [
        new SchemaSqlExporter(SqlDialect.MySql, ToSchema),
        new SchemaSqlExporter(SqlDialect.PostgreSql, ToSchema),
        new SchemaSqlExporter(SqlDialect.SqlServer, ToSchema),
        new SchemaSqlExporter(SqlDialect.Sqlite, ToSchema),
        new ErdMermaid(),
        new ErdMldText(),
    ];

    public IReadOnlyList<DiagramSample> Samples => [.. DatabaseSamples.Erd, .. DatabaseShowcase.Erd];

    public Diagram Create(string title)
    {
        var d = new Diagram { Kind = KindId, Title = title };
        d.Props["notation"] = NotationCrowsFoot;
        d.Nodes.Add(this.NewNode(d, Table, new DPoint(160, 140)));
        return d;
    }

    public static IReadOnlyList<Column> Columns(DiagramNode table) =>
        table.Prop("columns").Replace("\r", "").Split('\n').Select(Column.Parse).OfType<Column>().ToList();

    /// <summary>A column as the diagram shows it: "#client_id" in Merise, "client_id  INT" in crow's foot.</summary>
    public static string Display(Column c, string notation) =>
        notation == NotationMerise ? (c.IsForeign ? "#" : "") + c.Name : $"{c.Name}  {c.Type}".TrimEnd();

    /// <summary>About how many characters wide a column line is shown (for sizing generated tables).</summary>
    public static int DisplayLength(string line, string notation) =>
        Column.Parse(line) is { } c ? Display(c, notation).Length + (notation == NotationMerise ? 0 : 6) : line.Length;

    /// <summary>
    /// The tables of the diagram. A column marked FK without "-> table.column" takes the table a relationship from its
    /// table points to (the one whose key has the column's name, else the only one).
    /// </summary>
    public static Schema ToSchema(Diagram d)
    {
        var schema = new Schema();
        foreach (var n in d.Nodes.Where(n => n.Type == Table))
        {
            var t = new Table { Name = n.Text.Trim().Length > 0 ? n.Text.Trim() : "table", NodeId = n.Id, At = n.Bounds.Center, Comment = n.Prop("comment") };
            t.Columns.AddRange(Columns(n));
            schema.Tables.Add(t);
        }
        foreach (var n in d.Nodes.Where(n => n.Type == Table))
        {
            var t = schema.Tables.First(x => x.NodeId == n.Id);
            var targets = d.Edges.Where(e => e.Type == Relationship && e.From == n.Id)
                .Select(e => schema.Tables.FirstOrDefault(x => x.NodeId == e.To)).OfType<Table>().ToList();
            foreach (var c in t.Columns.Where(c => c.Foreign && c.RefTable is null))
            {
                var target = targets.FirstOrDefault(x => x.PrimaryKey.Any(k => k.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                             ?? targets.FirstOrDefault(x => c.Name.Contains(x.Name, StringComparison.OrdinalIgnoreCase))
                             ?? (targets.Count == 1 ? targets[0] : null);
                if (target is null)
                    continue;
                c.RefTable = target.Name;
                c.RefColumn = target.PrimaryKey.FirstOrDefault(k => k.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))?.Name
                              ?? target.PrimaryKey.FirstOrDefault()?.Name;
            }
        }
        return schema;
    }

    private static IEnumerable<DiagramProblem> Keys(Diagram d)
    {
        foreach (var n in d.Nodes.Where(n => n.Type == Table && !Columns(n).Any(c => c.PrimaryKey)))
            yield return new DiagramProblem(ProblemSeverity.Warning, L.T($"The table \"{n.Text}\" has no primary key (start a column with PK)."), [n.Id]);
    }

    private static IEnumerable<DiagramProblem> Names(Diagram d)
    {
        foreach (var g in d.Nodes.Where(n => n.Type == Table).GroupBy(n => n.Text.Trim().ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return new DiagramProblem(ProblemSeverity.Error, L.T($"Two tables are called \"{g.First().Text}\"."), g.Select(n => n.Id).ToList());
        foreach (var n in d.Nodes.Where(n => n.Type == Table))
            foreach (var g in Columns(n).GroupBy(c => c.Name.ToLowerInvariant()).Where(g => g.Count() > 1))
                yield return new DiagramProblem(ProblemSeverity.Error, L.T($"\"{n.Text}\" has two columns called \"{g.First().Name}\"."), [n.Id]);
    }

    private static IEnumerable<DiagramProblem> References(Diagram d)
    {
        var schema = ToSchema(d);
        foreach (var t in schema.Tables)
            foreach (var c in t.Columns.Where(c => c.IsForeign))
            {
                if (c.RefTable is null)
                {
                    yield return new DiagramProblem(ProblemSeverity.Warning,
                        L.T($"{t.Name}.{c.Name} is a foreign key to no table: write \"-> table.column\" or draw a relationship."), [t.NodeId!]);
                    continue;
                }
                if (schema.Table(c.RefTable) is not { } target)
                    yield return new DiagramProblem(ProblemSeverity.Error, L.T($"{t.Name}.{c.Name} references \"{c.RefTable}\", which isn't a table here."), [t.NodeId!]);
                else if (c.RefColumn is not null && target.Column(c.RefColumn) is null)
                    yield return new DiagramProblem(ProblemSeverity.Error,
                        L.T($"{t.Name}.{c.Name} references {target.Name}.{c.RefColumn}, which doesn't exist."), [t.NodeId!]);
            }
    }
}

/// <summary>Mermaid's erDiagram.</summary>
public sealed class ErdMermaid : IDiagramTextExporter
{
    public string Name => "Mermaid";
    public string Extension => "mmd";

    public string Export(Diagram d)
    {
        var w = new StringBuilder("erDiagram\n");
        foreach (var n in d.Nodes.Where(n => n.Type == ErdModel.Table))
        {
            w.Append($"  {SqlNames.Identifier(n.Text)} {{\n");
            foreach (var c in ErdModel.Columns(n))
            {
                var keys = string.Join(",", new[] { c.PrimaryKey ? "PK" : null, c.IsForeign ? "FK" : null, c.Unique && !c.PrimaryKey ? "UK" : null }.OfType<string>());
                w.Append($"    {SqlNames.Identifier(c.Type.Length > 0 ? c.Type : "string")} {SqlNames.Identifier(c.Name)}{(keys.Length > 0 ? " " + keys : "")}\n");
            }
            w.Append("  }\n");
        }
        foreach (var e in d.Edges.Where(e => e.Type == ErdModel.Relationship))
        {
            if (d.Node(e.From) is not { } a || d.Node(e.To) is not { } b)
                continue;
            var left = e.Prop("fromEnd", "zeroOrMany") switch { "one" => "||", "zeroOrOne" => "|o", "oneOrMany" => "}|", _ => "}o" };
            var right = e.Prop("toEnd", "one") switch { "zeroOrOne" => "o|", "oneOrMany" => "|{", "zeroOrMany" => "o{", _ => "||" };
            var label = e.Text.Trim().Length > 0 ? e.Text.Trim().Replace("\"", "'") : "references";
            w.Append($"  {SqlNames.Identifier(a.Text)} {left}--{right} {SqlNames.Identifier(b.Text)} : \"{label}\"\n");
        }
        return w.ToString();
    }
}

/// <summary>The tables in the course's textual MLD form.</summary>
public sealed class ErdMldText : IDiagramTextExporter
{
    public string Name => L.T("MLD (text)");
    public string Extension => "txt";
    public string Export(Diagram diagram) => MldTextExporter.Write(ErdModel.ToSchema(diagram));
}
