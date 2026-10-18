using DevHub.Diagrams.Core;
using DevHub.Diagrams.Database.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DevHub.Diagrams.Database.Core.Tests;

public class DatabaseTests
{
    private static readonly MeriseModel Merise = new();

    private static DiagramNode Entity(Diagram d, string name, string identifier, string properties = "", double x = 0)
    {
        var n = Merise.NewNode(d, MeriseModel.Entity, new DPoint(x, 0), name);
        n.Props["identifier"] = identifier;
        n.Props["properties"] = properties;
        d.Nodes.Add(n);
        return n;
    }

    private static DiagramNode Association(Diagram d, string verb, string properties = "")
    {
        var n = Merise.NewNode(d, MeriseModel.Association, new DPoint(0, 200), verb);
        n.Props["properties"] = properties;
        d.Nodes.Add(n);
        return n;
    }

    private static DiagramEdge Leg(Diagram d, DiagramNode entity, DiagramNode association, string card, string role = "")
    {
        var e = Merise.NewEdge(d, MeriseModel.Leg, entity, association)!;
        e.Props["card"] = card;
        if (role.Length > 0)
            e.Props["role"] = role;
        d.Edges.Add(e);
        return e;
    }

    /// <summary>Client 0,n — passer — 1,1 Commande 1,n — contenir (quantite) — 0,n Produit.</summary>
    private static Diagram Shop()
    {
        var d = new Diagram { Kind = MeriseModel.KindId, Title = "Boutique" };
        var client = Entity(d, "Client", "numClient : Compteur", "nom : Alphanumérique(50) // Nom du client\nemail : VARCHAR(120)");
        var commande = Entity(d, "Commande", "numCommande : Entier", "dateCommande : Date", 300);
        var produit = Entity(d, "Produit", "refProduit : CHAR(8)", "libellé : Texte\nprix : Monétaire", 600);
        var passer = Association(d, "passer");
        Leg(d, client, passer, "0,n");
        Leg(d, commande, passer, "1,1");
        var contenir = Association(d, "contenir", "quantité : Entier");
        Leg(d, commande, contenir, "1,n");
        Leg(d, produit, contenir, "0,n");
        return d;
    }

    [Fact]
    public void Column_lines_are_read_and_written_back()
    {
        var c = Column.Parse("FK client_id INT NOT NULL -> client.id // who ordered")!;
        Assert.True(c.IsForeign);
        Assert.True(c.NotNull);
        Assert.Equal(("client", "id"), (c.RefTable, c.RefColumn));
        Assert.Equal("who ordered", c.Comment);
        Assert.Equal(c.ToLine(), Column.Parse(c.ToLine())!.ToLine());

        var sql = Column.Parse("prix DECIMAL (10, 2) DEFAULT 0 REFERENCES tarif (code)")!;
        Assert.Equal("DECIMAL(10, 2)", sql.Type);
        Assert.Equal("0", sql.Default);
        Assert.Equal(("tarif", "code"), (sql.RefTable, sql.RefColumn));

        var key = Column.Parse("PK id INT AI")!;
        Assert.True(key.PrimaryKey && key.AutoIncrement && key.NotNull);
        Assert.Null(Column.Parse("   "));
    }

    [Fact]
    public void Course_types_map_to_each_database()
    {
        Assert.Equal("VARCHAR(50)", SqlTypes.Map("Alphanumérique(50)", SqlDialect.MySql));
        Assert.Equal("NVARCHAR(50)", SqlTypes.Map("Alphanumérique(50)", SqlDialect.SqlServer));
        Assert.Equal("INTEGER", SqlTypes.Map("Entier", SqlDialect.Sqlite));
        Assert.Equal("BIT", SqlTypes.Map("Booléen", SqlDialect.SqlServer));
        Assert.Equal("TIMESTAMP", SqlTypes.Map("DateHeure", SqlDialect.PostgreSql));
        Assert.Equal("DECIMAL(10, 2)", SqlTypes.Map("Monétaire", SqlDialect.MySql));
        Assert.Equal("NVARCHAR(MAX)", SqlTypes.Map("Texte", SqlDialect.SqlServer));
        Assert.True(SqlTypes.IsCounter("Compteur"));
        Assert.Equal("Libelle_du_produit", SqlNames.Identifier("Libellé du produit"));
    }

    [Fact]
    public void One_to_many_puts_a_foreign_key_and_many_to_many_makes_a_table()
    {
        var schema = Mld.ToSchema(Shop());
        Assert.Equal(["Client", "Commande", "Produit", "contenir"], schema.Tables.Select(t => t.Name));

        var commande = schema.Table("Commande")!;
        var fk = commande.Column("numClient")!;
        Assert.Equal(("Client", "numClient"), (fk.RefTable, fk.RefColumn));
        Assert.True(fk.NotNull);    // 1,1: every order has its client
        Assert.False(fk.PrimaryKey);

        var client = schema.Table("Client")!;
        Assert.True(client.Column("numClient")!.AutoIncrement);
        Assert.Equal("Nom du client", client.Column("nom")!.Comment);

        var contenir = schema.Table("contenir")!;
        Assert.Equal(["numCommande", "refProduit"], contenir.PrimaryKey.Select(c => c.Name));
        Assert.All(contenir.PrimaryKey, c => Assert.True(c.IsForeign));
        Assert.Equal("CHAR(8)", contenir.Column("refProduit")!.Type);
        Assert.NotNull(contenir.Column("quantite"));
    }

    [Fact]
    public void Optional_leg_makes_a_nullable_key_and_one_to_one_a_unique_one()
    {
        var d = new Diagram { Kind = MeriseModel.KindId };
        var personne = Entity(d, "Personne", "idPersonne");
        var badge = Entity(d, "Badge", "numBadge");
        var service = Entity(d, "Service", "codeService : CHAR(4)");
        var porter = Association(d, "porter");
        Leg(d, personne, porter, "0,1");
        Leg(d, badge, porter, "1,1");
        var travailler = Association(d, "travailler", "depuis : Date");
        Leg(d, personne, travailler, "0,1");
        Leg(d, service, travailler, "0,n");

        var schema = Mld.ToSchema(d);
        var key = schema.Table("Badge")!.Column("idPersonne")!;     // 1,1 side holds it
        Assert.True(key.Unique);
        Assert.True(key.NotNull);
        var job = schema.Table("Personne")!.Column("codeService")!;
        Assert.False(job.NotNull);
        Assert.NotNull(schema.Table("Personne")!.Column("depuis"));  // the association's property moves with the key
        Assert.Equal(3, schema.Tables.Count);
    }

    [Fact]
    public void Reflexive_associations_name_their_keys_by_role()
    {
        var d = new Diagram { Kind = MeriseModel.KindId };
        var employe = Entity(d, "Employé", "matricule");
        var diriger = Association(d, "diriger");
        Leg(d, employe, diriger, "0,1", "chef");
        Leg(d, employe, diriger, "0,n");
        var remplacer = Association(d, "remplacer");
        Leg(d, employe, remplacer, "0,n", "titulaire");
        Leg(d, employe, remplacer, "0,n", "remplacant");

        var schema = Mld.ToSchema(d);
        var table = schema.Table("Employe")!;
        Assert.Equal("Employe", table.Column(table.Columns[1].Name)!.RefTable);
        Assert.Equal(2, table.Columns.Count);
        var junction = schema.Table("remplacer")!;
        Assert.Equal(["titulaire_matricule", "remplacant_matricule"], junction.PrimaryKey.Select(c => c.Name));
        Assert.Equal(2, junction.ForeignKeys(schema).Count);
    }

    [Fact]
    public void Sqlite_script_runs_and_enforces_the_keys()
    {
        var sql = SqlWriter.Write(Mld.ToSchema(Shop()), SqlDialect.Sqlite);
        SQLitePCL.Batteries.Init();
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        void Run(string text)
        {
            using var command = db.CreateCommand();
            command.CommandText = text;
            command.ExecuteNonQuery();
        }
        Run("PRAGMA foreign_keys = ON;");
        Run(sql);
        Run("INSERT INTO Client (nom, email) VALUES ('Ana', 'a@b.c');");
        Run("INSERT INTO Commande (numCommande, dateCommande, numClient) VALUES (1, '2026-10-18', 1);");
        Run("INSERT INTO Produit (refProduit, libelle, prix) VALUES ('P1', 'Stylo', 1.5);");
        Run("INSERT INTO contenir (numCommande, refProduit, quantite) VALUES (1, 'P1', 3);");
        Assert.Throws<SqliteException>(() => Run("INSERT INTO Commande (numCommande, dateCommande, numClient) VALUES (2, '2026-10-18', 99);"));
        Assert.Throws<SqliteException>(() => Run("INSERT INTO contenir (numCommande, refProduit, quantite) VALUES (1, 'P1', 4);"));
    }

    [Fact]
    public void Other_dialects_write_their_own_counters_and_quotes()
    {
        var schema = Mld.ToSchema(Shop());
        Assert.Contains("numClient INT NOT NULL AUTO_INCREMENT", SqlWriter.Write(schema, SqlDialect.MySql));
        Assert.Contains("numClient INT GENERATED BY DEFAULT AS IDENTITY", SqlWriter.Write(schema, SqlDialect.PostgreSql));
        var server = SqlWriter.Write(schema, SqlDialect.SqlServer);
        Assert.Contains("numClient INT IDENTITY(1,1) NOT NULL", server);
        Assert.Contains("nom NVARCHAR(50)", server);
        Assert.Contains("CONSTRAINT pk_contenir PRIMARY KEY (numCommande, refProduit)", server);
        Assert.Equal("[order]", SqlWriter.Quote("order", SqlDialect.SqlServer));
        // Referenced tables come first, so no ALTER TABLE is needed here.
        Assert.DoesNotContain("ALTER TABLE", server);
        Assert.True(server.IndexOf("CREATE TABLE Client", StringComparison.Ordinal) < server.IndexOf("CREATE TABLE Commande", StringComparison.Ordinal));
    }

    [Fact]
    public void Scripts_import_back_into_the_same_tables()
    {
        var schema = Mld.ToSchema(Shop());
        foreach (var dialect in Enum.GetValues<SqlDialect>())
        {
            var back = SqlImport.Read(SqlWriter.Write(schema, dialect));
            Assert.Equal(schema.Tables.Select(t => t.Name).Order(), back.Tables.Select(t => t.Name).Order());
            foreach (var t in schema.Tables)
            {
                var b = back.Table(t.Name)!;
                Assert.Equal(t.Columns.Select(c => c.Name), b.Columns.Select(c => c.Name));
                Assert.Equal(t.PrimaryKey.Select(c => c.Name), b.PrimaryKey.Select(c => c.Name));
                Assert.Equal(t.Columns.Select(c => c.RefTable), b.Columns.Select(c => c.RefTable));
            }
        }
    }

    [Fact]
    public void Hand_written_scripts_import_with_their_dialect_quirks()
    {
        const string sql = """
            /* SQL Server */
            CREATE TABLE [dbo].[Auteur] (
                [id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                [nom] NVARCHAR(80) NOT NULL -- family name
            )
            GO
            CREATE TABLE Livre (
                isbn CHAR(13) NOT NULL,
                titre VARCHAR(200),
                auteur_id INT,
                prix DECIMAL(6, 2) DEFAULT 0,
                CONSTRAINT pk_livre PRIMARY KEY (isbn),
                UNIQUE (titre)
            );
            ALTER TABLE Livre ADD CONSTRAINT fk_livre_auteur FOREIGN KEY (auteur_id) REFERENCES Auteur (id) ON DELETE CASCADE;
            """;
        var schema = SqlImport.Read(sql);
        Assert.Equal(2, schema.Tables.Count);
        var auteur = schema.Table("Auteur")!;
        Assert.True(auteur.Column("id")!.AutoIncrement);
        Assert.Equal("NVARCHAR(80)", auteur.Column("nom")!.Type);
        var livre = schema.Table("Livre")!;
        Assert.Equal("isbn", livre.PrimaryKey.Single().Name);
        Assert.True(livre.Column("titre")!.Unique);
        Assert.Equal("DECIMAL(6, 2)", livre.Column("prix")!.Type);
        Assert.Equal(("Auteur", "id"), (livre.Column("auteur_id")!.RefTable, livre.Column("auteur_id")!.RefColumn));
    }

    [Fact]
    public void Imported_tables_rebuild_an_mcd_with_the_junction_as_an_association()
    {
        var mcd = Mld.ToMcd(SqlImport.Read(SqlWriter.Write(Mld.ToSchema(Shop()), SqlDialect.PostgreSql)), "Back");
        Assert.Equal(3, mcd.Nodes.Count(n => n.Type == MeriseModel.Entity));
        var associations = mcd.Nodes.Where(n => n.Type == MeriseModel.Association).ToList();
        Assert.Equal(2, associations.Count);
        var contenir = associations.Single(a => a.Text == "contenir");
        Assert.Contains("quantite", contenir.Prop("properties"));
        Assert.DoesNotContain(Merise.Check(mcd), p => p.Severity == ProblemSeverity.Error);
        // And forth again: the same tables.
        Assert.Equal(["Client", "Commande", "Produit", "contenir"], Mld.ToSchema(mcd).Tables.Select(t => t.Name));
    }

    [Fact]
    public void Mld_diagram_keeps_the_tables_and_draws_one_line_per_key()
    {
        var schema = Mld.ToSchema(Shop());
        var mld = Mld.ToDiagram(schema, "MLD");
        Assert.Equal(ErdModel.KindId, mld.Kind);
        Assert.Equal(4, mld.Nodes.Count);
        Assert.Equal(3, mld.Edges.Count);
        var back = ErdModel.ToSchema(mld);
        Assert.Equal(SqlWriter.Write(schema, SqlDialect.MySql), SqlWriter.Write(back, SqlDialect.MySql));
        Assert.Empty(new ErdModel().Check(mld));
        Assert.Contains("Commande (_numCommande_, dateCommande, #numClient)", MldTextExporter.Write(schema));
    }

    [Fact]
    public void Erd_foreign_keys_follow_drawn_relationships()
    {
        var erd = new ErdModel();
        var d = erd.Create("ERD");
        var client = d.Nodes[0];
        client.Text = "client";
        var order = erd.NewNode(d, ErdModel.Table, new DPoint(400, 0), "orders");
        order.Props["columns"] = "PK id INT AI\nFK client_id INT\ntotal DECIMAL(8,2)";
        d.Nodes.Add(order);
        Assert.Contains(erd.Check(d), p => p.Message.Contains("client_id"));
        d.Edges.Add(erd.NewEdge(d, ErdModel.Relationship, order, client)!);
        var schema = ErdModel.ToSchema(d);
        Assert.Equal(("client", "id"), (schema.Table("orders")!.Column("client_id")!.RefTable, schema.Table("orders")!.Column("client_id")!.RefColumn));
        Assert.Empty(erd.Check(d));
        Assert.Contains("orders }o--|| client", new ErdMermaid().Export(d));
    }

    [Fact]
    public void Merise_rules_catch_the_usual_mistakes()
    {
        var d = new Diagram { Kind = MeriseModel.KindId };
        var a = Entity(d, "A", "");
        var b = Entity(d, "B", "idB");
        var lone = Association(d, "seul");
        Leg(d, a, lone, "2,n");
        var problems = Merise.Check(d);
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Warning && p.ElementIds.Contains(a.Id));       // no identifier
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Error && p.ElementIds.Contains(lone.Id));      // one leg
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Error && p.Message.Contains("0,1"));           // 2,n
        Assert.Contains(problems, p => p.Severity == ProblemSeverity.Info && p.ElementIds.Contains(b.Id));         // linked to nothing
    }

    [Fact]
    public void Data_dictionary_lists_every_property()
    {
        var csv = new DataDictionaryExporter().Export(Shop()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 3 + 2 + 3 + 1, csv.Length);
        Assert.Contains(csv, l => l.StartsWith("nom,Nom du client,VARCHAR,50,", StringComparison.Ordinal));
        Assert.Contains(csv, l => l.StartsWith("refProduit,,CHAR,8,Identifier,", StringComparison.Ordinal));
    }
}

public class DatabaseSampleTests
{
    [Fact]
    public void Merise_sample_generates_tables_sqlite_accepts()
    {
        var model = new MeriseModel();
        var mcd = model.Samples.Single().Create();
        Assert.Empty(model.Check(mcd));
        var schema = Mld.ToSchema(mcd);
        Assert.Equal(4, schema.Tables.Count);       // Patient, Doctor, Department, consults
        Assert.NotNull(schema.Table("Doctor")!.Column("departmentCode"));
        SQLitePCL.Batteries.Init();
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = SqlWriter.Write(schema, SqlDialect.Sqlite);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Erd_sample_has_its_relationships()
    {
        var model = new ErdModel();
        var erd = model.Samples.Single().Create();
        Assert.Equal(4, erd.Nodes.Count);
        Assert.Equal(3, erd.Edges.Count);
        Assert.Empty(model.Check(erd));
    }
}
