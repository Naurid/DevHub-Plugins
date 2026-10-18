using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Uml.Core;

/// <summary>
/// Samples rebuilt from the course's exercises (hospital, cash machine, …), in the user's language: a start for a
/// student and an example of the notation.
/// </summary>
public static class UmlSamples
{
    public static IReadOnlyList<DiagramSample> UseCase =>
    [
        new(L.T("Hospital"), L.T("Patients book appointments, doctors consult; «include», «extend» and a service actor."), Hospital),
    ];

    public static IReadOnlyList<DiagramSample> Activity =>
    [
        new(L.T("Cash machine"), L.T("Withdrawing cash: decisions with guards, a loop, a fork and a join."), CashMachine),
    ];

    public static IReadOnlyList<DiagramSample> Class =>
    [
        new(L.T("Hospital"), L.T("Persons, patients and doctors, appointments and invoices: inheritance, associations, composition."), HospitalClasses),
    ];

    public static IReadOnlyList<DiagramSample> Sequence =>
    [
        new(L.T("Cash withdrawal"), L.T("A customer, the cash machine and the bank exchanging messages, with an alt fragment."), Withdrawal),
    ];

    public static IReadOnlyList<DiagramSample> State =>
    [
        new(L.T("Order"), L.T("An order's life: created, paid, shipped, delivered, or cancelled."), Order),
    ];

    private static Diagram Hospital()
    {
        var b = new SampleBuilder(new UseCaseModel(), L.T("Hospital"));
        var system = b.Box(UseCaseModel.Boundary, 260, 30, 400, 600, L.T("Hospital"));
        var patient = b.Node(UseCaseModel.Actor, 120, 170, L.T("Patient"));
        var secretary = b.Node(UseCaseModel.Actor, 120, 430, L.T("Secretary"));
        var doctor = b.Node(UseCaseModel.Actor, 800, 200, L.T("Doctor"));
        var auth = b.Box(UseCaseModel.Actor, 730, 490, 160, 48, L.T("Authentication"), null, ("stereotype", L.T("Service")));

        var book = b.Node(UseCaseModel.UseCase, 460, 110, L.T("Book an appointment"), system);
        var consult = b.Node(UseCaseModel.UseCase, 460, 240, L.T("Consult a patient"), system, ("extensionPoints", L.T("prescription")));
        consult.Height = 96;
        var print = b.Node(UseCaseModel.UseCase, 460, 380, L.T("Print a prescription"), system);
        var pay = b.Node(UseCaseModel.UseCase, 460, 470, L.T("Pay the invoice"), system);
        var login = b.Node(UseCaseModel.UseCase, 460, 570, L.T("Log in"), system);

        b.Edge(UseCaseModel.Association, patient, book);
        b.Edge(UseCaseModel.Association, patient, pay);
        b.Edge(UseCaseModel.Association, secretary, book);
        b.Edge(UseCaseModel.Association, secretary, pay);
        b.Edge(UseCaseModel.Association, doctor, consult);
        b.Edge(UseCaseModel.Association, auth, login);
        b.Edge(UseCaseModel.Include, consult, login);
        b.Edge(UseCaseModel.Include, pay, login);
        b.Edge(UseCaseModel.Extend, print, consult, "", ("condition", L.T("medicine prescribed")));
        return b.Diagram;
    }

    private static Diagram CashMachine()
    {
        var m = new ActivityModel();
        var b = new SampleBuilder(m, L.T("Cash machine"));
        var start = b.Node(ActivityModel.Start, 300, 40);
        var insert = b.Node(ActivityModel.Action, 300, 110, L.T("Insert the card"));
        var code = b.Node(ActivityModel.Action, 300, 190, L.T("Enter the PIN"));
        var right = b.Node(ActivityModel.Decision, 300, 270);
        var amount = b.Node(ActivityModel.Action, 300, 350, L.T("Choose the amount"));
        var enough = b.Node(ActivityModel.Decision, 300, 430);
        var fork = b.Node(ActivityModel.Bar, 300, 500);
        var cash = b.Node(ActivityModel.Action, 180, 570, L.T("Give the cash"));
        var card = b.Node(ActivityModel.Action, 420, 570, L.T("Give the card back"));
        var join = b.Node(ActivityModel.Bar, 300, 640);
        var end = b.Node(ActivityModel.End, 300, 710);
        var refuse = b.Node(ActivityModel.Action, 560, 270, L.T("Keep the card"));
        var refused = b.Node(ActivityModel.End, 560, 350);
        var low = b.Node(ActivityModel.Action, 560, 430, L.T("Show \"balance too low\""));

        b.Edge(ActivityModel.Flow, start, insert);
        b.Edge(ActivityModel.Flow, insert, code);
        b.Edge(ActivityModel.Flow, code, right);
        b.Edge(ActivityModel.Flow, right, amount, L.T("[PIN correct]"));
        b.Edge(ActivityModel.Flow, right, refuse, L.T("[third wrong PIN]"));
        b.Edge(ActivityModel.Flow, refuse, refused);
        b.Edge(ActivityModel.Flow, amount, enough);
        b.Edge(ActivityModel.Flow, enough, fork, L.T("[balance enough]"));
        b.Edge(ActivityModel.Flow, enough, low, L.T("[otherwise]"));
        b.Edge(ActivityModel.Flow, low, amount);
        b.Edge(ActivityModel.Flow, fork, cash);
        b.Edge(ActivityModel.Flow, fork, card);
        b.Edge(ActivityModel.Flow, cash, join);
        b.Edge(ActivityModel.Flow, card, join);
        b.Edge(ActivityModel.Flow, join, end);
        return b.Diagram;
    }

    private static Diagram HospitalClasses()
    {
        var b = new SampleBuilder(new ClassModel(), L.T("Hospital"));
        var person = b.Node(ClassModel.Class, 400, 80, L.T("Person"), null, ("abstract", "true"),
            ("attributes", L.T("# lastName : String\n# firstName : String")), ("operations", L.T("+ fullName() : String")));
        var patient = b.Node(ClassModel.Class, 220, 300, L.T("Patient"), null,
            ("attributes", L.T("- number : int\n- birthDate : Date\n- static count : int")), ("operations", L.T("+ age() : int")));
        var doctor = b.Node(ClassModel.Class, 600, 300, L.T("Doctor"), null,
            ("attributes", L.T("- staffNumber : String\n- speciality : Speciality")));
        var speciality = b.Node(ClassModel.Enumeration, 860, 300, L.T("Speciality"), null,
            ("attributes", L.T("GENERAL\nCARDIOLOGY\nPAEDIATRICS")));
        var appointment = b.Node(ClassModel.Class, 400, 520, L.T("Appointment"), null,
            ("attributes", L.T("- date : Date\n- time : Time")), ("operations", L.T("+ cancel() : void")));
        var invoice = b.Node(ClassModel.Class, 400, 740, L.T("Invoice"), null,
            ("attributes", L.T("- number : int\n- amount : float = 0")), ("operations", L.T("+ pay() : boolean")));

        b.Edge(ClassModel.Generalization, patient, person);
        b.Edge(ClassModel.Generalization, doctor, person);
        b.Edge(ClassModel.Association, patient, appointment, L.T("books"), ("fromMultiplicity", "1"), ("toMultiplicity", "0..*"));
        b.Edge(ClassModel.Association, doctor, appointment, L.T("sees"), ("fromMultiplicity", "1"), ("toMultiplicity", "0..*"));
        b.Edge(ClassModel.Composition, appointment, invoice, "", ("fromMultiplicity", "1"), ("toMultiplicity", "0..1"));
        b.Edge(ClassModel.Dependency, doctor, speciality);
        return b.Diagram;
    }

    private static Diagram Withdrawal()
    {
        var b = new SampleBuilder(new SequenceModel(), L.T("Cash withdrawal"));
        var customer = b.Box(SequenceModel.Lifeline, 60, 60, 120, 620, L.T(":Customer"), null, ("actor", "true"));
        var atm = b.Box(SequenceModel.Lifeline, 300, 60, 140, 620, L.T(":CashMachine"));
        var bank = b.Box(SequenceModel.Lifeline, 560, 60, 120, 620, L.T(":Bank"));
        var y = 140.0;
        DiagramEdge Message(string type, DiagramNode from, DiagramNode to, string text)
        {
            var e = b.Edge(type, from, to, text);
            e.Waypoints = [new DPoint(0, y)];
            y += 44;
            return e;
        }
        Message(SequenceModel.Sync, customer, atm, L.T("insertCard()"));
        Message(SequenceModel.Reply, atm, customer, L.T("askPin"));
        Message(SequenceModel.Sync, customer, atm, L.T("enterPin(pin)"));
        Message(SequenceModel.Sync, atm, bank, L.T("checkPin(card, pin)"));
        Message(SequenceModel.Reply, bank, atm, L.T("pinValid"));
        Message(SequenceModel.Sync, customer, atm, L.T("withdraw(amount)"));
        var fragmentTop = y - 34;
        Message(SequenceModel.Sync, atm, bank, L.T("debit(account, amount)"));
        Message(SequenceModel.Reply, bank, atm, L.T("ok"));
        Message(SequenceModel.Reply, atm, customer, L.T("cash"));
        var elseAt = y - 30 - fragmentTop;
        Message(SequenceModel.Reply, atm, customer, L.T("refusal"));
        b.Box(SequenceModel.Fragment, 40, fragmentTop, 660, y - fragmentTop + 4, "alt", null,
            ("guard", L.T("[balance enough]")), ("guard2", L.T("[else]")),
            ("elseAt", elseAt.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return b.Diagram;
    }

    private static Diagram Order()
    {
        var b = new SampleBuilder(new StateModel(), L.T("Order"));
        var initial = b.Node(StateModel.Initial, 60, 80);
        var created = b.Node(StateModel.State, 220, 80, L.T("Created"), null, ("activities", L.T("entry / send a confirmation")));
        var paid = b.Node(StateModel.State, 460, 80, L.T("Paid"));
        var shipped = b.Node(StateModel.State, 700, 80, L.T("Shipped"), null, ("activities", L.T("do / track the parcel")));
        var delivered = b.Node(StateModel.State, 700, 260, L.T("Delivered"));
        var cancelled = b.Node(StateModel.State, 220, 260, L.T("Cancelled"), null, ("activities", L.T("entry / refund")));
        var final = b.Node(StateModel.Final, 460, 360);
        b.Edge(StateModel.Transition, initial, created);
        b.Edge(StateModel.Transition, created, paid, L.T("pay"));
        b.Edge(StateModel.Transition, paid, shipped, L.T("ship [in stock]"));
        b.Edge(StateModel.Transition, shipped, delivered, L.T("deliver"));
        b.Edge(StateModel.Transition, created, cancelled, L.T("cancel"));
        b.Edge(StateModel.Transition, paid, cancelled, L.T("cancel / refund"));
        b.Edge(StateModel.Transition, delivered, final);
        b.Edge(StateModel.Transition, cancelled, final);
        return b.Diagram;
    }
}
