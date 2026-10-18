using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Business.Core;

/// <summary>Business samples in the user's language.</summary>
public static class BusinessSamples
{
    public static IReadOnlyList<DiagramSample> Flowchart =>
    [
        new(L.T("Log in"), L.T("Checking a password with three tries: decisions, a loop, input and output."), Login),
    ];

    public static IReadOnlyList<DiagramSample> Bpmn =>
    [
        new(L.T("Order handling"), L.T("A customer and a shop: two pools, lanes, a gateway and message flows."), OrderHandling),
    ];

    public static IReadOnlyList<DiagramSample> MindMap =>
    [
        new(L.T("Project kick-off"), L.T("A project's users, features, technology and planning around its name."), KickOff)
        {
            Arrange = d => MindMapModel.Arrange(d),
        },
    ];

    private static Diagram Login()
    {
        var b = new SampleBuilder(new FlowchartModel(), L.T("Log in"));
        var start = b.Node(FlowchartModel.Terminator, 260, 50, L.T("Start"));
        var input = b.Node(FlowchartModel.InputOutput, 260, 140, L.T("Read login and password"));
        var correct = b.Node(FlowchartModel.Decision, 260, 250, L.T("Correct?"));
        var open = b.Node(FlowchartModel.Process, 260, 370, L.T("Open the session"));
        var end = b.Node(FlowchartModel.Terminator, 260, 470, L.T("End"));
        var tries = b.Node(FlowchartModel.Decision, 520, 250, L.T("Fewer than 3 tries?"));
        tries.Width = 150;
        var locked = b.Node(FlowchartModel.Process, 520, 370, L.T("Lock the account"));
        b.Edge(FlowchartModel.Flow, start, input);
        b.Edge(FlowchartModel.Flow, input, correct);
        b.Edge(FlowchartModel.Flow, correct, open, L.T("yes"));
        b.Edge(FlowchartModel.Flow, correct, tries, L.T("no"));
        b.Edge(FlowchartModel.Flow, tries, input, L.T("yes"));
        b.Edge(FlowchartModel.Flow, tries, locked, L.T("no"));
        b.Edge(FlowchartModel.Flow, open, end);
        b.Edge(FlowchartModel.Flow, locked, end);
        return b.Diagram;
    }

    private static Diagram OrderHandling()
    {
        var b = new SampleBuilder(new BpmnModel(), L.T("Order handling"));
        var customer = b.Box(BpmnModel.Pool, 40, 40, 940, 170, L.T("Customer"));
        var cStart = b.Node(BpmnModel.StartEvent, 120, 125, "", customer);
        var place = b.Node(BpmnModel.Task, 260, 125, L.T("Place the order"), customer, ("taskType", "send"));
        var receive = b.Node(BpmnModel.Task, 760, 125, L.T("Receive the parcel"), customer, ("taskType", "receive"));
        var cEnd = b.Node(BpmnModel.EndEvent, 900, 125, "", customer);

        var shop = b.Box(BpmnModel.Pool, 40, 250, 940, 340, L.T("Shop"));
        var sales = b.Box(BpmnModel.Lane, 70, 250, 910, 170, L.T("Sales"), shop);
        var warehouse = b.Box(BpmnModel.Lane, 70, 420, 910, 170, L.T("Warehouse"), shop);
        var sStart = b.Node(BpmnModel.StartEvent, 130, 335, L.T("Order received"), sales, ("trigger", "message"));
        var check = b.Node(BpmnModel.Task, 260, 335, L.T("Check the stock"), sales, ("taskType", "user"));
        var stock = b.Node(BpmnModel.Gateway, 400, 335, L.T("In stock?"), sales);
        var apologise = b.Node(BpmnModel.Task, 560, 335, L.T("Tell the customer"), sales, ("taskType", "send"));
        var sorry = b.Node(BpmnModel.EndEvent, 700, 335, "", sales);
        var pack = b.Node(BpmnModel.Task, 560, 505, L.T("Pack the order"), warehouse, ("taskType", "manual"));
        var ship = b.Node(BpmnModel.Task, 760, 505, L.T("Ship"), warehouse, ("taskType", "send"));
        var shipped = b.Node(BpmnModel.EndEvent, 900, 505, "", warehouse);

        b.Edge(BpmnModel.SequenceFlow, cStart, place);
        b.Edge(BpmnModel.SequenceFlow, place, receive);
        b.Edge(BpmnModel.SequenceFlow, receive, cEnd);
        b.Edge(BpmnModel.SequenceFlow, sStart, check);
        b.Edge(BpmnModel.SequenceFlow, check, stock);
        b.Edge(BpmnModel.SequenceFlow, stock, pack, L.T("yes"));
        b.Edge(BpmnModel.SequenceFlow, stock, apologise, L.T("no"));
        b.Edge(BpmnModel.SequenceFlow, apologise, sorry);
        b.Edge(BpmnModel.SequenceFlow, pack, ship);
        b.Edge(BpmnModel.SequenceFlow, ship, shipped);
        b.Edge(BpmnModel.MessageFlow, place, sStart, L.T("order"));
        b.Edge(BpmnModel.MessageFlow, ship, receive, L.T("parcel"));
        b.Edge(BpmnModel.MessageFlow, apologise, receive, L.T("apology"));
        return b.Diagram;
    }

    private static Diagram KickOff()
    {
        var model = new MindMapModel();
        var b = new SampleBuilder(model, L.T("Project kick-off"));
        var d = b.Diagram;
        var root = b.Node(MindMapModel.Central, 500, 320, L.T("My app"));
        void Add(DiagramNode parent, string text, params string[] children)
        {
            var add = (AddElements)model.AddChild(d, parent);
            add.Apply(d);
            var node = d.Node(add.Nodes[0].Id)!;     // the diagram holds its own copy
            node.Text = text;
            foreach (var child in children)
            {
                var sub = (AddElements)model.AddChild(d, node);
                sub.Apply(d);
                d.Node(sub.Nodes[0].Id)!.Text = child;
            }
        }
        Add(root, L.T("Users"), L.T("Students"), L.T("Teachers"), L.T("Admins"));
        Add(root, L.T("Features"), L.T("Sign in"), L.T("Courses"), L.T("Marks"));
        Add(root, L.T("Technology"), L.T("Web app"), L.T("Database"));
        Add(root, L.T("Planning"), L.T("Mock-ups"), L.T("Sprints"), L.T("Tests"));
        MindMapModel.Arrange(d).Apply(d);
        return d;
    }
}
