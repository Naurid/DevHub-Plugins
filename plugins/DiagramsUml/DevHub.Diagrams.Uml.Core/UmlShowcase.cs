using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Uml.Core;

/// <summary>
/// "Everything" samples: one per UML kind using every shape and line it has, and the formatting every shape and line
/// can take (fill, line and text colours, text size, bold; line colour, width, dash, arrowheads, routing), so a student
/// sees what the editor can draw.
/// </summary>
public static class UmlShowcase
{
    public static string Name => L.T("Showcase: everything");

    // Formatting shared by the showcases: a highlighted shape and a highlighted line.
    internal static (string, string)[] Accent => [("fill", "#DCEBFF"), ("line", "#3574F0"), ("textColor", "#1F3F7A"), ("bold", "true")];
    internal static (string, string)[] Warm => [("fill", "#FFF0D9"), ("line", "#D98C1F"), ("fontSize", "14")];
    internal static (string, string)[] Red => [("line", "#E5534B"), ("width", "2")];
    internal static (string, string)[] BothArrows => [("startArrow", "open"), ("endArrow", "open"), ("line", "#2E9E5B"), ("width", "1.6")];

    private static (string, string)[] With(this (string, string)[] a, params (string, string)[] b) => [.. a, .. b];

    public static IReadOnlyList<DiagramSample> UseCase =>
        [new(Name, L.T("Every use case shape and line, stereotype actors, extension points and formatting."), UseCaseShowcase)];

    public static IReadOnlyList<DiagramSample> Activity =>
        [new(Name, L.T("Swimlanes, an activity, decisions with guards, fork and join, objects, signals, both ends."), ActivityShowcase)];

    public static IReadOnlyList<DiagramSample> Class =>
        [new(Name, L.T("A package of classes, an interface, an enumeration, a datatype and every relation."), ClassShowcase)];

    public static IReadOnlyList<DiagramSample> Object =>
        [new(Name, L.T("Objects with their values and the links between them."), ObjectShowcase)];

    public static IReadOnlyList<DiagramSample> Sequence =>
        [new(Name, L.T("An actor, activations, every message, a created and a destroyed object, loop, alt and opt."), SequenceShowcase)];

    public static IReadOnlyList<DiagramSample> State =>
        [new(Name, L.T("A composite state, choice, fork and join, history, activities and guarded transitions."), StateShowcase)];

    public static IReadOnlyList<DiagramSample> Structure(string kind) => kind switch
    {
        StructureModel.ComponentKind => [new(Name, L.T("Components with ports, offered interfaces, an artifact, a database, a package."), ComponentShowcase)],
        StructureModel.DeploymentKind => [new(Name, L.T("Servers, a device, an execution environment, deployed artifacts, a database."), DeploymentShowcase)],
        _ => [new(Name, L.T("Nested packages, dependencies and imports."), PackageShowcase)],
    };

    private static Diagram UseCaseShowcase()
    {
        var b = new SampleBuilder(new UseCaseModel(), L.T("Online shop"));
        var system = b.Box(UseCaseModel.Boundary, 280, 40, 460, 660, L.T("Online shop"), null, ("fill", "#FAFAFB"), ("bold", "true"), ("fontSize", "15"));
        var customer = b.Node(UseCaseModel.Actor, 130, 180, L.T("Customer"));
        var guest = b.Node(UseCaseModel.Actor, 130, 420, L.T("Guest"), null, ("line", "#8E5BD6"), ("textColor", "#8E5BD6"));
        var admin = b.Node(UseCaseModel.Actor, 880, 540, L.T("Administrator"), null, Warm);
        var bank = b.Box(UseCaseModel.Actor, 800, 160, 170, 48, L.T("Payment provider"), null, ("stereotype", L.T("Service")));

        var browse = b.Node(UseCaseModel.UseCase, 510, 100, L.T("Browse the catalogue"), system);
        var order = b.Node(UseCaseModel.UseCase, 510, 220, L.T("Place an order"), system, ("extensionPoints", L.T("coupon")));
        order.Height = 96;
        var coupon = b.Node(UseCaseModel.UseCase, 510, 350, L.T("Apply a coupon"), system, Warm);
        var pay = b.Node(UseCaseModel.UseCase, 650, 450, L.T("Pay"), system, Accent);
        pay.Width = 130;
        var card = b.Node(UseCaseModel.UseCase, 650, 560, L.T("Pay by card"), system);
        card.Width = 130;
        var login = b.Node(UseCaseModel.UseCase, 400, 470, L.T("Log in"), system);
        login.Width = 140;
        var products = b.Node(UseCaseModel.UseCase, 450, 630, L.T("Manage products"), system);
        var note = b.Node(UseCaseModel.Note, 960, 330, L.T("Orders over 50 € ship free"), null, ("fill", "#FFF8C5"));

        b.Edge(UseCaseModel.Association, customer, browse);
        b.Edge(UseCaseModel.Association, customer, order, "", Red);
        b.Edge(UseCaseModel.Association, guest, browse);
        b.Edge(UseCaseModel.Association, bank, pay, "", BothArrows);
        b.Edge(UseCaseModel.Association, admin, products, "", ("dash", "dotted"));
        b.Edge(UseCaseModel.Generalization, guest, customer);
        b.Edge(UseCaseModel.Include, order, pay);
        b.Edge(UseCaseModel.Include, order, login, "", ("line", "#3574F0"));
        b.Edge(UseCaseModel.Include, products, login);
        b.Edge(UseCaseModel.Extend, coupon, order, "", ("condition", L.T("the customer has a code")));
        b.Edge(UseCaseModel.Generalization, card, pay);
        b.Edge(UseCaseModel.NoteLink, note, order);
        return b.Diagram;
    }

    private static Diagram ActivityShowcase()
    {
        var b = new SampleBuilder(new ActivityModel(), L.T("Order processing"));
        var client = b.Box(ActivityModel.Lane, 40, 40, 300, 880, L.T("Customer"), null, ("vertical", "true"));
        var shop = b.Box(ActivityModel.Lane, 340, 40, 600, 880, L.T("Shop"), null, ("vertical", "true"), ("fill", "#F3F8FF"));

        var start = b.Node(ActivityModel.Start, 190, 110, null, client);
        var choose = b.Node(ActivityModel.Action, 190, 190, L.T("Choose products"), client);
        var send = b.Node(ActivityModel.SendSignal, 190, 280, L.T("Send the order"), client);
        var receive = b.Node(ActivityModel.ReceiveSignal, 560, 280, L.T("Order received"), shop);
        var stock = b.Node(ActivityModel.Decision, 560, 370, null, shop);
        var cancel = b.Node(ActivityModel.Action, 820, 370, L.T("Cancel the order"), shop, Warm);
        cancel.Width = 140;
        var stop = b.Node(ActivityModel.FlowEnd, 820, 450, null, shop);
        var fork = b.Node(ActivityModel.Bar, 560, 440, null, shop);
        var prepare = b.Box(ActivityModel.Activity, 370, 470, 380, 160, L.T("Prepare"), shop);
        var pack = b.Node(ActivityModel.Action, 470, 560, L.T("Pack the items"), prepare);
        pack.Width = 140;
        var print = b.Node(ActivityModel.Action, 650, 560, L.T("Print the invoice"), prepare);
        print.Width = 140;
        var invoice = b.Node(ActivityModel.Object, 840, 560, L.T("Invoice"), shop);
        invoice.Width = 100;
        var join = b.Node(ActivityModel.Bar, 560, 680, null, shop);
        var ship = b.Node(ActivityModel.Action, 560, 760, L.T("Ship"), shop, Accent);
        var end = b.Node(ActivityModel.End, 560, 850, null, shop);
        var note = b.Node(ActivityModel.Note, 190, 760, L.T("Express delivery within 24 h"), client);

        b.Edge(ActivityModel.Flow, start, choose);
        b.Edge(ActivityModel.Flow, choose, send);
        b.Edge(ActivityModel.Flow, send, receive, "", ("dash", "dashed"), ("line", "#8E5BD6"));
        b.Edge(ActivityModel.Flow, receive, stock);
        b.Edge(ActivityModel.Flow, stock, fork, L.T("[in stock]"), ("line", "#2E9E5B"));
        b.Edge(ActivityModel.Flow, stock, cancel, L.T("[otherwise]"), Red);
        b.Edge(ActivityModel.Flow, cancel, stop);
        b.Edge(ActivityModel.Flow, fork, pack);
        b.Edge(ActivityModel.Flow, fork, print);
        b.Edge(ActivityModel.Flow, print, invoice, "", ("endArrow", "filled"));
        b.Edge(ActivityModel.Flow, pack, join);
        b.Edge(ActivityModel.Flow, invoice, join);
        b.Edge(ActivityModel.Flow, join, ship, "", ("width", "2.4"));
        b.Edge(ActivityModel.Flow, ship, end);
        var link = b.Edge(ActivityModel.NoteLink, note, ship);
        link.Routing = EdgeRouting.Straight;
        return b.Diagram;
    }

    private static Diagram ClassShowcase()
    {
        var b = new SampleBuilder(new ClassModel(), L.T("Shop domain"));
        var domain = b.Box(ClassModel.Package, 30, 30, 1040, 760, L.T("domain"));
        var person = b.Node(ClassModel.Class, 200, 120, L.T("Person"), domain, ("abstract", "true"), ("stereotype", "entity"),
            ("properties", L.T("author=Ana")),
            ("attributes", L.T("# lastName : String\n# firstName : String\n# address : Address")),
            ("operations", L.T("+ fullName() : String\n+ abstract greeting() : String")));
        var customer = b.Node(ClassModel.Class, 200, 360, L.T("Customer"), domain,
            ("attributes", L.T("- number : int\n- static count : int = 0")),
            ("operations", L.T("+ static nextNumber() : int\n+ greeting() : String")));
        var order = b.Node(ClassModel.Class, 540, 360, L.T("Order"), domain, Accent.With(
            ("attributes", L.T("- date : Date\n- status : OrderStatus = NEW")),
            ("operations", L.T("+ total() : float\n+ pay(card : String, amount : float) : boolean"))));
        var line = b.Node(ClassModel.Class, 540, 620, L.T("OrderLine"), domain,
            ("attributes", L.T("- quantity : int = 1")), ("showOperations", "false"));
        var product = b.Node(ClassModel.Class, 870, 620, L.T("Product"), domain,
            ("attributes", L.T("- name : String\n- price : float")), ("operations", L.T("+ discounted(rate : float) : float")));
        var category = b.Node(ClassModel.Class, 870, 380, L.T("Category"), domain, ("attributes", L.T("- label : String")), ("showOperations", "false"));
        var payable = b.Node(ClassModel.Interface, 540, 110, L.T("Payable"), domain, ("operations", L.T("+ pay(card : String, amount : float) : boolean")));
        var status = b.Node(ClassModel.Enumeration, 870, 140, L.T("OrderStatus"), domain, Warm.With(("attributes", L.T("NEW\nPAID\nSHIPPED\nCANCELLED"))));
        var address = b.Node(ClassModel.DataType, 200, 600, L.T("Address"), domain, ("attributes", L.T("+ street : String\n+ city : String")));
        var note = b.Node(ClassModel.Note, 1180, 380, L.T("Prices include VAT"), null);

        b.Edge(ClassModel.Generalization, customer, person);
        b.Edge(ClassModel.Realization, order, payable);
        b.Edge(ClassModel.Association, customer, order, L.T("places"), ("fromMultiplicity", "1"), ("toMultiplicity", "0..*"),
            ("toRole", L.T("orders")), ("navigable", "true"));
        b.Edge(ClassModel.Composition, order, line, "", ("fromMultiplicity", "1"), ("toMultiplicity", "1..*"), ("line", "#3574F0"), ("width", "1.8"));
        b.Edge(ClassModel.Association, line, product, "", ("fromMultiplicity", "*"), ("toMultiplicity", "1"), ("navigable", "true"));
        b.Edge(ClassModel.Aggregation, category, product, L.T("contains"), ("fromMultiplicity", "0..1"), ("toMultiplicity", "*"));
        b.Edge(ClassModel.Dependency, order, status, L.T("uses"), ("dash", "dotted"));
        b.Edge(ClassModel.Dependency, person, address);
        b.Edge(ClassModel.Association, customer, category, L.T("follows"), BothArrows.With(("fromMultiplicity", "*"), ("toMultiplicity", "*"), ("line", "#8E5BD6")))
            .Routing = EdgeRouting.Orthogonal;
        b.Edge(ClassModel.NoteLink, note, product);
        return b.Diagram;
    }

    private static Diagram ObjectShowcase()
    {
        var b = new SampleBuilder(new ClassModel(objects: true), L.T("An order"));
        var ana = b.Node(ClassModel.Instance, 160, 120, L.T("ana : Customer"), null, Accent.With(("values", L.T("number = 17\nlastName = \"Martin\""))));
        var order = b.Node(ClassModel.Instance, 480, 120, L.T("o42 : Order"), null, ("values", L.T("date = 2026-10-18\nstatus = PAID")));
        var line1 = b.Node(ClassModel.Instance, 400, 330, L.T("l1 : OrderLine"), null, ("values", L.T("quantity = 2")));
        var line2 = b.Node(ClassModel.Instance, 640, 330, L.T("l2 : OrderLine"), null, ("values", L.T("quantity = 1")));
        var pen = b.Node(ClassModel.Instance, 400, 520, L.T("pen : Product"), null, Warm.With(("values", L.T("name = \"Pen\"\nprice = 1.5"))));
        var book = b.Node(ClassModel.Instance, 640, 520, L.T("book : Product"), null, ("values", L.T("name = \"Book\"\nprice = 12")));
        var note = b.Node(ClassModel.Note, 160, 330, L.T("A snapshot at payment time"));
        b.Edge(ClassModel.Link, ana, order, L.T("places"), Red);
        b.Edge(ClassModel.Link, order, line1);
        b.Edge(ClassModel.Link, order, line2, "", ("dash", "dashed"));
        b.Edge(ClassModel.Link, line1, pen);
        b.Edge(ClassModel.Link, line2, book, "", BothArrows);
        b.Edge(ClassModel.NoteLink, note, ana);
        return b.Diagram;
    }

    private static Diagram SequenceShowcase()
    {
        var b = new SampleBuilder(new SequenceModel(), L.T("Checkout"));
        var customer = b.Box(SequenceModel.Lifeline, 60, 60, 120, 820, L.T(":Customer"), null, ("actor", "true"));
        var shop = b.Box(SequenceModel.Lifeline, 290, 60, 140, 820, L.T(":Shop"), null, Accent);
        var payment = b.Box(SequenceModel.Lifeline, 540, 60, 150, 820, L.T(":PaymentService"));
        var invoice = b.Box(SequenceModel.Lifeline, 800, 60, 130, 760, L.T(":Invoice"), null, Warm.With(("destroyed", "true")));
        b.Box(SequenceModel.Activation, 354, 160, 12, 600, null, shop);
        b.Box(SequenceModel.Activation, 609, 380, 12, 140, null, payment, ("fill", "#DCEBFF"));
        var y = 150.0;
        DiagramEdge Message(string type, DiagramNode from, DiagramNode to, string text, params (string, string)[] props)
        {
            var e = b.Edge(type, from, to, text, props);
            e.Waypoints = [new DPoint(0, y)];
            y += 44;
            return e;
        }
        Message(SequenceModel.Sync, customer, shop, L.T("addToCart(item)"));
        var loopTop = y - 34;
        Message(SequenceModel.Sync, shop, shop, L.T("checkStock(item)"));
        y += 24;
        var loopBottom = y - 20;
        Message(SequenceModel.Sync, customer, shop, L.T("checkout()"), Red);
        var altTop = y - 34;
        Message(SequenceModel.Sync, shop, payment, L.T("charge(card, total)"));
        Message(SequenceModel.Async, payment, shop, L.T("paymentConfirmed"), ("line", "#2E9E5B"));
        Message(SequenceModel.CreateMessage, shop, invoice, "«create»");
        var elseAt = y - 30 - altTop;
        Message(SequenceModel.Reply, payment, shop, L.T("refused"), ("dash", "dotted"));
        var altBottom = y - 14;
        var optTop = y - 4;
        y += 20;
        Message(SequenceModel.Async, shop, customer, L.T("sendReceipt(email)"));
        var optBottom = y - 14;
        Message(SequenceModel.Reply, shop, customer, L.T("orderNumber"), ("width", "2"));
        b.Box(SequenceModel.Fragment, 250, loopTop, 280, loopBottom - loopTop, "loop", null, ("guard", L.T("[for each item]")));
        b.Box(SequenceModel.Fragment, 40, altTop, 910, altBottom - altTop, "alt", null,
            ("guard", L.T("[card accepted]")), ("guard2", L.T("[else]")), ("elseAt", elseAt.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        b.Box(SequenceModel.Fragment, 40, optTop, 420, optBottom - optTop, "opt", null, ("guard", L.T("[email given]")), ("line", "#8E5BD6"));
        var note = b.Node(SequenceModel.Note, 1040, 200, L.T("The invoice is deleted once archived"));
        b.Edge(SequenceModel.NoteLink, note, invoice);
        return b.Diagram;
    }

    private static Diagram StateShowcase()
    {
        var b = new SampleBuilder(new StateModel(), L.T("Order life"));
        var initial = b.Node(StateModel.Initial, 60, 120);
        var created = b.Node(StateModel.State, 230, 120, L.T("Created"), null, ("activities", L.T("entry / send a confirmation\nexit / log")));
        var check = b.Node(StateModel.Choice, 420, 120);
        var processing = b.Box(StateModel.Composite, 520, 40, 520, 320, L.T("Processing"), null, ("fill", "#F3F8FF"), ("line", "#3574F0"));
        var inner = b.Node(StateModel.Initial, 560, 120, null, processing);
        var fork = b.Node(StateModel.Bar, 680, 120, null, processing);
        fork.Width = 8;
        fork.Height = 120;
        var packing = b.Node(StateModel.State, 820, 90, L.T("Packing"), processing, Accent);
        var billing = b.Node(StateModel.State, 820, 200, L.T("Billing"), processing, ("activities", L.T("do / print the invoice")));
        var join = b.Node(StateModel.Bar, 960, 150, null, processing);
        join.Width = 8;
        join.Height = 120;
        var history = b.Node(StateModel.History, 600, 300, "H", processing);
        var shipped = b.Node(StateModel.State, 780, 470, L.T("Shipped"), null, Warm);
        var onHold = b.Node(StateModel.State, 420, 300, L.T("On hold"));
        var cancelled = b.Node(StateModel.State, 230, 300, L.T("Cancelled"), null, ("activities", L.T("entry / refund")), ("textColor", "#E5534B"));
        var final = b.Node(StateModel.Final, 230, 470);
        var note = b.Node(StateModel.Note, 1120, 470, L.T("A paused order resumes where it was"));

        b.Edge(StateModel.Transition, initial, created);
        b.Edge(StateModel.Transition, created, check, L.T("pay"));
        b.Edge(StateModel.Transition, check, processing, L.T("[paid]"), ("line", "#2E9E5B"));
        b.Edge(StateModel.Transition, check, cancelled, L.T("[refused] / notify"), Red);
        b.Edge(StateModel.Transition, inner, fork);
        b.Edge(StateModel.Transition, fork, packing);
        b.Edge(StateModel.Transition, fork, billing);
        b.Edge(StateModel.Transition, packing, join);
        b.Edge(StateModel.Transition, billing, join);
        b.Edge(StateModel.Transition, join, shipped, L.T("ready"), ("width", "2"));
        b.Edge(StateModel.Transition, processing, onHold, L.T("pause"), ("dash", "dashed"));
        b.Edge(StateModel.Transition, onHold, history, L.T("resume"), BothArrows);
        b.Edge(StateModel.Transition, shipped, final, L.T("deliver"));
        b.Edge(StateModel.Transition, cancelled, final);
        b.Edge(StateModel.NoteLink, note, history);
        return b.Diagram;
    }

    private static Diagram ComponentShowcase()
    {
        var model = new StructureModel(StructureModel.ComponentKind);
        var b = new SampleBuilder(model, L.T("Shop components"));
        var web = b.Box(StructureModel.Package, 30, 30, 440, 360, L.T("web"));
        var front = b.Box(StructureModel.Component, 70, 90, 180, 90, L.T("Storefront"), web, Accent);
        var cart = b.Box(StructureModel.Component, 70, 250, 180, 90, L.T("Cart"), web);
        var port = b.Box(StructureModel.Port, 243, 128, 14, 14, null, front);
        var api = b.Node(StructureModel.ProvidedInterface, 360, 135, L.T("OrderApi"), web);
        var orders = b.Box(StructureModel.Component, 560, 90, 200, 100, L.T("Orders"), null, Warm);
        var payments = b.Box(StructureModel.Component, 560, 260, 200, 90, L.T("Payments"));
        var pay = b.Node(StructureModel.ProvidedInterface, 870, 305, L.T("PaymentApi"));
        var db = b.Box(StructureModel.Database, 860, 90, 110, 90, L.T("Orders DB"));
        var jar = b.Box(StructureModel.Artifact, 560, 430, 160, 64, L.T("orders.jar"));
        var note = b.Box(StructureModel.Note, 70, 440, 260, 60, L.T("The storefront talks to Orders through its API"));

        b.Edge(StructureModel.Dependency, port, api, L.T("uses"));
        b.Edge(StructureModel.Realization, orders, api, "", ("line", "#3574F0"));
        b.Edge(StructureModel.Association, cart, front, L.T("assembly"), BothArrows);
        b.Edge(StructureModel.Dependency, orders, pay, "", Red);
        b.Edge(StructureModel.Realization, payments, pay);
        b.Edge(StructureModel.Dependency, orders, db, L.T("reads / writes"), ("dash", "dotted"));
        b.Edge(StructureModel.Dependency, jar, orders, L.T("«manifest»"));
        b.Edge(StructureModel.NoteLink, note, front);
        return b.Diagram;
    }

    private static Diagram DeploymentShowcase()
    {
        var model = new StructureModel(StructureModel.DeploymentKind);
        var b = new SampleBuilder(model, L.T("Shop deployment"));
        var server = b.Box(StructureModel.Node, 40, 40, 420, 300, L.T("Web server"), null, ("fill", "#F3F8FF"));
        var runtime = b.Box(StructureModel.Environment, 70, 110, 300, 200, L.T("Docker"), server);
        var app = b.Box(StructureModel.Artifact, 100, 170, 150, 60, L.T("shop.war"), runtime, Accent);
        var front = b.Box(StructureModel.Component, 100, 245, 150, 50, L.T("Storefront"), runtime);
        var dbServer = b.Box(StructureModel.Node, 600, 40, 300, 240, L.T("Database server"));
        var db = b.Box(StructureModel.Database, 680, 120, 120, 90, L.T("shop"), dbServer, Warm);
        var phone = b.Box(StructureModel.Device, 40, 430, 220, 140, L.T("Phone"));
        var appArtifact = b.Box(StructureModel.Artifact, 70, 490, 150, 60, L.T("shop.apk"), phone);
        var note = b.Box(StructureModel.Note, 600, 430, 260, 60, L.T("Backups every night at 2 am"));

        b.Edge(StructureModel.Association, server, dbServer, L.T("JDBC / TLS"), Red);
        b.Edge(StructureModel.Association, phone, server, L.T("HTTPS"), BothArrows);
        b.Edge(StructureModel.Deploy, app, runtime, "«deploy»", ("dash", "dashed"));
        b.Edge(StructureModel.Dependency, front, db, L.T("queries"), ("dash", "dotted"));
        b.Edge(StructureModel.Deploy, appArtifact, phone);
        b.Edge(StructureModel.NoteLink, note, db);
        return b.Diagram;
    }

    private static Diagram PackageShowcase()
    {
        var model = new StructureModel(StructureModel.PackageKind);
        var b = new SampleBuilder(model, L.T("Shop packages"));
        var shop = b.Box(StructureModel.Package, 30, 30, 640, 400, L.T("shop"), null, ("fill", "#FAFAFB"), ("bold", "true"));
        var ui = b.Box(StructureModel.Package, 70, 90, 220, 140, L.T("ui"), shop, Accent);
        var domain = b.Box(StructureModel.Package, 380, 90, 240, 140, L.T("domain"), shop);
        var data = b.Box(StructureModel.Package, 380, 270, 240, 130, L.T("data"), shop, Warm);
        var util = b.Box(StructureModel.Package, 800, 90, 200, 140, L.T("util"));
        var note = b.Box(StructureModel.Note, 800, 300, 220, 60, L.T("ui never uses data directly"));
        b.Edge(StructureModel.Dependency, ui, domain, "", Red);
        b.Edge(StructureModel.Dependency, data, domain);
        b.Edge(StructureModel.Import, domain, util, "«import»", ("dash", "dotted"));
        b.Edge(StructureModel.Import, ui, util, "«import»", BothArrows).Routing = EdgeRouting.Orthogonal;
        b.Edge(StructureModel.NoteLink, note, data);
        return b.Diagram;
    }
}
