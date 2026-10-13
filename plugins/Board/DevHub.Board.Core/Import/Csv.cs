using System.Text;

namespace DevHub.Board.Core.Import;

/// <summary>CSV as RFC 4180 writes it: commas, quotes around fields with commas, quotes or line breaks, "" for a quote.</summary>
public static class Csv
{
    /// <summary>Reads every row (a header row is just the first one). Quoted fields may hold line breaks.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Read(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var i = 0;
        if (text.Length > 0 && text[0] == '﻿')
            i = 1;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Count > 1 || row[0].Length > 0)
                        rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Count > 1 || row[0].Length > 0)
                rows.Add(row);
        }
        return rows;
    }

    /// <summary>Writes rows; "\n" line ends; fields quoted only when they need it.</summary>
    public static string Write(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                    text.Append(',');
                var value = row[i] ?? "";
                if (value.IndexOfAny([',', '"', '\n', '\r']) >= 0)
                    text.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
                else
                    text.Append(value);
            }
            text.Append('\n');
        }
        return text.ToString();
    }
}
