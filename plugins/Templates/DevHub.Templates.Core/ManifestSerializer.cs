using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DevHub.Templates.Core;

/// <summary>The outcome of reading a template.json: the manifest, or why it couldn't be read.</summary>
public sealed record ManifestReadResult(TemplateManifest? Manifest, string? Error)
{
    public bool Succeeded => Manifest is not null;
}

/// <summary>
/// Reads and writes template.json. camelCase, enums as camelCase strings ("project",
/// "createFolder"), comments and trailing commas allowed (people edit these by hand). Written for people
/// too: indented, characters like &lt; and é as they are (not 003C), and without what is the default
/// anyway (nulls, false, empty lists, kind "project", runIn "root"). Problems come back as a result, not an exception.
/// </summary>
public static class ManifestSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a file, never HTML: no need to escape <, >, &, '
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipEmptyLists } },
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>An empty list (no steps, no categories…) isn't written: missing means empty when reading.</summary>
    private static void SkipEmptyLists(JsonTypeInfo info)
    {
        foreach (var property in info.Properties)
        {
            if (!property.IsExtensionData && property.PropertyType != typeof(string)
                && typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
                property.ShouldSerialize = (_, value) => value is not ICollection { Count: 0 };
        }
    }

    public static ManifestReadResult Read(string json)
    {
        TemplateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<TemplateManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            return new ManifestReadResult(null, "template.json isn't valid: " + ex.Message);
        }

        return manifest switch
        {
            null => new ManifestReadResult(null, "template.json is empty."),
            { SchemaVersion: null } => new ManifestReadResult(null, "template.json has no schemaVersion."),
            { SchemaVersion: > TemplateManifest.CurrentSchemaVersion } m => new ManifestReadResult(null,
                $"This template needs a newer DevHub (template format {m.SchemaVersion}; this DevHub reads up to {TemplateManifest.CurrentSchemaVersion})."),
            { SchemaVersion: < 1 } m => new ManifestReadResult(null, $"template.json has an invalid schemaVersion ({m.SchemaVersion})."),
            _ => new ManifestReadResult(manifest, null),
        };
    }

    /// <summary>The manifest as JSON, always with the current <c>schemaVersion</c>.</summary>
    public static string Write(TemplateManifest manifest) =>
        JsonSerializer.Serialize(manifest with { SchemaVersion = TemplateManifest.CurrentSchemaVersion }, Options);
}
