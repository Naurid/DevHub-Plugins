using System.Text;
using System.Text.RegularExpressions;
using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Database.Core;

public enum SqlDialect { MySql, PostgreSql, SqlServer, Sqlite }

/// <summary>
/// Column types as students write them ("Entier", "Alphanumérique(50)", "VARCHAR(50)", "Date", "Booléen") mapped to each
/// database's own.
/// </summary>
public static partial class SqlTypes
{
    public static string DialectName(SqlDialect d) => d switch
    {
        SqlDialect.MySql => "MySQL",
        SqlDialect.PostgreSql => "PostgreSQL",
        SqlDialect.SqlServer => "SQL Server",
        _ => "SQLite",
    };

    /// <summary>A counter: an identifier the database numbers itself.</summary>
    public static bool IsCounter(string type) => Base(type).Name is "counter" or "compteur" or "serial" or "auto" or "autoincrement"
        or "auto_increment" or "numauto" or "numero_auto" or "identity" or "autoincrement_int";

    private static (string Name, string[] Args) Base(string type)
    {
        var m = TypePattern().Match(type.Trim());
        if (!m.Success)
            return (SqlNames.Identifier(type).ToLowerInvariant(), []);
        var args = m.Groups[2].Success ? m.Groups[2].Value.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToArray() : [];
        return (SqlNames.Identifier(m.Groups[1].Value).ToLowerInvariant(), args);
    }

    /// <summary>The length a type carries (VARCHAR(50) → "50"), for the data dictionary.</summary>
    public static string Size(string type) => string.Join(",", Base(type).Args);

    /// <summary>The generic name of a type for the data dictionary ("Entier" → INT, "Alphanumérique(50)" → VARCHAR).</summary>
    public static string Generic(string type) => Map(type, SqlDialect.MySql) is var t && t.IndexOf('(') is var i and >= 0 ? t[..i] : Map(type, SqlDialect.MySql);

    public static string Map(string type, SqlDialect d)
    {
        if (type.Trim().Length == 0)
            return d == SqlDialect.SqlServer ? "NVARCHAR(50)" : "VARCHAR(50)";
        var (name, args) = Base(type);
        string Args(string fallback) => "(" + (args.Length > 0 ? string.Join(", ", args) : fallback) + ")";
        return name switch
        {
            "int" or "integer" or "entier" or "counter" or "compteur" or "serial" or "auto" or "autoincrement" or "auto_increment"
                or "numauto" or "numero_auto" or "identity" or "nombre" or "number" when args.Length == 0 => d == SqlDialect.Sqlite ? "INTEGER" : "INT",
            "bigint" or "entier_long" or "long" => d == SqlDialect.Sqlite ? "INTEGER" : "BIGINT",
            "smallint" or "tinyint" or "entier_court" => d == SqlDialect.Sqlite ? "INTEGER" : "SMALLINT",
            "varchar" or "alphanumerique" or "alphanumeric" or "chaine" or "string" or "nvarchar" or "texte_court" or "caracteres" =>
                (d == SqlDialect.SqlServer ? "NVARCHAR" : "VARCHAR") + Args("255"),
            "char" or "caractere" or "nchar" => (d == SqlDialect.SqlServer ? "NCHAR" : "CHAR") + Args("1"),
            "text" or "texte" or "texte_long" or "memo" or "longtext" or "clob" => d == SqlDialect.SqlServer ? "NVARCHAR(MAX)" : "TEXT",
            "date" => "DATE",
            "datetime" or "timestamp" or "dateheure" or "date_heure" or "datetime2" or "horodatage" => d switch
            {
                SqlDialect.PostgreSql => "TIMESTAMP",
                SqlDialect.SqlServer => "DATETIME2",
                _ => "DATETIME",
            },
            "time" or "heure" => "TIME",
            "bool" or "boolean" or "booleen" or "logique" or "bit" or "oui_non" => d switch
            {
                SqlDialect.SqlServer => "BIT",
                SqlDialect.Sqlite => "INTEGER",
                _ => "BOOLEAN",
            },
            "decimal" or "numeric" or "monetaire" or "money" or "montant" or "nombre" or "number" => (d == SqlDialect.Sqlite ? "NUMERIC" : "DECIMAL") + Args("10, 2"),
            "float" or "real" or "reel" or "double" or "double_precision" => d switch
            {
                SqlDialect.PostgreSql => "DOUBLE PRECISION",
                SqlDialect.SqlServer => "FLOAT",
                SqlDialect.Sqlite => "REAL",
                _ => "DOUBLE",
            },
            "blob" or "binary" or "binaire" or "image" or "bytea" or "varbinary" => d switch
            {
                SqlDialect.PostgreSql => "BYTEA",
                SqlDialect.SqlServer => "VARBINARY(MAX)",
                _ => "BLOB",
            },
            "uuid" or "guid" or "uniqueidentifier" => d switch
            {
                SqlDialect.PostgreSql => "UUID",
                SqlDialect.SqlServer => "UNIQUEIDENTIFIER",
                SqlDialect.MySql => "CHAR(36)",
                _ => "TEXT",
            },
            _ => type.Trim().ToUpperInvariant(),
        };
    }

    [GeneratedRegex(@"^([^\(\)]+?)\s*(?:\((.*)\))?$")]
    private static partial Regex TypePattern();
}

/// <summary>Writes <c>CREATE TABLE</c> scripts.</summary>
public static class SqlWriter
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "order", "group", "table", "select", "from", "where", "key", "index", "desc", "asc", "by", "check", "column",
        "default", "references", "primary", "foreign", "unique", "values", "insert", "update", "delete", "join", "limit",
        "level", "range", "rank", "row", "rows", "comment", "position", "status", "type", "name", "date", "time", "year",
        "month", "day", "end", "case", "when", "to", "in", "is", "as", "on", "and", "or", "not", "null", "all", "any",
    };

    public static string Quote(string name, SqlDialect d)
    {
        if (Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") && !Reserved.Contains(name))
            return name;
        return d switch
        {
            SqlDialect.MySql => $"`{name.Replace("`", "``")}`",
            SqlDialect.SqlServer => $"[{name.Replace("]", "]]")}]",
            _ => $"\"{name.Replace("\"", "\"\"")}\"",
        };
    }

    public static string Write(Schema schema, SqlDialect d, string title = "")
    {
        var w = new StringBuilder();
        w.Append($"-- {(title.Length > 0 ? title + " — " : "")}{SqlTypes.DialectName(d)}\n");
        w.Append($"-- {L.T("Generated by DevHub Diagrams")}\n\n");
        string Q(string n) => Quote(n, d);
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferred = new List<string>();
        foreach (var table in schema.InDependencyOrder())
        {
            var lines = new List<string>();
            var key = table.PrimaryKey;
            var inlineKey = d == SqlDialect.Sqlite && key.Count == 1 && key[0].AutoIncrement;
            foreach (var c in table.Columns)
                lines.Add("    " + ColumnDefinition(c, d, inlineKey && c == key[0]));
            if (key.Count > 0 && !inlineKey)
                lines.Add($"    CONSTRAINT {Q("pk_" + table.Name)} PRIMARY KEY ({string.Join(", ", key.Select(c => Q(c.Name)))})");
            var n = 0;
            foreach (var fk in table.ForeignKeys(schema))
            {
                var target = schema.Table(fk.Table);
                var name = $"fk_{table.Name}_{fk.Table}" + (++n > 1 ? $"_{n}" : "");
                var targetColumns = fk.Columns.Select(c => c.RefColumn ?? target?.PrimaryKey.FirstOrDefault()?.Name ?? "id");
                var constraint = $"CONSTRAINT {Q(name)} FOREIGN KEY ({string.Join(", ", fk.Columns.Select(c => Q(c.Name)))}) " +
                                 $"REFERENCES {Q(target?.Name ?? fk.Table)} ({string.Join(", ", targetColumns.Select(Q))})";
                // SQLite takes forward references; the others need the target to exist first (cycles: ALTER TABLE after).
                if (d == SqlDialect.Sqlite || created.Contains(fk.Table) || string.Equals(fk.Table, table.Name, StringComparison.OrdinalIgnoreCase))
                    lines.Add("    " + constraint);
                else
                    deferred.Add($"ALTER TABLE {Q(table.Name)} ADD {constraint};");
            }
            if (table.Comment.Length > 0)
                w.Append($"-- {table.Comment.Replace("\n", " ")}\n");
            w.Append($"CREATE TABLE {Q(table.Name)} (\n{string.Join(",\n", lines)}\n);\n\n");
            created.Add(table.Name);
        }
        foreach (var line in deferred)
            w.Append(line).Append('\n');
        return w.ToString().TrimEnd() + "\n";
    }

    private static string ColumnDefinition(Column c, SqlDialect d, bool sqliteKey)
    {
        var w = new StringBuilder(Quote(c.Name, d)).Append(' ');
        if (sqliteKey)
            return w.Append("INTEGER PRIMARY KEY AUTOINCREMENT").ToString();
        var counter = c.AutoIncrement || SqlTypes.IsCounter(c.Type);
        w.Append(SqlTypes.Map(counter && SqlTypes.IsCounter(c.Type) ? "INT" : c.Type, d));
        if (counter && d != SqlDialect.Sqlite)
            w.Append(d switch
            {
                SqlDialect.MySql => " NOT NULL AUTO_INCREMENT",
                SqlDialect.PostgreSql => " GENERATED BY DEFAULT AS IDENTITY",
                _ => " IDENTITY(1,1) NOT NULL",
            });
        else if (c.NotNull || c.PrimaryKey)
            w.Append(" NOT NULL");
        if (c.Unique && !c.PrimaryKey)
            w.Append(" UNIQUE");
        if (c.Default is not null)
            w.Append(" DEFAULT ").Append(c.Default);
        return w.ToString();
    }
}

/// <summary>Reads <c>CREATE TABLE</c> (and <c>ALTER TABLE … ADD FOREIGN KEY</c>) statements back into tables.</summary>
public static partial class SqlImport
{
    public static Schema Read(string sql)
    {
        var schema = new Schema();
        foreach (var statement in Statements(StripComments(sql)))
        {
            var create = CreateTable().Match(statement);
            if (create.Success)
            {
                var table = new Table { Name = Column.Unquote(LastPart(create.Groups[1].Value)) };
                var open = statement.IndexOf('(', create.Index + create.Length - 1);
                var close = statement.LastIndexOf(')');
                if (open < 0 || close <= open)
                    continue;
                foreach (var item in SplitTop(statement[(open + 1)..close]))
                    AddItem(table, item);
                schema.Tables.Add(table);
                continue;
            }
            var alter = AlterForeignKey().Match(statement);
            if (alter.Success && schema.Table(Column.Unquote(LastPart(alter.Groups[1].Value))) is { } altered)
                ForeignKey(altered, Column.Names(alter.Groups[2].Value), alter.Groups[3].Value);
        }
        return schema;
    }

    private static void AddItem(Table table, string item)
    {
        var text = item.Trim();
        var constraint = ConstraintName().Match(text);
        if (constraint.Success)
            text = text[constraint.Length..].Trim();
        if (text.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in Column.Names(text[text.IndexOf('(')..]))
                if (table.Column(name) is { } c)
                    (c.PrimaryKey, c.NotNull) = (true, true);
            return;
        }
        var fk = TableForeignKey().Match(text);
        if (fk.Success)
        {
            ForeignKey(table, Column.Names(fk.Groups[1].Value), fk.Groups[2].Value);
            return;
        }
        if (text.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            var names = Column.Names(text[text.IndexOf('(')..]);
            if (names.Count == 1 && table.Column(names[0]) is { } c)
                c.Unique = true;
            return;
        }
        if (Regex.IsMatch(text, @"^(CHECK|INDEX|KEY|FULLTEXT|SPATIAL)\b", RegexOptions.IgnoreCase))
            return;
        if (Column.Parse(text.Replace("\n", " ")) is { } column)
            table.Columns.Add(column);
    }

    private static void ForeignKey(Table table, List<string> columns, string reference)
    {
        var (target, targetColumns) = Column.Reference(reference.Trim());
        target = Column.Unquote(LastPart(target));
        for (var i = 0; i < columns.Count; i++)
            if (table.Column(columns[i]) is { } c)
            {
                c.RefTable = target;
                c.RefColumn = i < targetColumns.Count ? targetColumns[i] : null;
            }
    }

    /// <summary>"dbo.client" → "client".</summary>
    private static string LastPart(string name)
    {
        var parts = name.Trim().Split('.');
        return parts[^1];
    }

    private static string StripComments(string sql)
    {
        sql = BlockComment().Replace(sql, " ");
        return string.Join("\n", sql.Replace("\r", "").Split('\n').Select(l =>
        {
            var i = l.IndexOf("--", StringComparison.Ordinal);
            var j = l.IndexOf('#');
            if (j >= 0 && (i < 0 || j < i) && !l.TrimStart().StartsWith("#", StringComparison.Ordinal))
                j = -1;
            var cut = i >= 0 ? i : j;
            return cut >= 0 ? l[..cut] : l;
        }));
    }

    private static IEnumerable<string> Statements(string sql)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < sql.Length; i++)
        {
            switch (sql[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ';' when depth <= 0:
                    yield return sql[start..i].Trim();
                    start = i + 1;
                    break;
            }
            if (i + 2 < sql.Length && depth <= 0 && sql[i] == '\n' && sql[(i + 1)..].TrimStart().StartsWith("GO", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(sql[(i + 1)..], @"^\s*GO\s*(\n|$)", RegexOptions.IgnoreCase))
            {
                yield return sql[start..i].Trim();
                var go = Regex.Match(sql[(i + 1)..], @"^\s*GO\s*", RegexOptions.IgnoreCase);
                start = i + 1 + go.Length;
                i = start - 1;
            }
        }
        if (start < sql.Length && sql[start..].Trim().Length > 0)
            yield return sql[start..].Trim();
    }

    private static List<string> SplitTop(string body)
    {
        var items = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '(')
                depth++;
            else if (body[i] == ')')
                depth--;
            else if (body[i] == ',' && depth == 0)
            {
                items.Add(body[start..i]);
                start = i + 1;
            }
        }
        items.Add(body[start..]);
        return items.Where(s => s.Trim().Length > 0).ToList();
    }

    [GeneratedRegex(@"^CREATE\s+(?:TEMP(?:ORARY)?\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([`""\[\]\w\.]+)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^ALTER\s+TABLE\s+(?:ONLY\s+)?([`""\[\]\w\.]+)\s+ADD\s+(?:CONSTRAINT\s+[`""\[\]\w]+\s+)?FOREIGN\s+KEY\s*(\([^\)]*\))\s*REFERENCES\s+(.+?)(?:\s+ON\s+.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AlterForeignKey();

    [GeneratedRegex(@"^CONSTRAINT\s+[`""\[\]\w]+\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ConstraintName();

    [GeneratedRegex(@"^FOREIGN\s+KEY\s*(\([^\)]*\))\s*REFERENCES\s+([`""\[\]\w\.]+\s*(?:\([^\)]*\))?)", RegexOptions.IgnoreCase)]
    private static partial Regex TableForeignKey();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();
}
