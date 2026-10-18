using DevHub.Diagrams.Business.Core;
using DevHub.Diagrams.Core;
using DevHub.Diagrams.Uml.Core;
using DevHub.MoodBoard.Core;
using Xunit;

namespace DevHub.Diagrams.Kinds.Tests;

public class UmlTests
{
    private static DiagramNode Add(Diagram d, IDiagramKindModel kind, string type, double x, double y, string? text = null, string? parent = null)
    {
        var n = kind.NewNode(d, type, new DPoint(x, y), text);
        n.ParentId = parent;
        d.Nodes.Add(n);
        return n;
    }

    private static DiagramEdge Link(Diagram d, IDiagramKindModel kind, string type, DiagramNode a, DiagramNode b, string text = "")
    {
        var e = kind.NewEdge(d, type, a, b) ?? throw new InvalidOperationException($"{type} refused between {a.Type} and {b.Type}");
        if (text.Length > 0)
            e.Text = text;
        d.Edges.Add(e);
        return e;
    }

    [Fact]
    public void Class_members_read_the_course_notation()
    {
        var a = ClassMember.Parse("+ size : Area = (100,100)")!;
        Assert.Equal(('+', "size", "Area", "(100,100)"), (a.Visibility, a.Name, a.Type, a.Default));
        Assert.False(a.IsOperation);

        var op = ClassMember.Parse("# static display(x : int = 0) : void")!;
        Assert.Equal(('#', "display", "x : int = 0", "void"), (op.Visibility, op.Name, op.Parameters, op.Type));
        Assert.True(op.IsStatic);
        Assert.Equal("#display(x : int = 0) : void", op.Display);

        Assert.True(ClassMember.Parse("_- count : int_")!.IsStatic);
        Assert.True(ClassMember.Parse("+ abstract draw()")!.IsAbstract);
        Assert.Null(ClassMember.Parse("  "));
    }

    [Fact]
    public void Use_case_rules_follow_the_course()
    {
        var kind = new UseCaseModel();
        var d = kind.Create("Hôpital");
        var system = d.Nodes[0];
        var inside = Add(d, kind, UseCaseModel.Actor, 400, 260, "Médecin", system.Id);
        var outside = Add(d, kind, UseCaseModel.UseCase, 900, 260, "Consulter");
        var problems = kind.Check(d);
        Assert.Contains(problems, p => p.ElementIds.Contains(inside.Id) && p.Message.Contains("outside"));
        Assert.Contains(problems, p => p.ElementIds.Contains(outside.Id) && p.Message.Contains("inside"));

        // «include» goes between use cases only.
        Assert.Null(kind.NewEdge(d, UseCaseModel.Include, inside, outside));
    }

    [Fact]
    public void Activity_decisions_need_guards_and_starts_have_no_inflow()
    {
        var kind = new ActivityModel();
        var d = kind.Create("Distributeur");
        var start = d.Nodes[0];
        var check = Add(d, kind, ActivityModel.Decision, 80, 200);
        var a = Add(d, kind, ActivityModel.Action, 0, 300, "Donner les billets");
        var b = Add(d, kind, ActivityModel.Action, 200, 300, "Rendre la carte");
        Link(d, kind, ActivityModel.Flow, start, check);
        Link(d, kind, ActivityModel.Flow, check, a, "[solde suffisant]");
        var unguarded = Link(d, kind, ActivityModel.Flow, check, b);
        var back = Link(d, kind, ActivityModel.Flow, b, start);
        var problems = kind.Check(d);
        Assert.Contains(problems, p => p.ElementIds.Contains(unguarded.Id));
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Error && p.ElementIds.Contains(back.Id));
        Assert.Contains("[solde suffisant]", new ActivityPlantUml().Export(d));
    }

    [Fact]
    public void Sequence_messages_run_straight_across_in_order()
    {
        var kind = new SequenceModel();
        var d = kind.Create("Retrait");
        var client = Add(d, kind, SequenceModel.Lifeline, 100, 300, ":Client");
        var atm = Add(d, kind, SequenceModel.Lifeline, 400, 300, ":Distributeur");
        var first = Link(d, kind, SequenceModel.Sync, client, atm, "insérerCarte()");
        var second = Link(d, kind, SequenceModel.Reply, atm, client, "demanderCode");
        var p1 = kind.PathOf(d, first);
        var p2 = kind.PathOf(d, second);
        Assert.Equal(p1[0].Y, p1[^1].Y);
        Assert.True(p2[0].Y > p1[0].Y);
        Assert.True(p1[^1].X > p1[0].X);
        var uml = new SequencePlantUml().Export(d);
        Assert.True(uml.IndexOf("insérerCarte()", StringComparison.Ordinal) < uml.IndexOf("demanderCode", StringComparison.Ordinal));
    }

    [Fact]
    public void Class_exports_carry_relations_and_multiplicities()
    {
        var kind = new ClassModel();
        var d = kind.Create("Hôpital");
        var patient = Add(d, kind, ClassModel.Class, 0, 0, "Patient");
        patient.Props["attributes"] = "- nom : String\n+ static compteur : int";
        var invoice = Add(d, kind, ClassModel.Class, 300, 0, "Facture");
        var e = Link(d, kind, ClassModel.Composition, patient, invoice);
        e.Props["toMultiplicity"] = "0..*";
        var uml = new ClassPlantUml().Export(d);
        Assert.Contains($"class \"Patient\" as {patient.Id}", uml);
        Assert.Contains("{static}", uml);
        Assert.Contains("*--", uml);
        Assert.Contains("\"0..*\"", uml);
        Assert.Contains("classDiagram", new ClassMermaid().Export(d));
    }
}

public class BusinessTests
{
    private static DiagramNode Add(Diagram d, IDiagramKindModel kind, string type, double x, double y, string? text = null, string? parent = null)
    {
        var n = kind.NewNode(d, type, new DPoint(x, y), text);
        n.ParentId = parent;
        d.Nodes.Add(n);
        return n;
    }

    [Fact]
    public void Bpmn_keeps_sequence_flows_in_a_pool_and_messages_between_pools()
    {
        var kind = new BpmnModel();
        var d = kind.Create("Commande");
        var shop = d.Nodes[0];
        var start = d.Nodes[1];
        var customer = Add(d, kind, BpmnModel.Pool, 460, 600, "Client");
        var pay = Add(d, kind, BpmnModel.Task, 300, 600, "Payer", customer.Id);
        var ship = Add(d, kind, BpmnModel.Task, 300, 200, "Expédier", shop.Id);

        var wrong = kind.NewEdge(d, BpmnModel.SequenceFlow, ship, pay)!;
        d.Edges.Add(wrong);
        var alsoWrong = kind.NewEdge(d, BpmnModel.MessageFlow, start, ship)!;
        d.Edges.Add(alsoWrong);
        var right = kind.NewEdge(d, BpmnModel.MessageFlow, ship, pay)!;
        d.Edges.Add(right);

        var problems = kind.Check(d);
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Error && p.ElementIds.Contains(wrong.Id));
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Error && p.ElementIds.Contains(alsoWrong.Id));
        Assert.DoesNotContain(problems, p => p.ElementIds.Contains(right.Id));
        Assert.Contains(problems, p => p.ElementIds.Contains(shop.Id) && p.Message.Contains("end event"));
        Assert.Same(shop, BpmnModel.PoolOf(d, ship));
    }

    [Fact]
    public void Flowchart_decisions_need_two_ways_out_and_export_to_mermaid()
    {
        var kind = new FlowchartModel();
        var d = kind.Create("Connexion");
        var question = Add(d, kind, FlowchartModel.Decision, 200, 200, "Mot de passe correct ?");
        var ok = Add(d, kind, FlowchartModel.Process, 200, 340, "Ouvrir la session");
        var flow = kind.NewEdge(d, FlowchartModel.Flow, question, ok)!;
        flow.Text = "oui";
        d.Edges.Add(flow);
        Assert.Contains(kind.Check(d), p => p.ElementIds.Contains(question.Id));
        var mermaid = new FlowchartMermaid().Export(d);
        Assert.Contains($"{question.Id}{{\"Mot de passe correct ?\"}}", mermaid);
        Assert.Contains($"{question.Id} -->|\"oui\"| {ok.Id}", mermaid);
    }

    [Fact]
    public void Mind_map_grows_on_both_sides_without_overlaps()
    {
        var kind = new MindMapModel();
        var d = kind.Create("Projet");
        var root = d.Nodes[0];
        for (var i = 0; i < 4; i++)
        {
            var add = (AddElements)kind.AddChild(d, root);
            add.Apply(d);
            for (var j = 0; j < 3; j++)
                ((AddElements)kind.AddChild(d, add.Nodes[0])).Apply(d);
        }
        MindMapModel.Arrange(d).Apply(d);
        var topics = MindMapModel.ChildrenOf(d, root).ToList();
        Assert.Equal(4, topics.Count);
        Assert.Equal(2, topics.Count(t => t.Center.X > root.Center.X));
        foreach (var a in d.Nodes)
            foreach (var b in d.Nodes.Where(b => b != a))
                Assert.False(a.Bounds.Intersects(b.Bounds), $"{a.Text} overlaps {b.Text}");
        // Each idea is on its topic's side, and topics have their own colour.
        Assert.All(topics, t => Assert.All(MindMapModel.ChildrenOf(d, t), c => Assert.Equal(Math.Sign(t.Center.X - root.Center.X), Math.Sign(c.Center.X - t.Center.X))));
        Assert.Equal(4, topics.Select(t => MindMapModel.ColourOf(d, t)).Distinct().Count());
        var outline = new MindMapOutline().Export(d);
        Assert.StartsWith("# Projet", outline);
        Assert.Equal(4 * 3, outline.Split('\n').Count(l => l.StartsWith("  - ", StringComparison.Ordinal)));
    }
}

public class MoodBoardTests
{
    [Fact]
    public void Palette_finds_the_main_colours_largest_first()
    {
        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 300; i++)
            pixels.Add(((byte)(200 + i % 5), 30, 30));     // red, most
        for (var i = 0; i < 100; i++)
            pixels.Add((20, 40, (byte)(180 + i % 3)));     // blue
        var palette = Palette.Dominant(pixels, 2);
        Assert.Equal(2, palette.Count);
        Assert.StartsWith("#C", palette[0]);
        Assert.EndsWith("B5", palette[1][..1] + palette[1][5..], StringComparison.Ordinal);
        Assert.Single(Palette.Dominant([(1, 2, 3), (1, 2, 3)], 5));
        Assert.Equal("#FFFFFF", Palette.TextOn("#202020"));
    }

    [Fact]
    public void Strokes_drop_needless_points_and_keep_their_place()
    {
        var line = Enumerable.Range(0, 50).Select(i => new DPoint(100 + i, 200 + i * 0.5)).ToList();
        Assert.Equal(2, Strokes.Simplify(line).Count);
        var corner = line.Concat(Enumerable.Range(1, 30).Select(i => new DPoint(149, 224.5 - i))).ToList();
        Assert.Equal(3, Strokes.Simplify(corner).Count);

        var d = new MoodBoardModel().Create("Board");
        var stroke = Strokes.Make(d, corner, "#E5534B", 4)!;
        Assert.Equal(MoodBoardModel.Stroke, stroke.Type);
        Assert.Equal(100 - 3, stroke.X);
        Assert.All(stroke.Points!, p => Assert.True(p.X >= 0 && p.Y >= 0 && p.X <= stroke.Width && p.Y <= stroke.Height));
        Assert.Equal("#E5534B", stroke.Prop("line"));
        Assert.Null(Strokes.Make(d, [new DPoint(1, 1)], "#000000", 2));
    }

    [Fact]
    public async Task Pictures_are_stored_once_beside_the_board()
    {
        var folder = Path.Combine(Path.GetTempPath(), "devhub-moodboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var board = Path.Combine(folder, "Inspiration" + DiagramFile.Extension);
            byte[] data = [1, 2, 3, 4];
            var src = await Assets.StoreAsync(board, data, ".PNG");
            Assert.StartsWith("Inspiration.assets/", src);
            Assert.EndsWith(".png", src);
            Assert.Equal(src, await Assets.StoreAsync(board, data, "png"));
            Assert.Single(Directory.GetFiles(Assets.FolderFor(board)));
            Assert.True(File.Exists(Assets.Resolve(folder, src)));
            Assert.True(Assets.IsPicture("photo.JPG"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Swatches_export_as_css_variables()
    {
        var kind = new MoodBoardModel();
        var d = kind.Create("Board");
        var a = kind.NewNode(d, MoodBoardModel.Swatch, new DPoint(0, 0), "Bleu océan");
        a.Props["color"] = "#1E6FD9";
        d.Nodes.Add(a);
        var b = kind.NewNode(d, MoodBoardModel.Swatch, new DPoint(200, 0), "");
        b.Props["color"] = "#FFAA00";
        d.Nodes.Add(b);
        var css = new SwatchCss().Export(d);
        Assert.Contains("--bleu-ocean: #1e6fd9;", css);
        Assert.Contains("--color-2: #ffaa00;", css);
    }
}

public class SampleTests
{
    public static TheoryData<string> Kinds => new() { "usecase", "activity", "class", "sequence", "state", "flowchart", "bpmn", "mindmap" };

    private static IDiagramKindModel Model(string id) => id switch
    {
        "usecase" => new UseCaseModel(),
        "activity" => new ActivityModel(),
        "class" => new ClassModel(),
        "sequence" => new SequenceModel(),
        "state" => new StateModel(),
        "flowchart" => new FlowchartModel(),
        "bpmn" => new BpmnModel(),
        _ => new MindMapModel(),
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Samples_build_and_break_no_rule(string kind)
    {
        var model = Model(kind);
        Assert.NotEmpty(model.Samples);
        foreach (var sample in model.Samples)
        {
            var d = sample.Create();
            Assert.Equal(model.Id, d.Kind);
            Assert.NotEmpty(d.Nodes);
            Assert.Equal(d.Nodes.Count + d.Edges.Count, d.Nodes.Select(n => n.Id).Concat(d.Edges.Select(e => e.Id)).Distinct().Count());
            Assert.All(d.Edges, e => Assert.True(d.Has(e.From) && d.Has(e.To)));
            var problems = model.Check(d).Where(p => p.Severity != ProblemSeverity.Info).ToList();
            Assert.True(problems.Count == 0, $"{sample.Name}: {string.Join("; ", problems.Select(p => p.Message))}");
            // Round trip through the file format.
            Assert.Equal(DiagramFile.Write(d), DiagramFile.Write(DiagramFile.Read(DiagramFile.Write(d))));
        }
    }
}

public class MindMapSampleTests
{
    [Fact]
    public void Mind_map_sample_keeps_its_words_and_arranges_after_fitting()
    {
        var sample = new MindMapModel().Samples.Single();
        var d = sample.Create();
        Assert.Contains(d.Nodes, n => n.Text == "Students");
        Assert.DoesNotContain(d.Nodes, n => n.Text is "Topic" or "Idea");
        Assert.NotNull(sample.Arrange);
        sample.Arrange!(d).Apply(d);
        Assert.Equal(4, MindMapModel.ChildrenOf(d, d.Nodes.Single(n => n.Type == MindMapModel.Central)).Count());
    }
}
