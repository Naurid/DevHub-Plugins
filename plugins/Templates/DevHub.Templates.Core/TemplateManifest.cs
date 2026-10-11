using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Templates.Core;

// The template.json model (docs/TEMPLATE_FORMAT.md §3). Plain records: what's in the file, nothing
// more. Every record keeps fields it doesn't know in Extra, so a manifest written by a newer
// DevHub survives being loaded and saved by this one.

/// <summary>What a template makes (§1).</summary>
public enum TemplateKind
{
    /// <summary>Creates a new project folder under a parent folder the user picks.</summary>
    Project,

    /// <summary>Adds files / folders into an existing folder the user picks (and may move files there).</summary>
    Item,
}

/// <summary>The kind of value a variable holds.</summary>
public enum VariableType
{
    String,
    Bool,
    Choice,
}

/// <summary>Where the base command runs (§4 step 1).</summary>
public enum RunIn
{
    /// <summary>Inside the (empty) root folder, e.g. <c>dotnet new console -o .</c>.</summary>
    Root,

    /// <summary>In the root's parent: the command creates the root folder itself, e.g. <c>cargo new Name</c>.</summary>
    Parent,
}

/// <summary>A rearranging step (§6).</summary>
public enum StepOp
{
    CreateFolder,
    Move,
    Rename,
    Delete,
}

/// <summary>One template.json.</summary>
public sealed record TemplateManifest
{
    /// <summary>The current format version; a manifest without one, or with a higher one, is refused.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The format version in the file: null when it has none (refused on load). No default here,
    /// so a missing field stays visible; <see cref="ManifestSerializer.Write"/> always writes the current one.
    /// </summary>
    public int? SchemaVersion { get; init; }

    /// <summary>Lowercase kebab-case, unique within its library, equal to the template's folder name.</summary>
    public string Id { get; init; } = "";

    public TemplateKind Kind { get; init; } = TemplateKind.Project;
    public string Name { get; init; } = "";
    public string? Description { get; init; }

    /// <summary>One language ("C#", "Python", …), or "Any".</summary>
    public string Language { get; init; } = "Any";

    /// <summary>Any number of categories ("Unity", "Web", …).</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    public IReadOnlyList<TemplateVariable> Variables { get; init; } = [];

    /// <summary>Project only: the folder created under the chosen parent (rendered); null = <see cref="DefaultRootFolder"/>.</summary>
    public string? RootFolder { get; init; }

    /// <summary>Project only, optional: the tool's own scaffold command.</summary>
    public BaseCommand? BaseCommand { get; init; }

    public IReadOnlyList<TemplateStep> Steps { get; init; } = [];

    public IReadOnlyList<PostCreateCommand> PostCreate { get; init; } = [];

    /// <summary>Extensions always treated as binary, on top of the built-in list ("<c>.png</c>").</summary>
    public IReadOnlyList<string> BinaryExtensions { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>A project's root folder when <see cref="RootFolder"/> isn't set.</summary>
    public const string DefaultRootFolder = "{{Name}}";

    /// <summary>The root folder pattern in effect (project templates).</summary>
    [JsonIgnore]
    public string EffectiveRootFolder => RootFolder ?? DefaultRootFolder;
}

/// <summary>A value the user fills in when using the template.</summary>
public sealed record TemplateVariable
{
    /// <summary>The name used in placeholders: <c>[A-Za-z][A-Za-z0-9]*</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>The form label; the name when empty.</summary>
    public string? Label { get; init; }

    public VariableType Type { get; init; } = VariableType.String;

    /// <summary>The initial value: a JSON string, or <c>true</c> / <c>false</c> for a bool (see <see cref="DefaultText"/>).</summary>
    public JsonElement? Default { get; init; }

    public bool Required { get; init; }

    /// <summary>Optional regex a string value must match.</summary>
    public string? Pattern { get; init; }

    /// <summary>The options of a <see cref="VariableType.Choice"/>.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>The default as text ("true" / "false" for a bool, "" when there's none).</summary>
    [JsonIgnore]
    public string DefaultText => Default switch
    {
        { ValueKind: JsonValueKind.String } d => d.GetString() ?? "",
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { ValueKind: JsonValueKind.Number } d => d.GetRawText(),
        _ => "",
    };
}

/// <summary>The scaffold command a project template starts from (§3, §4).</summary>
public sealed record BaseCommand
{
    /// <summary>An executable ("dotnet") or a DevHub tool alias ("@unity").</summary>
    public string Command { get; init; } = "";

    /// <summary>Separate arguments (never one shell string), rendered.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    public RunIn RunIn { get; init; } = RunIn.Root;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// One rearranging step. Which fields it uses depends on <see cref="Op"/>: createFolder / delete
/// use <see cref="Path"/>, move / rename use <see cref="From"/> and <see cref="To"/> (§6).
/// </summary>
public sealed record TemplateStep
{
    public StepOp Op { get; init; }
    public string? Path { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>A command run in the final folder after everything is in place (§4 step 6).</summary>
public sealed record PostCreateCommand
{
    /// <summary>What the confirmation and the output log call it ("Initialize git").</summary>
    public string Name { get; init; } = "";

    public string Command { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Relative to the project root (item: the target folder).</summary>
    public string WorkingDirectory { get; init; } = ".";

    /// <summary>Optional: the name of a bool variable; the command runs only when it's true.</summary>
    public string? When { get; init; }

    /// <summary>False: a failure stops the remaining commands.</summary>
    public bool ContinueOnError { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
