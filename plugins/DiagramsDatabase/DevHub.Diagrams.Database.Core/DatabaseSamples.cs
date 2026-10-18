using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Database.Core;

/// <summary>Database samples in the user's language: an MCD to generate an MLD from, and a table diagram.</summary>
public static class DatabaseSamples
{
    public static IReadOnlyList<DiagramSample> Merise =>
    [
        new(L.T("Hospital appointments"), L.T("Patients consult doctors (n–n, with properties), doctors belong to a department (1–n)."), Hospital),
    ];

    public static IReadOnlyList<DiagramSample> Erd =>
    [
        new(L.T("Library"), L.T("Authors, books, members and loans in crow's-foot notation."), Library),
    ];

    private static Diagram Hospital()
    {
        var b = new SampleBuilder(new MeriseModel(), L.T("Hospital appointments"));
        var patient = b.Box(MeriseModel.Entity, 40, 60, 190, 140, L.T("Patient"), null,
            ("identifier", L.T("patientNumber : Counter // Number given at registration")),
            ("properties", L.T("lastName : Alphanumeric(50)\nfirstName : Alphanumeric(50)\nbirthDate : Date")));
        var consult = b.Box(MeriseModel.Association, 330, 90, 160, 80, L.T("consults"), null,
            ("properties", L.T("consultationDate : DateTime")));
        var doctor = b.Box(MeriseModel.Entity, 590, 60, 190, 120, L.T("Doctor"), null,
            ("identifier", L.T("doctorNumber : Counter")),
            ("properties", L.T("doctorName : Alphanumeric(50)\nspeciality : Alphanumeric(40)")));
        var belong = b.Box(MeriseModel.Association, 610, 260, 150, 60, L.T("belongs to"));
        var department = b.Box(MeriseModel.Entity, 590, 380, 190, 100, L.T("Department"), null,
            ("identifier", L.T("departmentCode : Char(4)")),
            ("properties", L.T("label : Alphanumeric(60)")));
        b.Edge(MeriseModel.Leg, patient, consult, "", ("card", "0,n"));
        b.Edge(MeriseModel.Leg, doctor, consult, "", ("card", "0,n"));
        b.Edge(MeriseModel.Leg, doctor, belong, "", ("card", "1,1"));
        b.Edge(MeriseModel.Leg, department, belong, "", ("card", "0,n"));
        return b.Diagram;
    }

    private static Diagram Library()
    {
        var sql = """
            CREATE TABLE author (id INT AUTO_INCREMENT, name VARCHAR(80) NOT NULL, PRIMARY KEY (id));
            CREATE TABLE book (isbn CHAR(13), title VARCHAR(200) NOT NULL, year INT, author_id INT NOT NULL REFERENCES author (id), PRIMARY KEY (isbn));
            CREATE TABLE member (id INT AUTO_INCREMENT, name VARCHAR(80) NOT NULL, email VARCHAR(120) UNIQUE, PRIMARY KEY (id));
            CREATE TABLE loan (id INT AUTO_INCREMENT, isbn CHAR(13) NOT NULL REFERENCES book (isbn), member_id INT NOT NULL REFERENCES member (id),
                borrowed DATE NOT NULL, returned DATE, PRIMARY KEY (id));
            """;
        var schema = SqlImport.Read(sql);
        var places = new Dictionary<string, DPoint>
        {
            ["author"] = new(160, 120), ["book"] = new(470, 120), ["member"] = new(160, 380), ["loan"] = new(470, 380),
        };
        foreach (var t in schema.Tables)
            t.At = places.GetValueOrDefault(t.Name);
        var d = Mld.ToDiagram(schema, L.T("Library"), ErdModel.NotationCrowsFoot);
        return d;
    }
}
