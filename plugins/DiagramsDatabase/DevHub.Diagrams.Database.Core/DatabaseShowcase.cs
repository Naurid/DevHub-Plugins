using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Database.Core;

/// <summary>
/// "Everything" samples for the database kinds: every kind of association the MCD → MLD rules handle (1–n with
/// properties, n–n, ternary, reflexive with roles, 1–1 with the key side chosen) and every column option and
/// relationship end of the table diagram, with formatting.
/// </summary>
public static class DatabaseShowcase
{
    public static string Name => L.T("Showcase: everything");

    public static IReadOnlyList<DiagramSample> Merise =>
        [new(Name, L.T("1–n with properties, n–n, ternary, reflexive with roles, 1–1: every rule Generate MLD applies."), MeriseShowcase)];

    public static IReadOnlyList<DiagramSample> Erd =>
        [new(Name, L.T("Keys, composite keys, unique, counters, defaults, comments and every crow's-foot end."), ErdShowcase)];

    private static Diagram MeriseShowcase()
    {
        var b = new SampleBuilder(new MeriseModel(), L.T("School"));
        var student = b.Box(MeriseModel.Entity, 40, 60, 210, 140, L.T("Student"), null,
            ("identifier", L.T("studentNumber : Counter // Given at enrolment")),
            ("properties", L.T("lastName : Alphanumeric(50)\nfirstName : Alphanumeric(50)\nbirthDate : Date\nscholarship : Boolean")),
            ("fill", "#DCEBFF"), ("line", "#3574F0"), ("bold", "true"));
        var course = b.Box(MeriseModel.Entity, 620, 60, 210, 110, L.T("Course"), null,
            ("identifier", L.T("courseCode : Char(6)")),
            ("properties", L.T("title : Alphanumeric(80)\ncredits : Integer")));
        var enrol = b.Box(MeriseModel.Association, 330, 90, 180, 80, L.T("enrols in"), null,
            ("properties", L.T("enrolDate : Date\nmark : Decimal(4,2)")));
        var teacher = b.Box(MeriseModel.Entity, 620, 320, 210, 110, L.T("Teacher"), null,
            ("identifier", L.T("teacherId : Counter")),
            ("properties", L.T("teacherName : Alphanumeric(60)\nhourlyRate : Money")),
            ("fill", "#FFF0D9"), ("line", "#D98C1F"));
        var teach = b.Box(MeriseModel.Association, 660, 210, 140, 60, L.T("teaches"));
        var supervise = b.Box(MeriseModel.Association, 900, 340, 150, 64, L.T("supervises"));
        var room = b.Box(MeriseModel.Entity, 340, 520, 200, 90, L.T("Room"), null,
            ("identifier", L.T("roomNumber : Char(4)")), ("properties", L.T("seats : Integer")));
        var slot = b.Box(MeriseModel.Entity, 40, 520, 200, 90, L.T("TimeSlot"), null,
            ("identifier", L.T("slotId : Counter")), ("properties", L.T("startsAt : DateTime")));
        var schedule = b.Box(MeriseModel.Association, 360, 360, 160, 80, L.T("is scheduled"), null,
            ("properties", L.T("groupSize : Integer")));
        var office = b.Box(MeriseModel.Entity, 900, 520, 200, 90, L.T("Office"), null,
            ("identifier", L.T("officeCode : Char(5)")), ("properties", L.T("phone : Alphanumeric(15)")));
        var occupy = b.Box(MeriseModel.Association, 920, 440, 140, 50, L.T("occupies"));
        var note = b.Box(MeriseModel.Note, 40, 300, 220, 80, L.T("Each course is taught by one teacher; a teacher may teach several."));

        // n–n with properties → a junction table.
        b.Edge(MeriseModel.Leg, student, enrol, "", ("card", "0,n"));
        b.Edge(MeriseModel.Leg, course, enrol, "", ("card", "1,n"), ("line", "#3574F0"), ("width", "1.8"));
        // 1–n → a foreign key in Course.
        b.Edge(MeriseModel.Leg, course, teach, "", ("card", "1,1"));
        b.Edge(MeriseModel.Leg, teacher, teach, "", ("card", "0,n"));
        // Reflexive with roles → a foreign key to the same table.
        b.Edge(MeriseModel.Leg, teacher, supervise, "", ("card", "0,1"), ("role", L.T("trainee")), ("dash", "dashed"));
        b.Edge(MeriseModel.Leg, teacher, supervise, "", ("card", "0,n"), ("role", L.T("mentor")));
        // Ternary → a table keyed by the three.
        b.Edge(MeriseModel.Leg, course, schedule, "", ("card", "0,n"));
        b.Edge(MeriseModel.Leg, room, schedule, "", ("card", "0,n"));
        b.Edge(MeriseModel.Leg, slot, schedule, "", ("card", "0,n"));
        // 1–1 with the key on the chosen side.
        b.Edge(MeriseModel.Leg, teacher, occupy, "", ("card", "0,1"), ("keyHere", "true"));
        b.Edge(MeriseModel.Leg, office, occupy, "", ("card", "0,1"), ("line", "#E5534B"));
        b.Edge(MeriseModel.NoteLink, note, teach);
        return b.Diagram;
    }

    private static Diagram ErdShowcase()
    {
        var b = new SampleBuilder(new ErdModel(), L.T("Shop tables"));
        b.Diagram.Props["notation"] = ErdModel.NotationCrowsFoot;
        var customer = b.Box(ErdModel.Table, 40, 60, 260, 150, "customer", null,
            ("columns", "PK id INT AI\nemail VARCHAR(120) NOT NULL UNIQUE // login\nname VARCHAR(80) NOT NULL\ncreated DATETIME DEFAULT CURRENT_TIMESTAMP"),
            ("comment", L.T("People who buy")), ("fill", "#DCEBFF"), ("line", "#3574F0"));
        var orders = b.Box(ErdModel.Table, 420, 60, 260, 130, "orders", null,
            ("columns", "PK id INT AI\nFK customer_id INT NOT NULL -> customer.id\nstatus VARCHAR(12) DEFAULT 'NEW'\ntotal DECIMAL(10,2)"));
        var product = b.Box(ErdModel.Table, 800, 60, 240, 110, "product", null,
            ("columns", "PK sku CHAR(8)\nlabel VARCHAR(100) NOT NULL\nprice DECIMAL(8,2) NOT NULL"));
        var line = b.Box(ErdModel.Table, 600, 300, 260, 110, "order_line", null,
            ("columns", "PK FK order_id INT -> orders.id\nPK FK sku CHAR(8) -> product.sku\nquantity INT NOT NULL DEFAULT 1"),
            ("fill", "#FFF0D9"), ("line", "#D98C1F"));
        var profile = b.Box(ErdModel.Table, 40, 320, 260, 100, "profile", null,
            ("columns", "PK FK customer_id INT -> customer.id\navatar VARCHAR(200)\nbio TEXT"));
        var coupon = b.Box(ErdModel.Table, 340, 480, 240, 100, "coupon", null,
            ("columns", "PK code VARCHAR(20)\nFK order_id INT UNIQUE -> orders.id\nrate DECIMAL(4,2) NOT NULL"));
        var note = b.Box(ErdModel.Note, 900, 480, 220, 70, L.T("order_line's key is both of its foreign keys"));

        b.Edge(ErdModel.Relationship, orders, customer, L.T("places"), ("fromEnd", "zeroOrMany"), ("toEnd", "one"));
        b.Edge(ErdModel.Relationship, line, orders, "", ("fromEnd", "oneOrMany"), ("toEnd", "one"), ("line", "#D98C1F"), ("width", "1.8"));
        b.Edge(ErdModel.Relationship, line, product, "", ("fromEnd", "zeroOrMany"), ("toEnd", "one"), ("dash", "dashed"));
        b.Edge(ErdModel.Relationship, profile, customer, L.T("has"), ("fromEnd", "zeroOrOne"), ("toEnd", "one"));
        b.Edge(ErdModel.Relationship, coupon, orders, L.T("applies to"), ("fromEnd", "zeroOrOne"), ("toEnd", "zeroOrOne"), ("line", "#E5534B"));
        b.Edge(ErdModel.NoteLink, note, line);
        return b.Diagram;
    }
}
