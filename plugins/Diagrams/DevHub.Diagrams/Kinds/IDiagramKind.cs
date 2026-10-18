using DevHub.Diagrams.Core;
using DevHub.Diagrams.Rendering;
using DevHub.Integration;
using DevHub.Services;

namespace DevHub.Diagrams;

/// <summary>What a property field edits (the properties panel shows the matching editor).</summary>
public enum FieldKind { Text, MultilineText, Choice, Toggle, Color, Number, Rows }

/// <summary>An option of a choice field: stored <paramref name="Value"/>, shown <paramref name="Label"/> (translated).</summary>
public sealed record FieldChoice(string Value, string Label);

/// <summary>
/// One editable property of a shape, a line or the diagram. <paramref name="Key"/> "@text" is the element's text
/// ("@title" the diagram's title); any other key is a detail in its <c>Props</c>.
/// </summary>
public sealed record PropertyField(string Label, string Key, FieldKind Kind = FieldKind.Text, string Hint = "")
{
    public IReadOnlyList<FieldChoice> Choices { get; init; } = [];

    /// <summary>The value when the detail isn't set.</summary>
    public string Default { get; init; } = "";

    /// <summary>A number field with <see cref="Maximum"/> above <see cref="Minimum"/> is a slider.</summary>
    public double Minimum { get; init; }

    public double Maximum { get; init; }

    /// <summary>The slider's step (0: any value).</summary>
    public double Step { get; init; }

    /// <summary>With <see cref="FieldKind.Rows"/>: how the text splits into rows of cells (class attributes: visibility,
    /// name, type, static…), edited as a small form per row and stored back as the same text.</summary>
    public IRowCodec? Rows { get; init; }
}

/// <summary>A cell of a row in a <see cref="FieldKind.Rows"/> field.</summary>
/// <param name="Kind">Text, Choice (a drop-down), Toggle (a check box) or Number.</param>
/// <param name="Width">Its width in the row (0: the rest of the line).</param>
public sealed record RowColumn(string Key, string Label, FieldKind Kind = FieldKind.Text, double Width = 0)
{
    public IReadOnlyList<FieldChoice> Choices { get; init; } = [];

    /// <summary>Values offered while typing (types: int, String … and the diagram's classes).</summary>
    public Func<Diagram, IReadOnlyList<string>>? Suggestions { get; init; }

    /// <summary>Shown in the empty box.</summary>
    public string Placeholder { get; init; } = "";
}

/// <summary>Turns a field's text into rows of cells and back (one line per row).</summary>
public interface IRowCodec
{
    IReadOnlyList<RowColumn> Columns { get; }

    /// <summary>"Add …" (the button under the rows).</summary>
    string AddText { get; }

    /// <summary>The cells of a new row.</summary>
    IReadOnlyDictionary<string, string> NewRow();

    IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string text);

    string Format(IEnumerable<IReadOnlyDictionary<string, string>> rows);
}

/// <summary>A command a kind adds to the editor's toolbar / menu ("Generate MLD", "Palette from Image").</summary>
public sealed record KindCommand(string Name, string Icon, Func<IDiagramEditor, Task> Run)
{
    /// <summary>When it can run (default: always).</summary>
    public Func<IDiagramEditor, bool> CanRun { get; init; } = _ => true;

    /// <summary>A short explanation for the tooltip.</summary>
    public string Description { get; init; } = "";
}

/// <summary>An open diagram, as the canvas and kind commands use it (the document ViewModel implements it).</summary>
public interface IDiagramEditor
{
    Diagram Diagram { get; }

    IDiagramKind Kind { get; }

    /// <summary>The file, or null before the first save.</summary>
    string? FilePath { get; }

    /// <summary>The linked project's folder, or null.</summary>
    string? ProjectFolder { get; }

    IReadOnlyList<string> Selection { get; }

    void Select(IEnumerable<string> ids, bool add = false);

    /// <summary>Runs an operation (one Undo step); a refused one shows its reason in the status bar. False when refused.</summary>
    bool Do(IDiagramOperation operation);

    /// <summary>Saves a diagram as a file next to this one (or replaces it) and opens it in a tab.</summary>
    Task OpenGeneratedAsync(Diagram diagram, string path);

    /// <summary>Where diagram centre of the visible area is (new shapes from commands go there).</summary>
    DPoint ViewCenter { get; }

    IDialogService Dialogs { get; }
    IExportService Export { get; }
    IStatusService Status { get; }

    /// <summary>Issues to link to (null when the Board plugin is off).</summary>
    IIssueLinks? Issues { get; }
}

/// <summary>
/// A kind of diagram as the editor sees it: its <see cref="Model"/> (shapes, lines, rules, exports; tested in the kind
/// plugin's <c>.Core</c>) plus how it's drawn and edited. Kind plugins register one per kind:
/// <c>services.AddSingleton&lt;IDiagramKind, UseCaseKind&gt;()</c>.
/// </summary>
public interface IDiagramKind
{
    IDiagramKindModel Model { get; }

    /// <summary>An <c>IconKind</c> name for lists and the New Diagram dialog.</summary>
    string IconKind { get; }

    /// <summary>Position in the New Diagram dialog.</summary>
    int Order { get; }

    void DrawNode(IDiagramSurface surface, Diagram diagram, DiagramNode node);

    void DrawEdge(IDiagramSurface surface, Diagram diagram, DiagramEdge edge, IReadOnlyList<DPoint> path);

    IReadOnlyList<PropertyField> NodeFields(DiagramNode node);

    IReadOnlyList<PropertyField> EdgeFields(DiagramEdge edge);

    IReadOnlyList<PropertyField> DiagramFields { get; }

    /// <summary>The size a shape needs for its text (class boxes grow with their attributes), or null to keep it.</summary>
    DRect? Fit(DiagramNode node, IDiagramSurface measure);

    /// <summary>Whether the shape's text is several lines (in-place editing takes Enter as a new line).</summary>
    bool MultilineText(DiagramNode node);

    IReadOnlyList<KindCommand> Commands { get; }

    /// <summary>The kind offers the freehand pen (mood boards).</summary>
    bool HasPen { get; }

    /// <summary>Pastes what isn't diagram shapes (a picture, a link) at a point; false when it can't.</summary>
    Task<bool> PasteAsync(IDiagramEditor editor, DPoint at);

    /// <summary>Takes files dropped from Explorer (pictures); false when it doesn't.</summary>
    Task<bool> DropFilesAsync(IDiagramEditor editor, IReadOnlyList<string> paths, DPoint at);

    /// <summary>Makes a freehand stroke from points in diagram coordinates (only kinds with <see cref="HasPen"/>).</summary>
    IDiagramOperation? Stroke(Diagram diagram, IReadOnlyList<DPoint> points, string color, double width);
}
