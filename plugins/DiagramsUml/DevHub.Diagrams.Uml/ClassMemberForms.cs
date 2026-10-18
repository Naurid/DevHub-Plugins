using DevHub.Diagrams.Core;
using DevHub.Diagrams.Uml.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Uml;

/// <summary>
/// Class attributes and operations as small forms: a drop-down for the visibility, the name, the type (with
/// suggestions), the default value or the parameters, check boxes for static and abstract. Stored as the same text
/// lines the notation uses (<c>+ static name(arg : int) : Type = default</c>), so exports and rules are unchanged.
/// </summary>
public sealed class ClassMemberCodec(bool operations) : IRowCodec
{
    private const string Visibility = "vis";
    private const string Name = "name";
    private const string Parameters = "params";
    private const string Type = "type";
    private const string Default = "default";
    private const string Static = "static";
    private const string Abstract = "abstract";

    /// <summary>Types offered while typing: the common ones, then the diagram's classes, interfaces, enumerations.</summary>
    public static IReadOnlyList<string> TypeSuggestions(Diagram d) =>
    [
        .. d.Nodes.Where(n => n.Type is ClassModel.Class or ClassModel.Interface or ClassModel.Enumeration or ClassModel.DataType)
            .Select(n => n.Text.Trim()).Where(t => t.Length > 0).Distinct().Order(StringComparer.CurrentCultureIgnoreCase),
        "int", "float", "double", "boolean", "String", "char", "long", "Date", "Time", "DateTime", "decimal", "void",
        "List<T>", "Map<K, V>",
    ];

    public IReadOnlyList<RowColumn> Columns { get; } = operations
        ?
        [
            VisibilityColumn(),
            new(Name, L.T("Name"), FieldKind.Text, 150) { Placeholder = L.T("name") },
            new(Parameters, L.T("Parameters"), FieldKind.Text, 230) { Placeholder = L.T("arg : Type, other : Type") },
            new(Type, L.T("Return type"), FieldKind.Text, 140) { Placeholder = L.T("return type"), Suggestions = TypeSuggestions },
            new(Static, L.T("Static"), FieldKind.Toggle),
            new(Abstract, L.T("Abstract"), FieldKind.Toggle),
        ]
        :
        [
            VisibilityColumn(),
            new(Name, L.T("Name"), FieldKind.Text, 150) { Placeholder = L.T("name") },
            new(Type, L.T("Type"), FieldKind.Text, 140) { Placeholder = L.T("type"), Suggestions = TypeSuggestions },
            new(Default, L.T("Default value"), FieldKind.Text, 90) { Placeholder = L.T("= default") },
            new(Static, L.T("Static"), FieldKind.Toggle),
            new(Abstract, L.T("Abstract"), FieldKind.Toggle),
        ];

    private static RowColumn VisibilityColumn() => new(Visibility, L.T("Visibility"), FieldKind.Choice)
    {
        Choices =
        [
            new("+", L.T("+ public")), new("-", L.T("- private")), new("#", L.T("# protected")), new("~", L.T("~ package")),
            new("", L.T("(none)")),
        ],
    };

    public string AddText => operations ? L.T("Add Operation") : L.T("Add Attribute");

    public IReadOnlyDictionary<string, string> NewRow() => operations
        ? new Dictionary<string, string> { [Visibility] = "+", [Name] = L.T("operation"), [Type] = "void" }
        : new Dictionary<string, string> { [Visibility] = "-", [Name] = L.T("attribute"), [Type] = "String" };

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string text)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var line in ClassMember.Lines(text))
        {
            if (ClassMember.Parse(line) is not { } m)
            {
                rows.Add(new Dictionary<string, string> { [Name] = line.Trim() });
                continue;
            }
            rows.Add(new Dictionary<string, string>
            {
                [Visibility] = m.Visibility == '\0' ? "" : m.Visibility.ToString(),
                [Name] = m.Name,
                [Parameters] = m.Parameters ?? "",
                [Type] = m.Type,
                [Default] = m.Default,
                [Static] = m.IsStatic ? "true" : "",
                [Abstract] = m.IsAbstract ? "true" : "",
            });
        }
        return rows;
    }

    public string Format(IEnumerable<IReadOnlyDictionary<string, string>> rows) => string.Join("\n", rows
        .Where(r => r.GetValueOrDefault(Name, "").Trim().Length > 0)
        .Select(r => new ClassMember(
            r.GetValueOrDefault(Visibility, "") is { Length: > 0 } v ? v[0] : '\0',
            r[Name].Trim(),
            operations ? r.GetValueOrDefault(Parameters, "") : null,
            r.GetValueOrDefault(Type, ""),
            operations ? "" : r.GetValueOrDefault(Default, ""),
            r.GetValueOrDefault(Static) == "true",
            r.GetValueOrDefault(Abstract) == "true").ToLine()));
}

/// <summary>An object's values (<c>attribute = value</c>) as rows of two boxes.</summary>
public sealed class ObjectValueCodec : IRowCodec
{
    private const string Name = "name";
    private const string Value = "value";

    public IReadOnlyList<RowColumn> Columns { get; } =
    [
        new(Name, L.T("Attribute"), FieldKind.Text, 120) { Placeholder = L.T("attribute") },
        new(Value, L.T("Value"), FieldKind.Text, 120) { Placeholder = L.T("value") },
    ];

    public string AddText => L.T("Add Value");

    public IReadOnlyDictionary<string, string> NewRow() => new Dictionary<string, string> { [Name] = L.T("attribute"), [Value] = "" };

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string text) => ClassMember.Lines(text)
        .Select(line => line.IndexOf('=') is var i and >= 0
            ? new Dictionary<string, string> { [Name] = line[..i].Trim(), [Value] = line[(i + 1)..].Trim() }
            : new Dictionary<string, string> { [Name] = line.Trim(), [Value] = "" })
        .ToList<IReadOnlyDictionary<string, string>>();

    public string Format(IEnumerable<IReadOnlyDictionary<string, string>> rows) => string.Join("\n", rows
        .Where(r => r.GetValueOrDefault(Name, "").Trim().Length > 0)
        .Select(r => r.GetValueOrDefault(Value, "").Trim() is { Length: > 0 } value ? $"{r[Name].Trim()} = {value}" : r[Name].Trim()));
}
