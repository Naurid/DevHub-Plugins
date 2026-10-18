using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Business.Core;

/// <summary>"Everything" samples: every flowchart shape, every BPMN event trigger, task type and gateway, a styled mind map.</summary>
public static class BusinessShowcase
{
    public static string Name => L.T("Showcase: everything");

    private static readonly (string, string)[] Accent = [("fill", "#DCEBFF"), ("line", "#3574F0"), ("textColor", "#1F3F7A"), ("bold", "true")];
    private static readonly (string, string)[] Warm = [("fill", "#FFF0D9"), ("line", "#D98C1F"), ("fontSize", "14")];

    public static IReadOnlyList<DiagramSample> Flowchart =>
        [new(Name, L.T("Every flowchart shape, connectors, labelled branches and styled lines."), FlowchartShowcase)];

    public static IReadOnlyList<DiagramSample> Bpmn =>
        [new(Name, L.T("Every event trigger, task type and gateway, a sub-process, data, annotations, both kinds of flow."), BpmnShowcase)];

    public static IReadOnlyList<DiagramSample> MindMap =>
        [new(Name, L.T("Topics and sub-topics on both sides, own colours, a note."), MindMapShowcase) { Arrange = d => MindMapModel.Arrange(d) }];

    private static Diagram FlowchartShowcase()
    {
        var b = new SampleBuilder(new FlowchartModel(), L.T("Monthly report"));
        var start = b.Node(FlowchartModel.Terminator, 300, 50, L.T("Start"), null, Accent);
        var read = b.Node(FlowchartModel.InputOutput, 300, 140, L.T("Read the sales"));
        var db = b.Node(FlowchartModel.Database, 80, 140, L.T("Sales DB"));
        var manual = b.Node(FlowchartModel.ManualInput, 540, 140, L.T("Type the month"));
        var any = b.Node(FlowchartModel.Decision, 300, 250, L.T("Any sales?"));
        var sum = b.Node(FlowchartModel.Predefined, 300, 370, L.T("Compute totals"));
        var report = b.Node(FlowchartModel.Document, 300, 480, L.T("Report"), null, Warm);
        var a1 = b.Node(FlowchartModel.Connector, 560, 250, "A");
        var a2 = b.Node(FlowchartModel.Connector, 560, 480, "A");
        var empty = b.Node(FlowchartModel.Process, 560, 560, L.T("Write that there were no sales"));
        var end = b.Node(FlowchartModel.Terminator, 300, 620, L.T("End"), null, Accent);
        var note = b.Node(FlowchartModel.Note, 80, 370, L.T("Totals per product and per region"));

        b.Edge(FlowchartModel.Flow, start, read);
        b.Edge(FlowchartModel.Flow, db, read, "", ("dash", "dashed"), ("line", "#8E5BD6"));
        b.Edge(FlowchartModel.Flow, manual, read);
        b.Edge(FlowchartModel.Flow, read, any);
        b.Edge(FlowchartModel.Flow, any, sum, L.T("yes"), ("line", "#2E9E5B"), ("width", "2"));
        b.Edge(FlowchartModel.Flow, any, a1, L.T("no"), ("line", "#E5534B"), ("width", "2"));
        b.Edge(FlowchartModel.Flow, a2, empty);
        b.Edge(FlowchartModel.Flow, sum, report);
        b.Edge(FlowchartModel.Flow, report, end, "", ("startArrow", "circle"), ("endArrow", "filled"));
        b.Edge(FlowchartModel.Flow, empty, end);
        b.Edge(FlowchartModel.NoteLink, note, sum);
        return b.Diagram;
    }

    private static Diagram BpmnShowcase()
    {
        var b = new SampleBuilder(new BpmnModel(), L.T("Claim handling"));
        var customer = b.Box(BpmnModel.Pool, 40, 40, 1240, 170, L.T("Customer"), null, ("fill", "#FAFAFB"));
        var cStart = b.Node(BpmnModel.StartEvent, 120, 125, L.T("Damage"), customer, ("trigger", "none"));
        var file = b.Node(BpmnModel.Task, 260, 125, L.T("File a claim"), customer, ("taskType", "send"));
        var wait = b.Node(BpmnModel.IntermediateEvent, 420, 125, L.T("Answer"), customer, ("trigger", "message"));
        var cEnd = b.Node(BpmnModel.EndEvent, 560, 125, "", customer);

        var insurer = b.Box(BpmnModel.Pool, 40, 250, 1240, 520, L.T("Insurer"));
        var desk = b.Box(BpmnModel.Lane, 70, 250, 1210, 260, L.T("Claims desk"), insurer);
        var expert = b.Box(BpmnModel.Lane, 70, 510, 1210, 260, L.T("Expert"), insurer, ("fill", "#F3F8FF"));
        var received = b.Node(BpmnModel.StartEvent, 130, 330, L.T("Claim in"), desk, ("trigger", "message"));
        var register = b.Node(BpmnModel.Task, 260, 330, L.T("Register"), desk, ("taskType", "user"));
        var rules = b.Node(BpmnModel.Task, 400, 330, L.T("Check cover"), desk, ("taskType", "businessRule"));
        var covered = b.Node(BpmnModel.Gateway, 530, 330, L.T("Covered?"), desk, ("gatewayType", "exclusive"));
        var refuse = b.Node(BpmnModel.Task, 660, 430, L.T("Refuse"), desk, ("taskType", "send"));
        var refused = b.Node(BpmnModel.EndEvent, 800, 430, L.T("Refused"), desk, ("trigger", "message"));
        var split = b.Node(BpmnModel.Gateway, 660, 330, "", desk, ("gatewayType", "parallel"));
        var pay = b.Node(BpmnModel.Task, 800, 300, L.T("Pay out"), desk, ("taskType", "service"));
        var letter = b.Node(BpmnModel.Task, 800, 390, L.T("Write a letter"), desk, ("taskType", "script"));
        var merge = b.Node(BpmnModel.Gateway, 940, 330, "", desk, ("gatewayType", "parallel"));
        var notify = b.Node(BpmnModel.Task, 1060, 330, L.T("Notify"), desk, [.. Warm, ("taskType", "send")]);
        var done = b.Node(BpmnModel.EndEvent, 1190, 330, L.T("Closed"), desk, ("trigger", "terminate"));
        var visit = b.Node(BpmnModel.Task, 260, 620, L.T("Visit"), expert, ("taskType", "manual"));
        var inspect = b.Node(BpmnModel.SubProcess, 400, 620, L.T("Inspect the damage"), expert, ("loop", "true"));
        var timer = b.Node(BpmnModel.IntermediateEvent, 560, 620, L.T("2 days"), expert, ("trigger", "timer"));
        var choice = b.Node(BpmnModel.Gateway, 680, 620, "", expert, ("gatewayType", "eventBased"));
        var report = b.Node(BpmnModel.Task, 820, 590, L.T("Report"), expert, ("taskType", "receive"));
        var escalate = b.Node(BpmnModel.IntermediateEvent, 820, 700, L.T("Escalate"), expert, ("trigger", "signal"), ("throwing", "true"));
        var some = b.Node(BpmnModel.Gateway, 960, 620, "", expert, ("gatewayType", "inclusive"));
        var photos = b.Node(BpmnModel.Task, 1080, 560, L.T("Add photos"), expert);
        var complete = b.Node(BpmnModel.IntermediateEvent, 1190, 560, L.T("Complete"), expert, ("trigger", "conditional"));
        var expertEnd = b.Node(BpmnModel.EndEvent, 1190, 640, "", expert);
        var fraud = b.Node(BpmnModel.EndEvent, 1190, 720, L.T("Fraud"), expert, ("trigger", "error"));
        var claim = b.Node(BpmnModel.DataObject, 260, 440, L.T("Claim"), desk);
        var store = b.Node(BpmnModel.DataStore, 400, 445, L.T("Policies"), desk);
        var remark = b.Node(BpmnModel.Annotation, 160, 720, L.T("The expert has 2 days"), expert);

        b.Edge(BpmnModel.SequenceFlow, cStart, file);
        b.Edge(BpmnModel.SequenceFlow, file, wait);
        b.Edge(BpmnModel.SequenceFlow, wait, cEnd);
        b.Edge(BpmnModel.MessageFlow, file, received, L.T("claim"));
        b.Edge(BpmnModel.MessageFlow, notify, wait, L.T("decision"), ("line", "#3574F0"));
        b.Edge(BpmnModel.MessageFlow, refuse, wait, L.T("refusal"), ("line", "#E5534B"));

        b.Edge(BpmnModel.SequenceFlow, received, register);
        b.Edge(BpmnModel.SequenceFlow, register, rules);
        b.Edge(BpmnModel.SequenceFlow, rules, covered);
        b.Edge(BpmnModel.SequenceFlow, covered, split, L.T("yes"), ("line", "#2E9E5B"));
        b.Edge(BpmnModel.SequenceFlow, covered, refuse, L.T("no"), ("default", "true"));
        b.Edge(BpmnModel.SequenceFlow, refuse, refused);
        b.Edge(BpmnModel.SequenceFlow, split, pay);
        b.Edge(BpmnModel.SequenceFlow, split, letter);
        b.Edge(BpmnModel.SequenceFlow, pay, merge);
        b.Edge(BpmnModel.SequenceFlow, letter, merge);
        b.Edge(BpmnModel.SequenceFlow, merge, notify, "", ("width", "2"));
        b.Edge(BpmnModel.SequenceFlow, notify, done);
        b.Edge(BpmnModel.SequenceFlow, register, visit, "", ("dash", "dashed"));
        b.Edge(BpmnModel.SequenceFlow, visit, inspect);
        b.Edge(BpmnModel.SequenceFlow, inspect, timer);
        b.Edge(BpmnModel.SequenceFlow, timer, choice);
        b.Edge(BpmnModel.SequenceFlow, choice, report);
        b.Edge(BpmnModel.SequenceFlow, choice, escalate);
        b.Edge(BpmnModel.SequenceFlow, report, some);
        b.Edge(BpmnModel.SequenceFlow, escalate, fraud);
        b.Edge(BpmnModel.SequenceFlow, some, photos, L.T("damage visible"));
        b.Edge(BpmnModel.SequenceFlow, some, expertEnd, L.T("always"));
        b.Edge(BpmnModel.SequenceFlow, photos, complete);
        b.Edge(BpmnModel.SequenceFlow, complete, expertEnd);
        b.Edge(BpmnModel.Association, claim, register, "", ("dash", "dotted"));
        b.Edge(BpmnModel.Association, store, rules, "", ("endArrow", "open"));
        b.Edge(BpmnModel.Association, remark, timer);
        return b.Diagram;
    }

    private static Diagram MindMapShowcase()
    {
        var model = new MindMapModel();
        var b = new SampleBuilder(model, L.T("Holiday plan"));
        var d = b.Diagram;
        var root = b.Node(MindMapModel.Central, 500, 320, L.T("Holiday"), null, ("fill", "#2E9E5B"));
        DiagramNode Add(DiagramNode parent, string text, params (string, string)[] props)
        {
            var add = (AddElements)model.AddChild(d, parent);
            add.Apply(d);
            var node = d.Node(add.Nodes[0].Id)!;     // the diagram holds its own copy
            node.Text = text;
            foreach (var (k, v) in props)
                node.Props[k] = v;
            return node;
        }
        var where = Add(root, L.T("Where"));
        Add(where, L.T("Seaside"));
        Add(where, L.T("Mountains"), ("bold", "true"));
        var budget = Add(root, L.T("Budget"), ("line", "#E5534B"), ("fill", "#FFF0EF"));
        Add(budget, L.T("Travel"));
        Add(budget, L.T("Hotel"));
        Add(budget, L.T("Food"), ("textColor", "#E5534B"));
        var packing = Add(root, L.T("Packing"));
        Add(packing, L.T("Passport"), ("fill", "#DCEBFF"));
        var swim = Add(packing, L.T("Swimwear"));
        Add(swim, L.T("Sun cream"));
        var who = Add(root, L.T("Who"), ("fontSize", "16"));
        Add(who, L.T("Family"));
        Add(who, L.T("Friends"));
        foreach (var e in d.Edges.Where(e => e.To == budget.Id))
            e.Props["width"] = "4";
        foreach (var e in d.Edges.Where(e => e.To == who.Id))
            (e.Props["dash"], e.Props["endArrow"]) = ("dashed", "filled");
        b.Node(MindMapModel.Note, 500, 120, L.T("Decide before the end of March"), null, ("fill", "#FFF8C5"));
        return d;
    }
}
