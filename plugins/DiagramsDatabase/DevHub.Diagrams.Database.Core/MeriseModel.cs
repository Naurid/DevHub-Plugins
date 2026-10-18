using System.Text;
using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Database.Core;

/// <summary>
/// A property of an entity or association, one line of text: <c>nom : VARCHAR(50) // Nom du patient</c>
/// (name, optional type, optional meaning for the data dictionary).
/// </summary>
public sealed record MeriseProperty(string Name, string Type, string Meaning)
{
    public static IEnumerable<MeriseProperty> Parse(string text)
    {
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '_').Trim();
            if (line.Length == 0)
                continue;
            var meaning = "";
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                meaning = line[(comment + 2)..].Trim();
                line = line[..comment].Trim();
            }
            var colon = line.IndexOf(':');
            yield return colon >= 0
                ? new MeriseProperty(line[..colon].Trim(), line[(colon + 1)..].Trim(), meaning)
                : new MeriseProperty(line, "", meaning);
        }
    }
}

/// <summary>A cardinality on an association's leg: 0,1 / 1,1 / 0,n / 1,n.</summary>
public readonly record struct Cardinality(int Min, bool Many)
{
    public static readonly string[] All = ["0,1", "1,1", "0,n", "1,n"];

    public static Cardinality? Parse(string text)
    {
        var t = text.Replace(" ", "").Replace(";", ",").Replace("..", ",").ToLowerInvariant().Replace("*", "n");
        return t switch
        {
            "0,1" => new Cardinality(0, false),
            "1,1" or "1" => new Cardinality(1, false),
            "0,n" or "0,m" => new Cardinality(0, true),
            "1,n" or "1,m" or "n" => new Cardinality(1, true),
            _ => null,
        };
    }

    public override string ToString() => $"{Min},{(Many ? "n" : "1")}";
}

/// <summary>
/// Merise conceptual data model (MCD): entities with their identifier and properties, associations named by a verb with
/// their own properties, and legs carrying cardinalities. The MLD (tables) and SQL come from it by the usual rules.
/// </summary>
public sealed class MeriseModel : IDiagramKindModel
{
    public const string KindId = "merise";

    public const string Entity = "entity";
    public const string Association = "association";
    public const string Note = "note";
    public const string Leg = "leg";
    public const string NoteLink = "noteLink";

    public string Id => KindId;
    public string Name => L.T("Merise data model (MCD)");
    public string Description => L.T("Entities, associations and cardinalities; generates the MLD, the SQL tables and the data dictionary.");

    public IReadOnlyList<NodeTypeInfo> NodeTypes { get; } =
    [
        new(Entity, L.T("Entity"), NodeShape.Rectangle, 170, 110, L.T("Entity"))
            { Description = L.T("A thing the system keeps data about (Patient, Order), with its identifier underlined.") },
        new(Association, L.T("Association"), NodeShape.Ellipse, 130, 60, L.T("verb"))
            { Description = L.T("Links entities, named by a verb (consults, contains); it can have its own properties.") },
        new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note")) { Description = L.T("A comment, tied to shapes with a dashed line.") },
    ];

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; } =
    [
        new(Leg, L.T("Leg (cardinality)"), L.T("Joins an entity to an association; its cardinality says how many times the entity takes part: 0,1 / 1,1 / 0,n / 1,n."))
        {
            Allowed = (a, b) => (a == Entity && b == Association) || (a == Association && b == Entity),
            Props = new Dictionary<string, string> { ["card"] = "0,n" },
        },
        new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
    ];

    public IReadOnlyList<IDiagramRule> Rules { get; } =
    [
        new Rule(Identifiers),
        new Rule(Legs),
        new Rule(Cardinalities),
        new Rule(DuplicateNames),
        new Rule(RepeatedProperties),
        new Rule(MovingProperties),
        new Rule(Lonely),
    ];

    public IReadOnlyList<IDiagramTextExporter> TextExporters { get; } =
    [
        new SchemaSqlExporter(SqlDialect.MySql, Mld.ToSchema),
        new SchemaSqlExporter(SqlDialect.PostgreSql, Mld.ToSchema),
        new SchemaSqlExporter(SqlDialect.SqlServer, Mld.ToSchema),
        new SchemaSqlExporter(SqlDialect.Sqlite, Mld.ToSchema),
        new MldTextExporter(),
        new DataDictionaryExporter(),
    ];

    public IReadOnlyList<DiagramSample> Samples => [.. DatabaseSamples.Merise, .. DatabaseShowcase.Merise];

    public Diagram Create(string title)
    {
        var d = new Diagram { Kind = KindId, Title = title };
        d.Nodes.Add(this.NewNode(d, Entity, new DPoint(160, 140)));
        return d;
    }

    /// <summary>The entity and association a leg joins (either way round).</summary>
    public static (DiagramNode Entity, DiagramNode Association)? Ends(Diagram d, DiagramEdge leg)
    {
        if (leg.Type != Leg || d.Node(leg.From) is not { } a || d.Node(leg.To) is not { } b)
            return null;
        return a.Type == Entity && b.Type == Association ? (a, b) : b.Type == Entity && a.Type == Association ? (b, a) : null;
    }

    public static IReadOnlyList<DiagramEdge> LegsOf(Diagram d, DiagramNode association) =>
        d.Edges.Where(e => e.Type == Leg && (e.From == association.Id || e.To == association.Id) && Ends(d, e) is not null).ToList();

    private static IEnumerable<DiagramProblem> Identifiers(Diagram d)
    {
        foreach (var e in d.Nodes.Where(n => n.Type == Entity && !MeriseProperty.Parse(n.Prop("identifier")).Any()))
            yield return new DiagramProblem(ProblemSeverity.Warning,
                L.T($"\"{e.Text}\" has no identifier: each entity needs one (underlined), e.g. num{SqlNames.Identifier(e.Text)}."), [e.Id]);
    }

    private static IEnumerable<DiagramProblem> Legs(Diagram d)
    {
        foreach (var a in d.Nodes.Where(n => n.Type == Association))
            if (LegsOf(d, a).Count < 2)
                yield return new DiagramProblem(ProblemSeverity.Error,
                    L.T($"The association \"{a.Text}\" needs at least two legs (the same entity twice for a reflexive one)."), [a.Id]);
    }

    private static IEnumerable<DiagramProblem> Cardinalities(Diagram d)
    {
        foreach (var leg in d.Edges.Where(e => e.Type == Leg && Cardinality.Parse(e.Prop("card")) is null))
            yield return new DiagramProblem(ProblemSeverity.Error, L.T("A leg's cardinality is 0,1, 1,1, 0,n or 1,n."), [leg.Id]);
    }

    private static IEnumerable<DiagramProblem> DuplicateNames(Diagram d)
    {
        foreach (var g in d.Nodes.Where(n => n.Type == Entity && n.Text.Trim().Length > 0)
                     .GroupBy(n => SqlNames.Identifier(n.Text).ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return new DiagramProblem(ProblemSeverity.Error, L.T($"Two entities are called \"{g.First().Text}\"."), g.Select(n => n.Id).ToList());
    }

    private static IEnumerable<DiagramProblem> RepeatedProperties(Diagram d)
    {
        var owners = d.Nodes.Where(n => n.Type is Entity or Association)
            .SelectMany(n => MeriseProperty.Parse(n.Prop("identifier") + "\n" + n.Prop("properties")).Select(p => (Node: n, p.Name)))
            .GroupBy(x => SqlNames.Identifier(x.Name).ToLowerInvariant())
            .Where(g => g.Select(x => x.Node.Id).Distinct().Count() > 1);
        foreach (var g in owners)
            yield return new DiagramProblem(ProblemSeverity.Info,
                L.T($"\"{g.First().Name}\" is a property of several shapes: in an MCD each property appears once (rename it, e.g. {g.First().Name + SqlNames.Identifier(g.First().Node.Text)})."),
                g.Select(x => x.Node.Id).Distinct().ToList());
    }

    private static IEnumerable<DiagramProblem> MovingProperties(Diagram d)
    {
        foreach (var a in d.Nodes.Where(n => n.Type == Association && MeriseProperty.Parse(n.Prop("properties")).Any()))
        {
            var legs = LegsOf(d, a);
            if (legs.Count == 2 && legs.Any(l => Cardinality.Parse(l.Prop("card")) is { Many: false }))
                yield return new DiagramProblem(ProblemSeverity.Info,
                    L.T($"\"{a.Text}\" has a 0,1 or 1,1 leg: its properties go to that entity's table in the MLD (they could belong to the entity)."), [a.Id]);
        }
    }

    private static IEnumerable<DiagramProblem> Lonely(Diagram d)
    {
        if (d.Nodes.Count(n => n.Type == Entity) < 2)
            yield break;
        foreach (var e in d.Nodes.Where(n => n.Type == Entity && !d.Edges.Any(x => x.Type == Leg && (x.From == n.Id || x.To == n.Id))))
            yield return new DiagramProblem(ProblemSeverity.Info, L.T($"\"{e.Text}\" isn't linked to any association."), [e.Id]);
    }
}

/// <summary>
/// The usual Merise rules from MCD to MLD: an entity becomes a table, its identifier the primary key; a binary
/// association with a 0,1 / 1,1 leg becomes a foreign key in that entity's table (with the association's properties);
/// any other association (n–n, n-ary) becomes a table whose key is the foreign keys together.
/// </summary>
public static class Mld
{
    public static Schema ToSchema(Diagram mcd)
    {
        var schema = new Schema();
        var tables = new Dictionary<string, Table>();
        foreach (var e in mcd.Nodes.Where(n => n.Type == MeriseModel.Entity))
        {
            var name = schema.FreeName(SqlNames.Identifier(e.Text.Length > 0 ? e.Text : "entity"));
            var table = new Table { Name = name, NodeId = e.Id, At = e.Bounds.Center };
            var ids = MeriseProperty.Parse(e.Prop("identifier")).ToList();
            if (ids.Count == 0)
                table.Columns.Add(new Column { Name = "id_" + name, Type = "INT", PrimaryKey = true, NotNull = true, AutoIncrement = true });
            foreach (var p in ids)
                table.Columns.Add(new Column
                {
                    Name = Free(table, SqlNames.Identifier(p.Name)),
                    Type = SqlTypes.IsCounter(p.Type) || p.Type.Length == 0 ? "INT" : p.Type,
                    AutoIncrement = SqlTypes.IsCounter(p.Type) || (p.Type.Length == 0 && ids.Count == 1),
                    PrimaryKey = true,
                    NotNull = true,
                    Comment = p.Meaning,
                });
            AddProperties(table, e.Prop("properties"));
            schema.Tables.Add(table);
            tables[e.Id] = table;
        }

        foreach (var a in mcd.Nodes.Where(n => n.Type == MeriseModel.Association))
        {
            var legs = MeriseModel.LegsOf(mcd, a)
                .Select(l => (Leg: l, Entity: MeriseModel.Ends(mcd, l)!.Value.Entity, Card: Cardinality.Parse(l.Prop("card")) ?? new Cardinality(0, true)))
                .Where(l => tables.ContainsKey(l.Entity.Id))
                .ToList();
            if (legs.Count < 2)
                continue;
            var reflexive = legs.Select(l => l.Entity.Id).Distinct().Count() < legs.Count;
            var ones = legs.Where(l => !l.Card.Many).ToList();
            if (legs.Count == 2 && ones.Count > 0)
            {
                // The foreign key goes where the cardinality is x,1 (a chosen leg, else 1,1 before 0,1).
                var holder = ones.FirstOrDefault(l => l.Leg.Flag("keyHere")) is { Leg: not null } chosen
                    ? chosen
                    : ones.OrderByDescending(l => l.Card.Min).First();
                var other = legs.First(l => l.Leg != holder.Leg);
                var table = tables[holder.Entity.Id];
                AddForeignKey(table, tables[other.Entity.Id], nullable: holder.Card.Min == 0, unique: !other.Card.Many, primary: false,
                    role: reflexive ? Role(other.Leg, a) : other.Leg.Prop("role"));
                AddProperties(table, a.Prop("properties"));
                continue;
            }
            var junction = new Table { Name = schema.FreeName(SqlNames.Identifier(a.Text.Length > 0 ? a.Text : "association")), NodeId = a.Id, At = a.Bounds.Center };
            foreach (var l in legs)
                AddForeignKey(junction, tables[l.Entity.Id], nullable: false, unique: false, primary: true,
                    role: reflexive ? Role(l.Leg, a) : l.Leg.Prop("role"));
            AddProperties(junction, a.Prop("properties"));
            schema.Tables.Add(junction);
        }
        return schema;
    }

    private static string Role(DiagramEdge leg, DiagramNode association) =>
        leg.Prop("role") is { Length: > 0 } role ? role : SqlNames.Identifier(association.Text);

    private static void AddProperties(Table table, string text)
    {
        foreach (var p in MeriseProperty.Parse(text))
            table.Columns.Add(new Column { Name = Free(table, SqlNames.Identifier(p.Name)), Type = p.Type, Comment = p.Meaning });
    }

    private static void AddForeignKey(Table holder, Table target, bool nullable, bool unique, bool primary, string role)
    {
        var key = target.PrimaryKey;
        foreach (var pk in key)
        {
            var name = role.Length > 0 ? SqlNames.Identifier($"{role}_{pk.Name}") : pk.Name;
            if (holder.Column(name) is not null)
                name = Free(holder, $"{target.Name}_{pk.Name}");
            holder.Columns.Add(new Column
            {
                Name = name,
                Type = pk.AutoIncrement || SqlTypes.IsCounter(pk.Type) ? "INT" : pk.Type,
                Foreign = true,
                RefTable = target.Name,
                RefColumn = pk.Name,
                PrimaryKey = primary,
                NotNull = primary || !nullable,
                Unique = unique && key.Count == 1 && !primary,
            });
        }
    }

    private static string Free(Table table, string name)
    {
        var candidate = name;
        for (var i = 2; table.Column(candidate) is not null; i++)
            candidate = $"{name}_{i}";
        return candidate;
    }

    /// <summary>The MLD as an entity-relationship diagram (Merise look: # foreign keys, underlined primary keys).</summary>
    public static Diagram ToDiagram(Schema schema, string title, string notation = ErdModel.NotationMerise)
    {
        var d = new Diagram { Kind = ErdModel.KindId, Title = title };
        d.Props["notation"] = notation;
        var nodes = new Dictionary<Table, DiagramNode>();
        var i = 0;
        foreach (var t in schema.Tables)
        {
            var lines = t.Columns.Select(c => c.ToLine()).ToList();
            var width = Math.Max(140, Math.Max(t.Name.Length * 9, (lines.Count == 0 ? 0 : lines.Max(l => ErdModel.DisplayLength(l, notation))) * 7.2) + 24);
            var height = 34 + Math.Max(1, lines.Count) * 17 + 10;
            var at = t.At ?? new DPoint(160 + i % 4 * 260, 140 + i / 4 * 240);
            var node = new DiagramNode
            {
                Id = DiagramIds.NewIn(d),
                Type = ErdModel.Table,
                X = Math.Round(at.X - width / 2),
                Y = Math.Round(at.Y - height / 2),
                Width = Math.Ceiling(width),
                Height = height,
                Text = t.Name,
            };
            node.Props["columns"] = string.Join("\n", lines);
            if (t.Comment.Length > 0)
                node.Props["comment"] = t.Comment;
            d.Nodes.Add(node);
            nodes[t] = node;
            i++;
        }
        foreach (var t in schema.Tables)
            foreach (var fk in t.ForeignKeys(schema))
            {
                if (schema.Table(fk.Table) is not { } target)
                    continue;
                var edge = new DiagramEdge
                {
                    Id = DiagramIds.NewIn(d),
                    Type = ErdModel.Relationship,
                    From = nodes[t].Id,
                    To = nodes[target].Id,
                    Routing = EdgeRouting.Orthogonal,
                };
                edge.Props["fromEnd"] = fk.Unique ? "zeroOrOne" : "zeroOrMany";
                edge.Props["toEnd"] = fk.Nullable ? "zeroOrOne" : "one";
                d.Edges.Add(edge);
            }
        return d;
    }

    /// <summary>
    /// An MCD rebuilt from tables (after an SQL import): a table whose key is only foreign keys to two or more tables
    /// becomes an association; every other foreign key a binary association.
    /// </summary>
    public static Diagram ToMcd(Schema schema, string title)
    {
        var model = new MeriseModel();
        var d = new Diagram { Kind = MeriseModel.KindId, Title = title };
        bool IsJunction(Table t) => t.PrimaryKey.Count >= 2 && t.PrimaryKey.All(c => c.RefTable is not null)
                                    && t.ForeignKeys(schema).Count >= 2 && t.Columns.Where(c => c.RefTable is not null).All(c => c.PrimaryKey);
        static string Line(Column c) => c.Name + (c.Type.Length > 0 ? " : " + c.Type : "") + (c.Comment.Length > 0 ? " // " + c.Comment : "");
        var entities = new Dictionary<string, DiagramNode>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var t in schema.Tables.Where(t => !IsJunction(t)))
        {
            var n = model.NewNode(d, MeriseModel.Entity, new DPoint(160 + i % 4 * 300, 140 + i / 4 * 260), t.Name);
            n.Props["identifier"] = string.Join("\n", t.PrimaryKey.Where(c => c.RefTable is null).Select(Line));
            n.Props["properties"] = string.Join("\n", t.Columns.Where(c => !c.PrimaryKey && c.RefTable is null).Select(Line));
            n.Height = 40 + Math.Max(2, t.Columns.Count) * 17;
            d.Nodes.Add(n);
            entities[t.Name] = n;
            i++;
        }
        void Link(DiagramNode association, DiagramNode entity, string card)
        {
            var leg = model.NewEdge(d, MeriseModel.Leg, entity, association)!;
            leg.Props["card"] = card;
            d.Edges.Add(leg);
        }
        foreach (var t in schema.Tables)
        {
            if (IsJunction(t))
            {
                var keys = t.ForeignKeys(schema).Where(k => entities.ContainsKey(k.Table)).ToList();
                var centre = keys.Select(k => entities[k.Table].Bounds.Center).Aggregate(new DPoint(0, 0), (s, p) => s + p) * (1.0 / Math.Max(1, keys.Count));
                var a = model.NewNode(d, MeriseModel.Association, centre + new DPoint(0, 40), t.Name);
                a.Props["properties"] = string.Join("\n", t.Columns.Where(c => c.RefTable is null).Select(Line));
                d.Nodes.Add(a);
                foreach (var k in keys)
                    Link(a, entities[k.Table], "0,n");
                continue;
            }
            if (!entities.TryGetValue(t.Name, out var holder))
                continue;
            foreach (var k in t.ForeignKeys(schema).Where(k => entities.ContainsKey(k.Table)))
            {
                var target = entities[k.Table];
                var a = model.NewNode(d, MeriseModel.Association, (holder.Bounds.Center + target.Bounds.Center) * 0.5 + new DPoint(0, holder == target ? 120 : 0),
                    k.Columns[0].Name.Replace("id_", "").Replace("_id", ""));
                d.Nodes.Add(a);
                Link(a, holder, k.Nullable ? "0,1" : "1,1");
                Link(a, target, k.Unique ? "0,1" : "0,n");
            }
        }
        return d;
    }
}

/// <summary>SQL for a diagram, through its tables.</summary>
public sealed class SchemaSqlExporter(SqlDialect dialect, Func<Diagram, Schema> schema) : IDiagramTextExporter
{
    public string Name => $"SQL ({SqlTypes.DialectName(dialect)})";
    public string Extension => "sql";
    public string Export(Diagram diagram) => SqlWriter.Write(schema(diagram), dialect, diagram.Title);
}

/// <summary>The MLD in the textual form of the course: Table (key, column, #foreignKey).</summary>
public sealed class MldTextExporter : IDiagramTextExporter
{
    public string Name => L.T("MLD (text)");
    public string Extension => "txt";

    public string Export(Diagram diagram) => Write(Mld.ToSchema(diagram));

    public static string Write(Schema schema)
    {
        var w = new StringBuilder();
        foreach (var t in schema.Tables)
            w.Append(t.Name).Append(" (")
                .Append(string.Join(", ", t.Columns.Select(c => (c.IsForeign ? "#" : "") + (c.PrimaryKey ? $"_{c.Name}_" : c.Name))))
                .Append(")\n");
        w.Append('\n').Append(L.T("_underlined_ = primary key, # = foreign key")).Append('\n');
        return w.ToString();
    }
}

/// <summary>The data dictionary: every property with its meaning, type, size and constraints (CSV for a spreadsheet).</summary>
public sealed class DataDictionaryExporter : IDiagramTextExporter
{
    public string Name => L.T("Data dictionary (CSV)");
    public string Extension => "csv";

    public string Export(Diagram diagram)
    {
        var w = new StringBuilder();
        Row(w, L.T("Code"), L.T("Meaning"), L.T("Type"), L.T("Size"), L.T("Constraints"), L.T("Entity or association"));
        foreach (var n in diagram.Nodes.Where(n => n.Type is MeriseModel.Entity or MeriseModel.Association).OrderBy(n => n.Type).ThenBy(n => n.Text))
        {
            foreach (var p in MeriseProperty.Parse(n.Prop("identifier")))
                Row(w, p.Name, p.Meaning, Generic(p.Type, true), SqlTypes.Size(p.Type), L.T("Identifier"), n.Text);
            foreach (var p in MeriseProperty.Parse(n.Prop("properties")))
                Row(w, p.Name, p.Meaning, Generic(p.Type, false), SqlTypes.Size(p.Type), "", n.Text);
        }
        return w.ToString();
    }

    private static string Generic(string type, bool identifier) =>
        type.Length == 0 ? (identifier ? "INT" : "VARCHAR") : SqlTypes.IsCounter(type) ? L.T("Counter") : SqlTypes.Generic(type);

    private static void Row(StringBuilder w, params string[] cells) =>
        w.Append(string.Join(",", cells.Select(c => c.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{c.Replace("\"", "\"\"")}\"" : c))).Append("\r\n");
}
