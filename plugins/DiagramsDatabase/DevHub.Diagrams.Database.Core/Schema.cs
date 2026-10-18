using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DevHub.Diagrams.Core;

namespace DevHub.Diagrams.Database.Core;

/// <summary>
/// A column of a table. In an entity-relationship diagram each column is one line of text:
/// <c>PK id INT AI</c>, <c>FK client_id INT -> client.id</c>, <c>nom VARCHAR(50) NOT NULL</c>.
/// </summary>
public sealed class Column
{
    public string Name { get; set; } = "";

    /// <summary>As written ("VARCHAR(50)", "Entier", "DATE"); <see cref="SqlTypes"/> maps it per dialect.</summary>
    public string Type { get; set; } = "";

    public bool PrimaryKey { get; set; }

    /// <summary>Marked as a foreign key (FK or #) even without a target written.</summary>
    public bool Foreign { get; set; }

    public bool NotNull { get; set; }
    public bool Unique { get; set; }
    public bool AutoIncrement { get; set; }
    public string? RefTable { get; set; }
    public string? RefColumn { get; set; }
    public string? Default { get; set; }
    public string Comment { get; set; } = "";

    public bool IsForeign => Foreign || RefTable is not null;

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "PK", "FK", "UQ", "AI", "NN", "#" };

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOT", "NULL", "UNIQUE", "PRIMARY", "REFERENCES", "->", "DEFAULT", "AUTO_INCREMENT", "AUTOINCREMENT", "IDENTITY",
        "AI", "PK", "FK", "UQ", "NN", "CONSTRAINT", "CHECK", "COLLATE", "GENERATED",
    };

    /// <summary>Reads a column line; null for a blank or comment line.</summary>
    public static Column? Parse(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith("--", StringComparison.Ordinal))
            return null;
        var column = new Column();
        var comment = text.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
        {
            column.Comment = text[(comment + 2)..].Trim();
            text = text[..comment].Trim();
        }
        var tokens = Tokens(text);
        var i = 0;
        while (i < tokens.Count && Flags.Contains(tokens[i]))
            Flag(column, tokens[i++]);
        if (i >= tokens.Count)
            return null;
        var name = tokens[i++];
        if (name.StartsWith('#'))
        {
            column.Foreign = true;
            name = name[1..];
        }
        column.Name = Unquote(name.TrimEnd(':'));
        if (i < tokens.Count && tokens[i] == ":")
            i++;
        var type = new List<string>();
        while (i < tokens.Count && !Keywords.Contains(tokens[i]) && !tokens[i].StartsWith("IDENTITY(", StringComparison.OrdinalIgnoreCase))
            type.Add(tokens[i++]);
        column.Type = string.Join(" ", type);
        if (column.Type.Equals("SERIAL", StringComparison.OrdinalIgnoreCase))
        {
            column.Type = "INT";
            column.AutoIncrement = true;
        }
        while (i < tokens.Count)
        {
            var t = tokens[i++].ToUpperInvariant();
            switch (t)
            {
                case "NOT" when i < tokens.Count && tokens[i].Equals("NULL", StringComparison.OrdinalIgnoreCase):
                    column.NotNull = true;
                    i++;
                    break;
                case "UNIQUE":
                    column.Unique = true;
                    break;
                case "PRIMARY":
                    column.PrimaryKey = true;
                    if (i < tokens.Count && tokens[i].Equals("KEY", StringComparison.OrdinalIgnoreCase))
                        i++;
                    break;
                case "AUTO_INCREMENT" or "AUTOINCREMENT" or "IDENTITY":
                    column.AutoIncrement = true;
                    break;
                case "GENERATED":
                    // GENERATED { ALWAYS | BY DEFAULT } AS IDENTITY
                    column.AutoIncrement = true;
                    while (i < tokens.Count && !tokens[i - 1].StartsWith("IDENTITY", StringComparison.OrdinalIgnoreCase))
                        i++;
                    break;
                case "DEFAULT" when i < tokens.Count:
                    column.Default = tokens[i++];
                    break;
                case "->" when i < tokens.Count:
                    var target = tokens[i++];
                    var dot = target.LastIndexOf('.');
                    column.RefTable = Unquote(dot > 0 ? target[..dot] : target);
                    column.RefColumn = dot > 0 ? Unquote(target[(dot + 1)..]) : null;
                    break;
                case "REFERENCES" when i < tokens.Count:
                    var (table, columns) = Reference(tokens[i++]);
                    if (columns.Count == 0 && i < tokens.Count && tokens[i].StartsWith('('))
                        columns = Names(tokens[i++]);
                    column.RefTable = table;
                    column.RefColumn = columns.FirstOrDefault();
                    break;
                default:
                    if (t.StartsWith("IDENTITY(", StringComparison.Ordinal))
                        column.AutoIncrement = true;
                    else
                        Flag(column, t);
                    break;
            }
        }
        if (column.PrimaryKey)
            column.NotNull = true;
        return column;
    }

    private static void Flag(Column c, string flag)
    {
        switch (flag.ToUpperInvariant())
        {
            case "PK": c.PrimaryKey = true; break;
            case "FK" or "#": c.Foreign = true; break;
            case "UQ": c.Unique = true; break;
            case "AI": c.AutoIncrement = true; break;
            case "NN": c.NotNull = true; break;
        }
    }

    /// <summary>"client(id)" or "client" → the table and its columns.</summary>
    internal static (string Table, List<string> Columns) Reference(string text)
    {
        var open = text.IndexOf('(');
        return open < 0 ? (Unquote(text), []) : (Unquote(text[..open].Trim()), Names(text[open..]));
    }

    /// <summary>"(a, b)" → [a, b].</summary>
    internal static List<string> Names(string group) =>
        group.Trim().TrimStart('(').TrimEnd(')').Split(',').Select(s => Unquote(s.Trim())).Where(s => s.Length > 0).ToList();

    internal static string Unquote(string name) => name.Trim().Trim('`', '"', '[', ']', '\'');

    /// <summary>Words, with a parenthesised group kept with the word before it ("VARCHAR (50)" → "VARCHAR(50)").</summary>
    internal static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        void Flush()
        {
            if (current.Length > 0)
                tokens.Add(current.ToString());
            current.Clear();
        }
        foreach (var c in text)
        {
            if (c == '(')
            {
                if (depth == 0 && current.Length == 0 && tokens.Count > 0 && !Keywords.Contains(tokens[^1]))
                {
                    current.Append(tokens[^1]);
                    tokens.RemoveAt(tokens.Count - 1);
                }
                depth++;
                current.Append(c);
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
                current.Append(c);
            }
            else if (depth == 0 && (char.IsWhiteSpace(c) || c == ','))
                Flush();
            else if (depth == 0 && c == ':' )
            {
                Flush();
                tokens.Add(":");
            }
            else
                current.Append(c);
        }
        Flush();
        return tokens;
    }

    /// <summary>The line an entity-relationship diagram shows and <see cref="Parse"/> reads back.</summary>
    public string ToLine()
    {
        var w = new StringBuilder();
        if (PrimaryKey)
            w.Append("PK ");
        if (IsForeign)
            w.Append("FK ");
        w.Append(Name);
        if (Type.Length > 0)
            w.Append(' ').Append(Type);
        if (AutoIncrement)
            w.Append(" AI");
        if (NotNull && !PrimaryKey)
            w.Append(" NOT NULL");
        if (Unique && !PrimaryKey)
            w.Append(" UNIQUE");
        if (Default is not null)
            w.Append(" DEFAULT ").Append(Default);
        if (RefTable is not null)
            w.Append(" -> ").Append(RefTable).Append(RefColumn is null ? "" : "." + RefColumn);
        if (Comment.Length > 0)
            w.Append(" // ").Append(Comment);
        return w.ToString();
    }

    public Column Clone() => (Column)MemberwiseClone();
}

/// <summary>A table: its columns in order, and where it is drawn.</summary>
public sealed class Table
{
    public string Name { get; set; } = "";
    public List<Column> Columns { get; } = [];

    /// <summary>The MCD shape it comes from (entity or association), or its ERD shape.</summary>
    public string? NodeId { get; set; }

    /// <summary>Where its shape's centre is.</summary>
    public DPoint? At { get; set; }

    public string Comment { get; set; } = "";

    public IReadOnlyList<Column> PrimaryKey => Columns.Where(c => c.PrimaryKey).ToList();

    public Column? Column(string name) => Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The foreign keys, a composite one as one group (columns referencing a composite key, in key order).</summary>
    public IReadOnlyList<ForeignKey> ForeignKeys(Schema schema)
    {
        var keys = new List<ForeignKey>();
        foreach (var byTable in Columns.Where(c => c.RefTable is not null).GroupBy(c => c.RefTable!, StringComparer.OrdinalIgnoreCase))
        {
            var target = schema.Table(byTable.Key);
            var keySize = target?.PrimaryKey.Count ?? 1;
            var group = new List<Column>();
            foreach (var c in byTable)
            {
                // A new group starts when this target column is already in the current group (a second reference).
                if (keySize <= 1 || group.Any(g => string.Equals(g.RefColumn, c.RefColumn, StringComparison.OrdinalIgnoreCase)))
                {
                    if (group.Count > 0)
                        keys.Add(new ForeignKey(byTable.Key, group));
                    group = [];
                }
                group.Add(c);
            }
            if (group.Count > 0)
                keys.Add(new ForeignKey(byTable.Key, group));
        }
        return keys;
    }
}

/// <summary>Columns that reference another table's key together.</summary>
public sealed record ForeignKey(string Table, IReadOnlyList<Column> Columns)
{
    public bool Nullable => Columns.Any(c => !c.NotNull);
    public bool Unique => Columns.Count == 1 && Columns[0].Unique;
}

/// <summary>A set of tables: what an MLD is, what SQL creates.</summary>
public sealed class Schema
{
    public List<Table> Tables { get; } = [];

    public Table? Table(string name) => Tables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A name not taken yet ("patient", "patient_2" …).</summary>
    public string FreeName(string name)
    {
        var candidate = name;
        for (var i = 2; Table(candidate) is not null; i++)
            candidate = $"{name}_{i}";
        return candidate;
    }

    /// <summary>The tables with the ones they reference first (cycles keep their order).</summary>
    public IReadOnlyList<Table> InDependencyOrder()
    {
        var done = new List<Table>();
        var visiting = new HashSet<Table>();
        void Visit(Table t)
        {
            if (done.Contains(t) || !visiting.Add(t))
                return;
            foreach (var c in t.Columns.Where(c => c.RefTable is not null))
                if (Table(c.RefTable!) is { } target && target != t)
                    Visit(target);
            visiting.Remove(t);
            done.Add(t);
        }
        foreach (var t in Tables)
            Visit(t);
        return done;
    }
}

/// <summary>Names that work in SQL everywhere: no accents, no spaces.</summary>
public static partial class SqlNames
{
    public static string Identifier(string text)
    {
        var plain = new StringBuilder();
        foreach (var c in text.Trim().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                plain.Append(c);
        var name = NotWord().Replace(plain.ToString(), "_").Trim('_');
        name = Underscores().Replace(name, "_");
        if (name.Length == 0)
            return "x";
        return char.IsDigit(name[0]) ? "_" + name : name;
    }

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex NotWord();

    [GeneratedRegex("_{2,}")]
    private static partial Regex Underscores();
}
