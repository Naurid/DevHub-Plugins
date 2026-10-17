using System.Text;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core.Search;

/// <summary>A query that can't be read or run, with where in the text the problem is.</summary>
public sealed class QueryException(string message, int position) : Exception(message)
{
    /// <summary>Character index in the query text (0-based).</summary>
    public int Position { get; } = position;
}

/// <summary>A parsed query: an optional condition and an optional order.</summary>
public sealed record Query(QueryNode? Where, IReadOnlyList<QueryOrder> OrderBy)
{
    public static Query Everything { get; } = new(null, []);
}

public sealed record QueryOrder(string Field, bool Descending);

/// <summary>The condition tree.</summary>
public abstract record QueryNode
{
    public sealed record And(QueryNode Left, QueryNode Right) : QueryNode;
    public sealed record Or(QueryNode Left, QueryNode Right) : QueryNode;
    public sealed record Not(QueryNode Inner) : QueryNode;

    /// <summary><c>field op value</c>: = != &gt; &gt;= &lt; &lt;= ~ !~</summary>
    public sealed record Compare(string Field, string Op, QueryValue Value, int Position) : QueryNode;

    /// <summary><c>field [NOT] IN (a, b)</c></summary>
    public sealed record In(string Field, IReadOnlyList<QueryValue> Values, bool Negated, int Position) : QueryNode;

    /// <summary><c>field IS [NOT] EMPTY</c></summary>
    public sealed record Empty(string Field, bool Negated, int Position) : QueryNode;
}

/// <summary>A value: a word or quoted text, or a function call (<c>currentUser()</c>, <c>startOfWeek(-1)</c>).</summary>
public abstract record QueryValue
{
    public sealed record Literal(string Text) : QueryValue;
    public sealed record Function(string Name, IReadOnlyList<string> Arguments) : QueryValue;
}

/// <summary>
/// Reads the search language (docs/BOARD_PLAN.md: "a JQL-like query language"):
/// <code>
/// type = bug AND status != Done AND assignee = currentUser() ORDER BY priority DESC, created
/// labels in (ui, "needs design") OR summary ~ "login form"
/// sprint in openSprints() AND points is empty
/// </code>
/// Keywords (AND, OR, NOT, IN, IS, EMPTY, NULL, ORDER BY, ASC, DESC) are case-insensitive; field names too.
/// </summary>
public static class QueryParser
{
    public static Query Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Query.Everything;
        var parser = new Parser(Tokenize(text), text.Length);
        return parser.ParseQuery();
    }

    /// <summary>Parses, or returns the error instead of throwing.</summary>
    public static bool TryParse(string? text, out Query query, out QueryException? error)
    {
        try
        {
            query = Parse(text);
            error = null;
            return true;
        }
        catch (QueryException e)
        {
            query = Query.Everything;
            error = e;
            return false;
        }
    }

    /// <summary>Quotes a value when it needs it ("In Progress" → <c>"In Progress"</c>, ui → ui).</summary>
    public static string Quote(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-') && !IsKeyword(value)
            ? value
            : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static bool IsKeyword(string word) =>
        word.ToUpperInvariant() is "AND" or "OR" or "NOT" or "IN" or "IS" or "EMPTY" or "NULL" or "ORDER" or "BY" or "ASC" or "DESC";

    // ---- Tokens -------------------------------------------------------------------------------------------

    internal enum Kind { Word, Text, Op, Open, Close, Comma, End }

    internal readonly record struct Token(Kind Kind, string Value, int Position)
    {
        public bool IsKeyword(string keyword) => Kind == Kind.Word && string.Equals(Value, keyword, StringComparison.OrdinalIgnoreCase);
    }

    internal static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            var start = i;
            switch (c)
            {
                case '(':
                    tokens.Add(new Token(Kind.Open, "(", i++));
                    continue;
                case ')':
                    tokens.Add(new Token(Kind.Close, ")", i++));
                    continue;
                case ',':
                    tokens.Add(new Token(Kind.Comma, ",", i++));
                    continue;
                case '"' or '\'':
                    tokens.Add(new Token(Kind.Text, ReadString(text, ref i, c), start));
                    continue;
                case '=' or '~':
                    tokens.Add(new Token(Kind.Op, c.ToString(), i++));
                    continue;
                case '!' when i + 1 < text.Length && text[i + 1] is '=' or '~':
                    tokens.Add(new Token(Kind.Op, text.Substring(i, 2), i));
                    i += 2;
                    continue;
                case '<' or '>':
                    var op = i + 1 < text.Length && text[i + 1] == '=' ? text.Substring(i, 2) : c.ToString();
                    tokens.Add(new Token(Kind.Op, op, i));
                    i += op.Length;
                    continue;
            }
            if (IsWordChar(c) || (c is '-' or '+' && i + 1 < text.Length && IsWordChar(text[i + 1])))
            {
                i++;
                while (i < text.Length && IsWordChar(text[i]))
                    i++;
                tokens.Add(new Token(Kind.Word, text[start..i], start));
                continue;
            }
            throw new QueryException(L.T($"Unexpected \"{c}\"."), i);
        }
        tokens.Add(new Token(Kind.End, "", text.Length));
        return tokens;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or '@' or ':' or '/' or '+';

    private static string ReadString(string text, ref int i, char quote)
    {
        var start = i;
        var value = new StringBuilder();
        i++;
        while (i < text.Length && text[i] != quote)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
                i++;
            value.Append(text[i]);
            i++;
        }
        if (i >= text.Length)
            throw new QueryException(L.T("A quote isn't closed."), start);
        i++;
        return value.ToString();
    }

    // ---- Grammar ------------------------------------------------------------------------------------------

    private sealed class Parser(List<Token> tokens, int length)
    {
        private int _index;

        private Token Peek => tokens[_index];
        private Token Next() => tokens[_index++];

        public Query ParseQuery()
        {
            QueryNode? where = null;
            if (!Peek.IsKeyword("ORDER"))
                where = ParseOr();
            var order = new List<QueryOrder>();
            if (Peek.IsKeyword("ORDER"))
            {
                Next();
                Expect("BY");
                do
                {
                    var field = ExpectField();
                    var descending = false;
                    if (Peek.IsKeyword("DESC"))
                    {
                        Next();
                        descending = true;
                    }
                    else if (Peek.IsKeyword("ASC"))
                    {
                        Next();
                    }
                    order.Add(new QueryOrder(field, descending));
                }
                while (TryTake(Kind.Comma));
            }
            if (Peek.Kind != Kind.End)
                throw new QueryException(L.T($"Expected AND, OR or ORDER BY, not \"{Peek.Value}\"."), Peek.Position);
            return new Query(where, order);
        }

        private QueryNode ParseOr()
        {
            var left = ParseAnd();
            while (Peek.IsKeyword("OR"))
            {
                Next();
                left = new QueryNode.Or(left, ParseAnd());
            }
            return left;
        }

        private QueryNode ParseAnd()
        {
            var left = ParseNot();
            while (Peek.IsKeyword("AND"))
            {
                Next();
                left = new QueryNode.And(left, ParseNot());
            }
            return left;
        }

        private QueryNode ParseNot()
        {
            if (Peek.IsKeyword("NOT"))
            {
                Next();
                return new QueryNode.Not(ParseNot());
            }
            if (TryTake(Kind.Open))
            {
                var inner = ParseOr();
                if (!TryTake(Kind.Close))
                    throw new QueryException(L.T("A \"(\" isn't closed."), Peek.Position);
                return inner;
            }
            return ParseClause();
        }

        private QueryNode ParseClause()
        {
            var position = Peek.Position;
            var field = ExpectField();

            if (Peek.IsKeyword("IS"))
            {
                Next();
                var negated = false;
                if (Peek.IsKeyword("NOT"))
                {
                    Next();
                    negated = true;
                }
                if (!(Peek.IsKeyword("EMPTY") || Peek.IsKeyword("NULL")))
                    throw new QueryException(L.T("Expected EMPTY after IS."), Peek.Position);
                Next();
                return new QueryNode.Empty(field, negated, position);
            }

            if (Peek.IsKeyword("NOT") || Peek.IsKeyword("IN"))
            {
                var negated = Peek.IsKeyword("NOT");
                if (negated)
                    Next();
                Expect("IN");
                return new QueryNode.In(field, ParseList(), negated, position);
            }

            if (Peek.Kind != Kind.Op)
                throw new QueryException(L.T($"Expected an operator (=, !=, ~, <, >, IN, IS) after \"{field}\"."), Peek.Position);
            var op = Next().Value;
            if (Peek.IsKeyword("EMPTY") || Peek.IsKeyword("NULL"))
            {
                if (op is not ("=" or "!="))
                    throw new QueryException(L.T("EMPTY only goes with = or !=."), Peek.Position);
                Next();
                return new QueryNode.Empty(field, op == "!=", position);
            }
            return new QueryNode.Compare(field, op, ParseValue(), position);
        }

        private IReadOnlyList<QueryValue> ParseList()
        {
            if (Peek.Kind == Kind.Word && Peek.Value.Length > 0 && tokens[_index + 1].Kind == Kind.Open)
                return [ParseValue()];   // sprint in openSprints()
            if (!TryTake(Kind.Open))
                throw new QueryException(L.T("Expected \"(\" after IN."), Peek.Position);
            var values = new List<QueryValue>();
            if (Peek.Kind != Kind.Close)
            {
                do
                    values.Add(ParseValue());
                while (TryTake(Kind.Comma));
            }
            if (!TryTake(Kind.Close))
                throw new QueryException(L.T("Expected \",\" or \")\" in the list."), Peek.Position);
            if (values.Count == 0)
                throw new QueryException(L.T("The list is empty."), Peek.Position);
            return values;
        }

        private QueryValue ParseValue()
        {
            var token = Peek;
            if (token.Kind == Kind.Text)
            {
                Next();
                return new QueryValue.Literal(token.Value);
            }
            if (token.Kind != Kind.Word)
                throw new QueryException(token.Kind == Kind.End ? L.T("A value is missing at the end.") : L.T($"Expected a value, not \"{token.Value}\"."), token.Position);
            Next();
            if (TryTake(Kind.Open))
            {
                var args = new List<string>();
                if (Peek.Kind != Kind.Close)
                {
                    do
                    {
                        var arg = Next();
                        if (arg.Kind is not (Kind.Word or Kind.Text))
                            throw new QueryException(L.T("Expected an argument."), arg.Position);
                        args.Add(arg.Value);
                    }
                    while (TryTake(Kind.Comma));
                }
                if (!TryTake(Kind.Close))
                    throw new QueryException(L.T($"\"{token.Value}(\" isn't closed."), Peek.Position);
                return new QueryValue.Function(token.Value, args);
            }
            return new QueryValue.Literal(token.Value);
        }

        private string ExpectField()
        {
            var token = Next();
            if (token.Kind is Kind.Word or Kind.Text && !(token.Kind == Kind.Word && IsKeyword(token.Value)))
                return token.Value;
            throw new QueryException(token.Kind == Kind.End ? L.T("A field name is missing at the end.") : L.T($"Expected a field name, not \"{token.Value}\"."),
                token.Kind == Kind.End ? length : token.Position);
        }

        private void Expect(string keyword)
        {
            if (!Peek.IsKeyword(keyword))
                throw new QueryException(L.T($"Expected {keyword}."), Peek.Position);
            Next();
        }

        private bool TryTake(Kind kind)
        {
            if (Peek.Kind != kind)
                return false;
            Next();
            return true;
        }
    }
}
