using DevHub.Diagrams.Core;
using Xunit;

namespace DevHub.Diagrams.Core.Tests;

public class DiagramCoreTests
{
    private static DiagramNode Box(string id, double x, double y, double w = 100, double h = 50, string? parent = null) =>
        new() { Id = id, Type = "box", X = x, Y = y, Width = w, Height = h, Text = id, ParentId = parent };

    private static DiagramEdge Line(string id, string from, string to) => new() { Id = id, Type = "line", From = from, To = to };

    private static Diagram Sample() => new()
    {
        Kind = "test",
        Title = "Sample",
        Nodes = [Box("sys", 0, 0, 400, 300), Box("a", 20, 40, parent: "sys"), Box("b", 200, 40, parent: "sys"), Box("c", 500, 40)],
        Edges = [Line("ab", "a", "b"), Line("bc", "b", "c")],
    };

    [Fact]
    public void A_file_round_trips_and_keeps_fields_it_does_not_know()
    {
        const string json = """
            { "schemaVersion": 1, "kind": "usecase", "title": "Hôpital", "future": { "x": 1 },
              "nodes": [ { "id": "n1", "type": "actor", "x": 1, "y": 2, "width": 3, "height": 4, "text": "Secrétaire", "later": true } ],
              "edges": [] }
            """;
        var diagram = DiagramFile.Read(json);
        var again = DiagramFile.Read(DiagramFile.Write(diagram));

        Assert.Equal("Hôpital", again.Title);
        Assert.Equal("Secrétaire", again.Nodes[0].Text);
        Assert.Contains("\"future\"", DiagramFile.Write(again));
        Assert.Contains("\"later\": true", DiagramFile.Write(again));
        Assert.Contains("Hôpital", DiagramFile.Write(again));   // not escaped
        Assert.Equal(DiagramFile.Write(diagram), DiagramFile.Write(again));
    }

    [Fact]
    public void A_dragged_label_keeps_its_place_along_the_line_and_its_offset()
    {
        IReadOnlyList<DPoint> path = [new(0, 0), new(100, 0), new(100, 100)];   // 200 long: a corner at the middle
        var (at, offset) = Routing.PlaceLabel(path, new DPoint(130, 75));

        Assert.Equal(0.875, at, 3);                                // nearest to (100, 75): 175 of 200
        Assert.Equal(new DPoint(30, 0), offset);
        var e = Line("ab", "a", "b");
        new MoveLabel("ab", at, offset).Apply(new Diagram { Kind = "test", Edges = [e] });
        Assert.Equal(new DPoint(130, 75), Routing.LabelCenter(path, e));
        // Moved shapes: the label stays at the same share of the line, at the same offset.
        Assert.Equal(new DPoint(30, 175), Routing.LabelCenter([new(0, 0), new(0, 200)], e));
    }

    [Fact]
    public void Moving_a_label_back_onto_its_line_drops_the_offset_and_undo_restores_it()
    {
        var d = Sample();
        var before = DiagramFile.Write(d);
        var undo = new MoveLabel("ab", 0.3, new DPoint(12, -8)).Apply(d);
        Assert.Equal("12", d.Edge("ab")!.Prop(Routing.LabelDx));
        Assert.Equal("-8", d.Edge("ab")!.Prop(Routing.LabelDy));

        new MoveLabel("ab", 0.3, new DPoint(0.2, 0)).Apply(d);
        Assert.Empty(d.Edge("ab")!.Props);

        undo.Apply(d);
        Assert.Equal(before, DiagramFile.Write(d));
    }

    [Fact]
    public void A_file_without_a_kind_is_refused()
    {
        Assert.Throws<DiagramFileException>(() => DiagramFile.Read("""{ "nodes": [] }"""));
        Assert.Throws<DiagramFileException>(() => DiagramFile.Read("not json"));
    }

    [Fact]
    public void Deleting_a_container_takes_its_contents_and_lines_and_undo_puts_everything_back_in_order()
    {
        var d = Sample();
        var before = DiagramFile.Write(d);

        var undo = new RemoveElements(["sys"]).Apply(d);
        Assert.Equal(["c"], d.Nodes.Select(n => n.Id));
        Assert.Empty(d.Edges);

        undo.Apply(d);
        Assert.Equal(before, DiagramFile.Write(d));
    }

    [Fact]
    public void Moving_a_container_moves_its_contents_and_the_bends_between_them()
    {
        var d = Sample();
        d.Edge("ab")!.Waypoints = [new DPoint(150, 100)];

        var undo = new MoveNodes(["sys"], 10, 5).Apply(d);
        Assert.Equal(30, d.Node("a")!.X);
        Assert.Equal(new DPoint(160, 105), d.Edge("ab")!.Waypoints[0]);
        Assert.Equal(500, d.Node("c")!.X);   // outside: stays

        undo.Apply(d);
        Assert.Equal(20, d.Node("a")!.X);
    }

    [Fact]
    public void Undo_stack_knows_when_the_diagram_is_back_to_its_saved_state()
    {
        var stack = new UndoStack(Sample());
        stack.MarkSaved();
        Assert.False(stack.IsDirty);

        stack.Do(new SetText("a", "Client"));
        Assert.True(stack.IsDirty);
        Assert.Equal("Edit Text", stack.UndoName);

        stack.Undo();
        Assert.False(stack.IsDirty);
        Assert.Equal("a", stack.Diagram.Node("a")!.Text);

        stack.Redo();
        Assert.Equal("Client", stack.Diagram.Node("a")!.Text);

        // Saved, undone, then a new change: the saved state can't come back by undoing.
        stack.MarkSaved();
        stack.Undo();
        stack.Do(new SetText("b", "Vendeur"));
        stack.Undo();
        Assert.True(stack.IsDirty);
    }

    [Fact]
    public void A_batch_is_one_step_and_all_or_nothing()
    {
        var d = Sample();
        var before = DiagramFile.Write(d);
        var failing = new Batch("Test", [new SetText("a", "x"), new SetText("missing", "y")]);

        Assert.Throws<DiagramRuleException>(() => failing.Apply(d));
        Assert.Equal(before, DiagramFile.Write(d));

        var undo = new Batch("Test", [new SetText("a", "x"), new SetProps("b", new Dictionary<string, string?> { ["k"] = "v" })]).Apply(d);
        undo.Apply(d);
        Assert.Equal(before, DiagramFile.Write(d));
    }

    [Fact]
    public void A_container_cannot_go_inside_itself()
    {
        var d = Sample();
        Assert.Throws<DiagramRuleException>(() => new SetParent(["sys"], "a").Apply(d));
    }

    [Fact]
    public void Reorder_and_its_undo()
    {
        var d = Sample();
        var undo = new Reorder(["sys"], ToFront: true).Apply(d);
        Assert.Equal("sys", d.Nodes[^1].Id);
        undo.Apply(d);
        Assert.Equal("sys", d.Nodes[0].Id);
    }

    [Fact]
    public void Lines_meet_the_outline_of_ellipses_and_diamonds()
    {
        var r = new DRect(0, 0, 200, 100);
        Assert.Equal(new DPoint(200, 50), Shapes.Boundary(NodeShape.Ellipse, r, new DPoint(500, 50)));
        Assert.Equal(new DPoint(100, 0), Shapes.Boundary(NodeShape.Diamond, r, new DPoint(100, -300)));
        var diagonal = Shapes.Boundary(NodeShape.Ellipse, r, new DPoint(300, 150));
        Assert.Equal(1, Math.Pow((diagonal.X - 100) / 100, 2) + Math.Pow((diagonal.Y - 50) / 50, 2), 6);
        Assert.False(Shapes.Hit(NodeShape.Ellipse, r, new DPoint(2, 2), slack: 0));
        Assert.True(Shapes.Hit(NodeShape.Rectangle, r, new DPoint(2, 2), slack: 0));
    }

    [Fact]
    public void Orthogonal_routes_have_only_straight_segments_and_go_around_shapes()
    {
        var from = new DRect(0, 0, 80, 40);
        var to = new DRect(400, 0, 80, 40);
        var wall = new DRect(180, -20, 60, 80);

        var route = Routing.Orthogonal(from, to, [wall]);

        for (var i = 1; i < route.Count; i++)
            Assert.True(Math.Abs(route[i].X - route[i - 1].X) < 0.01 || Math.Abs(route[i].Y - route[i - 1].Y) < 0.01);
        for (var i = 1; i < route.Count; i++)
            Assert.False(Shapes.SegmentCrosses(route[i - 1], route[i], wall));
    }

    [Fact]
    public void Straight_lines_start_and_end_on_the_shapes()
    {
        var d = Sample();
        var path = Routing.Path(d, d.Edge("ab")!, _ => NodeShape.Rectangle);
        Assert.Equal(new DPoint(120, 65), path[0]);
        Assert.Equal(new DPoint(200, 65), path[^1]);
        Assert.Equal(new DPoint(160, 65), Routing.LabelPoint(path, null));
    }

    [Fact]
    public void Snapping_and_smart_guides()
    {
        Assert.Equal(40, Layout.Snap(43, 10));
        Assert.Equal(43, Layout.Snap(43, 0));
        var (dx, dy, gx, _) = Layout.Guides(new DRect(103, 300, 50, 50), [new DRect(100, 0, 80, 40)]);
        Assert.Equal(-3, dx);
        Assert.Equal(0, dy);
        Assert.Equal(100, gx);
    }

    [Fact]
    public void Align_and_distribute()
    {
        var d = new Diagram { Kind = "t", Nodes = [Box("a", 0, 0), Box("b", 130, 30), Box("c", 400, 10)] };
        Layout.Align(d, ["a", "b", "c"], Alignment.Top).Apply(d);
        Assert.All(d.Nodes, n => Assert.Equal(0, n.Y));

        Layout.Distribute(d, ["a", "b", "c"], horizontally: true).Apply(d);
        Assert.Equal(200, d.Node("b")!.X);   // gaps of 100 between 0..100, 200..300, 400..500
    }

    [Fact]
    public void Layered_layout_puts_each_shape_below_what_leads_to_it_even_with_a_loop()
    {
        var d = new Diagram
        {
            Kind = "activity",
            Nodes = [Box("start", 300, 300, 20, 20), Box("act", 10, 10), Box("check", 500, 0), Box("end", 0, 500, 20, 20)],
            Edges = [Line("1", "start", "act"), Line("2", "act", "check"), Line("3", "check", "act"), Line("4", "check", "end")],
        };
        AutoLayout.Layered(d, d.Nodes.Select(n => n.Id).ToList()).Apply(d);

        double Row(string id) => d.Node(id)!.Center.Y;
        Assert.True(Row("start") < Row("act"));
        Assert.True(Row("act") < Row("check"));
        Assert.True(Row("check") < Row("end"));
    }

    [Fact]
    public void Project_links_are_relative_to_the_diagram_and_survive_a_move()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "shop");
        var file = Path.Combine(project, "docs", "diagrams", "Login" + DiagramFile.Extension);
        var d = new Diagram { Kind = "usecase", Project = ProjectLinks.Relative(file, project) };
        Assert.Equal("../..", d.Project);
        Assert.True(ProjectLinks.BelongsTo(file, d, project));

        var moved = Path.Combine(root, "renamed");
        Assert.True(ProjectLinks.BelongsTo(Path.Combine(moved, "docs", "diagrams", "Login" + DiagramFile.Extension), d, moved));
        Assert.Null(ProjectLinks.Resolve(file, new Diagram { Kind = "x" }));
    }

    [Fact]
    public async Task Files_under_a_project_skip_build_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "dg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var d = new Diagram { Kind = "usecase" };
            await DiagramFile.SaveAsync(d, Path.Combine(root, "docs", "diagrams", "A" + DiagramFile.Extension));
            await DiagramFile.SaveAsync(d, Path.Combine(root, "bin", "B" + DiagramFile.Extension));
            var found = ProjectLinks.FilesUnder(root);
            Assert.Single(found);
            Assert.Equal("A", DiagramFile.NameOf(found[0]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class DiagramsSettingsTests
{
    [Fact]
    public void Exported_images_go_where_the_settings_say()
    {
        var diagram = Path.Combine("C:", "Shop", "docs", "diagrams", "Login" + DiagramFile.Extension);
        Assert.Null(new DiagramsSettings().TargetFor(diagram, "Login", "png"));
        Assert.Equal(Path.Combine("C:", "Shop", "docs", "diagrams", "Login.png"),
            new DiagramsSettings { ExportPlace = ExportPlace.BesideDiagram }.TargetFor(diagram, "Login", "png"));
        Assert.Equal(Path.Combine("C:", "Shop", "docs", "diagrams", "images", "Login.svg"),
            new DiagramsSettings { ExportPlace = ExportPlace.BesideDiagram, BesideSubfolder = "images" }.TargetFor(diagram, "Login", "svg"));
        // Characters a file name can't hold are dropped.
        Assert.Equal(Path.Combine("D:", "Pictures", "Login.png"),
            new DiagramsSettings { ExportPlace = ExportPlace.Folder, ExportFolder = Path.Combine("D:", "Pictures") }.TargetFor(diagram, "Log:in", "png"));
        // A folder setting without a folder, or an unsaved diagram beside nothing, falls back to asking.
        Assert.Null(new DiagramsSettings { ExportPlace = ExportPlace.Folder }.TargetFor(diagram, "Login", "png"));
        Assert.Null(new DiagramsSettings { ExportPlace = ExportPlace.BesideDiagram }.TargetFor(null, "Login", "png"));
    }
}

public class InteractionTests
{
    private static NodeShape Rect(DiagramNode n) => NodeShape.Rectangle;

    [Fact]
    public void A_slider_drag_is_one_undo_step()
    {
        var d = new Diagram { Kind = "test" };
        d.Nodes.Add(new DiagramNode { Id = "a", Type = "box", Width = 50, Height = 50 });
        var stack = new UndoStack(d);
        foreach (var width in new[] { "1.5", "2", "3", "4" })
            stack.Do(new SetProps("a", new Dictionary<string, string?> { ["width"] = width }), mergeKey: "a|width");
        Assert.Equal("4", d.Node("a")!.Prop("width"));
        stack.Undo();
        Assert.Equal("", d.Node("a")!.Prop("width"));
        Assert.False(stack.CanUndo);
        stack.Redo();
        Assert.Equal("4", d.Node("a")!.Prop("width"));

        // Another field, or no key, is a new step.
        stack.Do(new SetProps("a", new Dictionary<string, string?> { ["fontSize"] = "20" }), mergeKey: "a|fontSize");
        stack.Do(new SetProps("a", new Dictionary<string, string?> { ["fill"] = "#FF0000" }));
        stack.Undo();
        stack.Undo();
        Assert.Equal(("4", ""), (d.Node("a")!.Prop("width"), d.Node("a")!.Prop("fontSize")));
    }

    [Fact]
    public void Clicks_inside_a_container_pick_shapes_then_lines_then_the_innermost_container()
    {
        var d = new Diagram { Kind = "test" };
        d.Nodes.Add(new DiagramNode { Id = "pkg", Type = "package", X = 0, Y = 0, Width = 400, Height = 300 });
        d.Nodes.Add(new DiagramNode { Id = "sub", Type = "package", X = 200, Y = 150, Width = 180, Height = 130, ParentId = "pkg" });
        d.Nodes.Add(new DiagramNode { Id = "a", Type = "class", X = 20, Y = 20, Width = 60, Height = 40, ParentId = "pkg" });
        d.Nodes.Add(new DiagramNode { Id = "b", Type = "class", X = 220, Y = 20, Width = 60, Height = 40, ParentId = "pkg" });
        // The package was added last: it's on top in the list, as after "Bring to Front".
        var pkg = d.Nodes[0];
        d.Nodes.RemoveAt(0);
        d.Nodes.Add(pkg);
        d.Edges.Add(new DiagramEdge { Id = "ab", Type = "line", From = "a", To = "b" });
        bool IsContainer(DiagramNode n) => n.Type == "package";
        IReadOnlyList<DPoint> PathOf(DiagramEdge e) => [new(80, 40), new(220, 40)];

        Assert.Equal("ab", Layout.HitAt(d, new DPoint(150, 41), Rect, IsContainer, PathOf, 6).Edge?.Id);
        Assert.Equal("a", Layout.HitAt(d, new DPoint(30, 30), Rect, IsContainer, PathOf, 6).Node?.Id);
        Assert.Equal("sub", Layout.HitAt(d, new DPoint(300, 250), Rect, IsContainer, PathOf, 6).Node?.Id);
        Assert.Equal("pkg", Layout.HitAt(d, new DPoint(100, 200), Rect, IsContainer, PathOf, 6).Node?.Id);
        Assert.Equal((null, null), (Layout.HitAt(d, new DPoint(600, 600), Rect, IsContainer, PathOf, 6).Node, Layout.HitAt(d, new DPoint(600, 600), Rect, IsContainer, PathOf, 6).Edge));
    }
}
