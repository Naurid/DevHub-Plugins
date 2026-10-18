using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Diagrams.Core;

/// <summary>Reads and writes <c>*.devhub-diagram.json</c> files: indented, camelCase, stable bytes (diff-friendly in git).</summary>
public static class DiagramFile
{
    /// <summary>The file name ending ("Login.devhub-diagram.json").</summary>
    public const string Extension = ".devhub-diagram.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // « é » stays readable in the file
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <exception cref="DiagramFileException">Not a diagram file.</exception>
    public static Diagram Read(string json)
    {
        Diagram? diagram;
        try
        {
            diagram = JsonSerializer.Deserialize<Diagram>(json, Options);
        }
        catch (JsonException e)
        {
            throw new DiagramFileException(e.Message);
        }
        if (diagram is null || diagram.Kind.Length == 0)
            throw new DiagramFileException("No diagram kind in the file.");
        diagram.Nodes ??= [];
        diagram.Edges ??= [];
        diagram.Props ??= new(StringComparer.Ordinal);
        foreach (var n in diagram.Nodes)
            n.Props ??= new(StringComparer.Ordinal);
        foreach (var e in diagram.Edges)
        {
            e.Props ??= new(StringComparer.Ordinal);
            e.Waypoints ??= [];
        }
        return diagram;
    }

    public static string Write(Diagram diagram) => JsonSerializer.Serialize(diagram, Options).Replace("\r\n", "\n") + "\n";

    public static async Task<Diagram> LoadAsync(string path, CancellationToken ct = default) =>
        Read(await File.ReadAllTextAsync(path, Encoding.UTF8, ct));

    /// <summary>Writes through a temporary file, so a failure never leaves half a diagram.</summary>
    public static async Task SaveAsync(Diagram diagram, string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        await File.WriteAllTextAsync(temp, Write(diagram), new UTF8Encoding(false), ct);
        File.Move(temp, full, overwrite: true);
    }

    /// <summary>Whether a path is a diagram file.</summary>
    public static bool IsDiagram(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name shown for a file ("Login" for "Login.devhub-diagram.json").</summary>
    public static string NameOf(string path)
    {
        var name = Path.GetFileName(path);
        return IsDiagram(name) ? name[..^Extension.Length] : Path.GetFileNameWithoutExtension(name);
    }
}

/// <summary>A file that isn't a readable diagram.</summary>
public sealed class DiagramFileException(string message) : Exception(message);

/// <summary>Short ids for nodes and edges ("n7k2qd").</summary>
public static class DiagramIds
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[6];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return new string(chars);
    }

    /// <summary>A new id that isn't used in the diagram yet.</summary>
    public static string NewIn(Diagram diagram)
    {
        while (true)
        {
            var id = New();
            if (!diagram.Has(id))
                return id;
        }
    }
}
