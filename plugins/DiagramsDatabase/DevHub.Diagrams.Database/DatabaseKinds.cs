using Avalonia.Media;
using DevHub.Diagrams.Core;
using DevHub.Diagrams.Database.Core;
using DevHub.Diagrams.Rendering;
using DevHub.Sdk.Core.Localization;
using Icons = DevHub.Controls.IconKind;

namespace DevHub.Diagrams.Database;

/// <summary>Shared commands: SQL import, and where generated diagrams go.</summary>
internal static class DatabaseCommands
{
    /// <summary>A free path for a generated diagram beside the open one (or in the project's diagrams folder).</summary>
    public static string? PathFor(IDiagramEditor editor, string name, string? fallbackFolder = null)
    {
        var folder = editor.FilePath is { } file ? Path.GetDirectoryName(file)
            : editor.ProjectFolder is { } project ? Path.Combine(project, ProjectLinks.DiagramsFolder)
            : fallbackFolder;
        if (folder is null)
            return null;
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        var path = Path.Combine(folder, safe + DiagramFile.Extension);
        for (var i = 2; File.Exists(path) && path != editor.FilePath; i++)
            path = Path.Combine(folder, $"{safe} {i}{DiagramFile.Extension}");
        return path;
    }

    public static KindCommand ImportSql => new(L.T("Import SQL…"), Icons.Import, async editor =>
    {
        var file = await editor.Dialogs.PickFileAsync(L.T("Import SQL"), L.T("SQL scripts"), ["*.sql"]);
        if (file is null)
            return;
        Schema schema;
        try
        {
            schema = SqlImport.Read(await File.ReadAllTextAsync(file));
        }
        catch (IOException e)
        {
            editor.Status.Show(L.T($"The script couldn't be read: {e.Message}"));
            return;
        }
        if (schema.Tables.Count == 0)
        {
            editor.Status.Show(L.T("No CREATE TABLE found in the script."));
            return;
        }
        var name = Path.GetFileNameWithoutExtension(file);
        var mld = Mld.ToDiagram(schema, name, ErdModel.NotationCrowsFoot);
        Arrange(mld);
        var mcd = Mld.ToMcd(schema, name);
        Arrange(mcd);
        if (PathFor(editor, name + " (MLD)", Path.GetDirectoryName(file)) is { } mldPath)
            await editor.OpenGeneratedAsync(mld, mldPath);
        if (PathFor(editor, name + " (MCD)", Path.GetDirectoryName(file)) is { } mcdPath)
            await editor.OpenGeneratedAsync(mcd, mcdPath);
        editor.Status.Show(L.T($"Imported {schema.Tables.Count} tables: the MLD and a rebuilt MCD are open."));
    })
    { Description = L.T("Reads CREATE TABLE statements into a table diagram and rebuilds the Merise MCD from them.") };

    /// <summary>Sizes a generated diagram's shapes to their text, then lays it out in layers (referenced tables above).</summary>
    public static void Arrange(Diagram d)
    {
        MeasureSurface.FitAll(d, d.Kind == ErdModel.KindId ? new ErdKind() : new MeriseKind());
        AutoLayout.Layered(d, d.Nodes.Select(n => n.Id).ToList(), leftToRight: false,
            reversed: new HashSet<string> { ErdModel.Relationship }, gapAcross: 60, gapAlong: 80).Apply(d);
    }
}

/// <summary>Merise MCD: entities, associations, legs with cardinalities.</summary>
public sealed class MeriseKind : DiagramKindBase
{
    private const double Row = 16;

    public override IDiagramKindModel Model { get; } = new MeriseModel();
    public override string IconKind => Icons.Template;
    public override int Order => 70;

    public override IReadOnlyList<PropertyField> NodeFields(DiagramNode n) => n.Type switch
    {
        MeriseModel.Entity =>
        [
            new(L.T("Name"), "@text"),
            new(L.T("Identifier"), "identifier", FieldKind.MultilineText,
                L.T("Underlined. name : type // meaning, e.g. numPatient : Compteur (several lines for a composite one).")),
            new(L.T("Properties"), "properties", FieldKind.MultilineText,
                L.T("One per line: name : type // meaning, e.g. nom : Alphanumérique(50) // Nom de famille.")),
            .. StyleFields,
        ],
        MeriseModel.Association =>
        [
            new(L.T("Verb"), "@text"),
            new(L.T("Properties"), "properties", FieldKind.MultilineText, L.T("Properties of the link itself, e.g. quantité : Entier.")),
            .. StyleFields,
        ],
        _ => base.NodeFields(n),
    };

    public override IReadOnlyList<PropertyField> EdgeFields(DiagramEdge e) => e.Type == MeriseModel.Leg
        ?
        [
            new(L.T("Cardinality"), "card", FieldKind.Choice)
            {
                Choices =
                [
                    new("0,1", L.T("0,1: at most once")), new("1,1", L.T("1,1: exactly once")),
                    new("0,n", L.T("0,n: any number of times")), new("1,n", L.T("1,n: at least once")),
                ],
                Default = "0,n",
            },
            new(L.T("Role"), "role", Hint: L.T("Names the leg (needed for a reflexive association: chef, subordonné)")),
            new(L.T("Foreign key on this side (1,1 – 1,1)"), "keyHere", FieldKind.Toggle),
            .. LineStyleFields,
        ]
        : base.EdgeFields(e);

    public override IReadOnlyList<KindCommand> Commands =>
    [
        new(L.T("Generate MLD"), Icons.Run, async editor =>
        {
            var schema = Mld.ToSchema(editor.Diagram);
            if (schema.Tables.Count == 0)
            {
                editor.Status.Show(L.T("Add entities first: each one becomes a table."));
                return;
            }
            var title = (editor.Diagram.Title.Length > 0 ? editor.Diagram.Title : L.T("Data model")) + " (MLD)";
            if (DatabaseCommands.PathFor(editor, editor.FilePath is { } f ? DiagramFile.NameOf(f) + " (MLD)" : title) is not { } path)
            {
                editor.Status.Show(L.T("Save the MCD first: the MLD is written next to it."));
                return;
            }
            // Regenerating keeps the place of tables already moved in the MLD.
            var mld = Mld.ToDiagram(schema, title);
            MeasureSurface.FitAll(mld, new ErdKind());
            mld.Project = editor.Diagram.Project is null || editor.FilePath is null ? null : editor.Diagram.Project;
            if (File.Exists(path))
                try
                {
                    var old = await DiagramFile.LoadAsync(path);
                    foreach (var n in mld.Nodes)
                        if (old.Nodes.FirstOrDefault(o => o.Type == ErdModel.Table && o.Text == n.Text) is { } before)
                            (n.X, n.Y) = (before.X, before.Y);
                    mld.Props["notation"] = old.Props.GetValueOrDefault("notation", ErdModel.NotationMerise);
                }
                catch (Exception e) when (e is IOException or DiagramFileException)
                {
                    // An unreadable old MLD is replaced.
                }
            await editor.OpenGeneratedAsync(mld, path);
        })
        { Description = L.T("Writes the tables (MLD) by the Merise rules into a diagram next to this one, and opens it.") },
        DatabaseCommands.ImportSql,
    ];

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        var pen = PenOf(s, n);
        switch (n.Type)
        {
            case MeriseModel.Entity:
            {
                s.Rect(r, FillOf(s, n), pen);
                s.Text(n.Text, new DRect(r.X, r.Y + 4, r.Width, 20), TextOf(n, new DText(13, Bold: true)), wrap: false);
                s.Line(new DPoint(r.X, r.Y + 27), new DPoint(r.Right, r.Y + 27), pen);
                var y = r.Y + 32;
                foreach (var p in MeriseProperty.Parse(n.Prop("identifier")))
                    y = Property(s, r, y, p, underline: true);
                foreach (var p in MeriseProperty.Parse(n.Prop("properties")))
                    y = Property(s, r, y, p, underline: false);
                break;
            }
            case MeriseModel.Association:
            {
                s.Ellipse(r, FillOf(s, n), pen);
                var props = MeriseProperty.Parse(n.Prop("properties")).ToList();
                var name = TextOf(n, new DText(12.5, Bold: true));
                if (props.Count == 0)
                {
                    s.Text(n.Text, r.Inflate(-r.Width * 0.1), name);
                    break;
                }
                var top = r.Center.Y - (props.Count * Row + 22) / 2;
                s.Text(n.Text, new DRect(r.X + r.Width * 0.12, top, r.Width * 0.76, 20), name, wrap: false);
                var half = Math.Sqrt(Math.Max(0, 1 - Math.Pow((top + 22 - r.Center.Y) / (r.Height / 2), 2))) * r.Width / 2;
                s.Line(new DPoint(r.Center.X - half, top + 22), new DPoint(r.Center.X + half, top + 22), pen);
                var y = top + 24;
                foreach (var p in props)
                {
                    s.Text(p.Name, new DRect(r.X, y, r.Width, Row), new DText(11.5), wrap: false);
                    y += Row;
                }
                break;
            }
            case MeriseModel.Note:
                Note(s, n);
                break;
            default:
                base.DrawNode(s, d, n);
                break;
        }
    }

    private static double Property(IDiagramSurface s, DRect r, double y, MeriseProperty p, bool underline)
    {
        s.Text(p.Name, new DRect(r.X + 8, y, r.Width - 16, Row), new DText(11.5, Underline: underline, Align: TextAlign.Left), wrap: false);
        return y + Row;
    }

    public override void DrawEdge(IDiagramSurface s, Diagram d, DiagramEdge e, IReadOnlyList<DPoint> path)
    {
        if (e.Type != MeriseModel.Leg)
        {
            s.Polyline(path, PenOf(s, e, StrokeDash.Dashed));
            return;
        }
        if (path.Count < 2)
            return;
        s.Polyline(path, PenOf(s, e));
        // The cardinality sits by the entity, the role by the association.
        var entityAtStart = d.Node(e.From)?.Type == MeriseModel.Entity;
        EndLabel(s, e.Prop("card", "0,n"), path, atStart: entityAtStart, side: 1);
        if (e.Prop("role") is { Length: > 0 } role)
            EndLabel(s, role, path, atStart: !entityAtStart, side: -1);
    }

    public override DRect? Fit(DiagramNode n, IDiagramSurface m)
    {
        DRect fit;
        if (n.Type == MeriseModel.Entity)
        {
            var props = MeriseProperty.Parse(n.Prop("identifier") + "\n" + n.Prop("properties")).ToList();
            var width = Math.Max(m.Measure(n.Text, new DText(13, Bold: true)).Width + 24, props.Count == 0 ? 0 : props.Max(p => m.Measure(p.Name, new DText(11.5)).Width) + 20);
            fit = n.Bounds with { Width = Math.Ceiling(Math.Max(n.Width, Math.Max(120, width))), Height = Math.Max(60, 32 + props.Count * Row + 8) };
        }
        else if (n.Type == MeriseModel.Association)
        {
            var props = MeriseProperty.Parse(n.Prop("properties")).ToList();
            var width = Math.Max(m.Measure(n.Text, new DText(12.5, Bold: true)).Width, props.Count == 0 ? 0 : props.Max(p => m.Measure(p.Name, new DText(11.5)).Width));
            fit = n.Bounds with
            {
                Width = Math.Ceiling(Math.Max(n.Width, width * 1.45 + 24)),
                Height = Math.Ceiling(Math.Max(props.Count == 0 ? 50 : 0, props.Count == 0 ? n.Height : (props.Count * Row + 22) * 1.45 + 10)),
            };
        }
        else
            return null;
        return fit == n.Bounds ? null : fit;
    }
}

/// <summary>Tables: the Merise MLD or a crow's-foot entity-relationship diagram.</summary>
public sealed class ErdKind : DiagramKindBase
{
    private const double Row = 17;
    private const double Header = 28;

    public override IDiagramKindModel Model { get; } = new ErdModel();
    public override string IconKind => Icons.Menu;
    public override int Order => 71;

    public override bool MultilineText(DiagramNode n) => false;

    private static string Notation(Diagram d) => d.Props.GetValueOrDefault("notation", ErdModel.NotationCrowsFoot);

    public override IReadOnlyList<PropertyField> DiagramFields =>
    [
        new(L.T("Title"), "@title"),
        new(L.T("Notation"), "notation", FieldKind.Choice)
        {
            Choices = [new(ErdModel.NotationCrowsFoot, L.T("Crow's foot (ERD)")), new(ErdModel.NotationMerise, L.T("Merise MLD (#foreign keys)"))],
            Default = ErdModel.NotationCrowsFoot,
        },
    ];

    public override IReadOnlyList<PropertyField> NodeFields(DiagramNode n) => n.Type == ErdModel.Table
        ?
        [
            new(L.T("Table name"), "@text"),
            new(L.T("Columns"), "columns", FieldKind.MultilineText,
                L.T("One per line: [PK] [FK] name TYPE [AI] [NOT NULL] [UNIQUE] [DEFAULT x] [-> table.column] [// comment].")),
            new(L.T("Comment"), "comment"),
            .. StyleFields,
        ]
        : base.NodeFields(n);

    private static readonly IReadOnlyList<FieldChoice> EndChoices =
    [
        new("one", L.T("Exactly one")), new("zeroOrOne", L.T("Zero or one")),
        new("oneOrMany", L.T("One or many")), new("zeroOrMany", L.T("Zero or many")),
    ];

    public override IReadOnlyList<PropertyField> EdgeFields(DiagramEdge e) => e.Type == ErdModel.Relationship
        ?
        [
            new(L.T("Name"), "@text"),
            new(L.T("At the foreign key's table"), "fromEnd", FieldKind.Choice) { Choices = EndChoices, Default = "zeroOrMany" },
            new(L.T("At the referenced table"), "toEnd", FieldKind.Choice) { Choices = EndChoices, Default = "one" },
            new(L.T("Route"), "@routing", FieldKind.Choice) { Choices = [new("straight", L.T("Straight")), new("orthogonal", L.T("Right angles"))] },
            .. LineStyleFields,
        ]
        : base.EdgeFields(e);

    public override IReadOnlyList<KindCommand> Commands =>
    [
        DatabaseCommands.ImportSql,
        new(L.T("Rebuild MCD"), Icons.Refresh, async editor =>
        {
            var schema = ErdModel.ToSchema(editor.Diagram);
            var name = editor.FilePath is { } f ? DiagramFile.NameOf(f).Replace(" (MLD)", "") + " (MCD)" : L.T("Data model") + " (MCD)";
            if (DatabaseCommands.PathFor(editor, name) is not { } path)
            {
                editor.Status.Show(L.T("Save the diagram first: the MCD is written next to it."));
                return;
            }
            var mcd = Mld.ToMcd(schema, editor.Diagram.Title.Replace(" (MLD)", ""));
            DatabaseCommands.Arrange(mcd);
            await editor.OpenGeneratedAsync(mcd, path);
        })
        { Description = L.T("Rebuilds a Merise MCD from these tables (a table keyed by two foreign keys becomes an association).") },
    ];

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        if (n.Type != ErdModel.Table)
        {
            if (n.Type == ErdModel.Note)
                Note(s, n);
            else
                base.DrawNode(s, d, n);
            return;
        }
        var r = n.Bounds;
        var pen = PenOf(s, n);
        var merise = Notation(d) == ErdModel.NotationMerise;
        s.Rect(r, FillOf(s, n), pen);
        var headerFill = TryColor(n.Prop("fill")) is { } fill ? fill : merise ? (Color?)null : Mix(s.Style.Accent, s.Style.Fill, 0.18);
        if (headerFill is { } header)
            s.Rect(new DRect(r.X, r.Y, r.Width, Header), header, null);
        s.Text(n.Text, new DRect(r.X + 4, r.Y, r.Width - 8, Header), TextOf(n, new DText(12.5, Bold: true)), wrap: false);
        s.Line(new DPoint(r.X, r.Y + Header), new DPoint(r.Right, r.Y + Header), pen);
        var y = r.Y + Header + 4;
        var muted = new DText(10.5, Color: s.Style.MutedText, Align: TextAlign.Left, Mono: true);
        foreach (var c in ErdModel.Columns(n))
        {
            if (merise)
            {
                s.Text(ErdModel.Display(c, ErdModel.NotationMerise), new DRect(r.X + 8, y, r.Width - 16, Row),
                    new DText(11.5, Underline: c.PrimaryKey, Align: TextAlign.Left), wrap: false);
            }
            else
            {
                var keys = (c.PrimaryKey ? "PK" : "") + (c.PrimaryKey && c.IsForeign ? "," : "") + (c.IsForeign ? "FK" : "");
                s.Text(keys, new DRect(r.X + 6, y, 34, Row), muted, wrap: false);
                s.Text(c.Name, new DRect(r.X + 42, y, r.Width - 48, Row),
                    new DText(11.5, Bold: c.PrimaryKey, Underline: false, Align: TextAlign.Left), wrap: false);
                s.Text(c.Type + (c.NotNull && !c.PrimaryKey ? " ●" : ""), new DRect(r.X + 42, y, r.Width - 50, Row), muted with { Align = TextAlign.Right }, wrap: false);
            }
            y += Row;
        }
    }

    private static Color Mix(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R * k + b.R * (1 - k)), (byte)(a.G * k + b.G * (1 - k)), (byte)(a.B * k + b.B * (1 - k)));

    public override void DrawEdge(IDiagramSurface s, Diagram d, DiagramEdge e, IReadOnlyList<DPoint> path)
    {
        if (path.Count < 2)
            return;
        if (e.Type != ErdModel.Relationship)
        {
            s.Polyline(path, PenOf(s, e, StrokeDash.Dashed));
            return;
        }
        var pen = PenOf(s, e);
        s.Polyline(path, pen);
        if (Notation(d) == ErdModel.NotationMerise)
            DrawEnd(s, path[^1], path[^2], LineEnd.Open, pen.Color);
        else
        {
            CrowsFoot(s, path[0], path[1], e.Prop("fromEnd", "zeroOrMany"), pen);
            CrowsFoot(s, path[^1], path[^2], e.Prop("toEnd", "one"), pen);
        }
        if (e.Text.Length > 0)
            Label(s, e.Text, Routing.LabelPoint(path, e.LabelAt), 10.5, s.Style.MutedText, italic: true);
    }

    /// <summary>Crow's-foot end at <paramref name="tip"/> (on the table's edge), the line coming from <paramref name="from"/>.</summary>
    public static void CrowsFoot(IDiagramSurface s, DPoint tip, DPoint from, string end, DPen pen)
    {
        var dx = tip.X - from.X;
        var dy = tip.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.01)
            return;
        var u = new DPoint(dx / len, dy / len);      // towards the table
        var v = new DPoint(-u.Y, u.X);
        DPoint At(double back, double side) => tip - u * back + v * side;
        var many = end is "oneOrMany" or "zeroOrMany";
        var optional = end is "zeroOrOne" or "zeroOrMany";
        if (many)
        {
            s.Line(At(12, 0), At(0, 7), pen);
            s.Line(At(12, 0), At(0, -7), pen);
        }
        else
            s.Line(At(5, 6), At(5, -6), pen);
        if (optional)
        {
            var c = At(many ? 18 : 15, 0);
            s.Ellipse(new DRect(c.X - 4.5, c.Y - 4.5, 9, 9), s.Style.Background, pen);
        }
        else
            s.Line(At(many ? 15 : 10, 6), At(many ? 15 : 10, -6), pen);
    }

    public override DRect? Fit(DiagramNode n, IDiagramSurface m)
    {
        if (n.Type != ErdModel.Table)
            return null;
        var columns = ErdModel.Columns(n);
        var width = m.Measure(n.Text, new DText(12.5, Bold: true)).Width + 24;
        foreach (var c in columns)
            width = Math.Max(width, Math.Max(
                m.Measure(ErdModel.Display(c, ErdModel.NotationMerise), new DText(11.5)).Width + 20,
                42 + m.Measure(c.Name, new DText(11.5, Bold: c.PrimaryKey)).Width + 16 + m.Measure(c.Type + " ●", new DText(10.5, Mono: true)).Width + 10));
        var fit = n.Bounds with { Width = Math.Ceiling(Math.Max(width, 140)), Height = Header + 8 + Math.Max(1, columns.Count) * Row };
        return fit == n.Bounds ? null : fit;
    }
}
