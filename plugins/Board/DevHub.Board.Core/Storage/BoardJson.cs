using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Board.Core.Model;

namespace DevHub.Board.Core;

/// <summary>
/// How board files are written and read. Same data ⇒ same bytes (fixed property order, "\n" line ends, UTF-8
/// without BOM), so diffs stay small and unchanged files are recognised as unchanged.
/// </summary>
public static class BoardJson
{
    /// <summary>The file format version written into every file ("schema").</summary>
    public const int SchemaVersion = 1;

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        NewLine = "\n",
        IndentSize = 2,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>One line per object (history files).</summary>
    public static JsonSerializerOptions LineOptions { get; } = new(Options) { WriteIndented = false };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] Serialize<T>(T value) =>
        Utf8.GetBytes(JsonSerializer.Serialize(value, Options) + "\n");

    public static T Deserialize<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(StripBom(bytes), Options)
        ?? throw new JsonException($"The file holds no {typeof(T).Name}.");

    /// <summary>History lines: one JSON object per line.</summary>
    public static byte[] AppendLines<T>(byte[]? existing, IEnumerable<T> lines)
    {
        var text = new StringBuilder(existing is null ? "" : Utf8.GetString(StripBom(existing)));
        if (text.Length > 0 && text[^1] != '\n')
            text.Append('\n');
        foreach (var line in lines)
            text.Append(JsonSerializer.Serialize(line, LineOptions)).Append('\n');
        return Utf8.GetBytes(text.ToString());
    }

    /// <summary>Reads JSON lines, skipping blank and unreadable ones (a hand-edited or half-merged file still loads).</summary>
    public static IReadOnlyList<T> ReadLines<T>(byte[]? bytes)
    {
        if (bytes is null)
            return [];
        var result = new List<T>();
        foreach (var line in Utf8.GetString(StripBom(bytes)).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<T>(line, LineOptions) is { } item)
                    result.Add(item);
            }
            catch (JsonException)
            {
            }
        }
        return result;
    }

    private static ReadOnlySpan<byte> StripBom(byte[] bytes) =>
        bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes.AsSpan(3) : bytes;
}

/// <summary>Where each kind of file lives inside a board store (forward slashes, relative).</summary>
public static class BoardPaths
{
    public const string Project = "board.json";
    public const string IssuesFolder = "issues/";
    public const string WorkflowsFolder = "workflows/";
    public const string BoardsFolder = "boards/";
    public const string VersionsFolder = "versions/";
    public const string HistoryFolder = "history/";
    public const string AttachmentsFolder = "attachments/";

    public static string Issue(string id) => IssuesFolder + id + ".json";
    public static string Workflow(string id) => WorkflowsFolder + id + ".json";
    public static string Board(string id) => BoardsFolder + id + ".json";
    public static string Version(string id) => VersionsFolder + id + ".json";
    public static string History(string issueId) => HistoryFolder + issueId + ".jsonl";
    public static string AttachmentsOf(string issueId) => AttachmentsFolder + issueId + "/";

    public static string Attachment(string issueId, string attachmentId, string fileName) =>
        AttachmentsOf(issueId) + attachmentId + "-" + SafeFileName(fileName);

    /// <summary>"issues/abc.json" → "abc" when the path is a direct child of <paramref name="folder"/> with that extension.</summary>
    public static string? IdOf(string path, string folder, string extension) =>
        path.StartsWith(folder, StringComparison.Ordinal) && path.EndsWith(extension, StringComparison.Ordinal)
        && path.IndexOf('/', folder.Length) < 0
            ? path[folder.Length..^extension.Length]
            : null;

    /// <summary>
    /// True for a path a store may write: relative, forward slashes, no empty, "." or ".." parts, no characters
    /// Windows refuses. Stores check every path, so a bad file name can never write outside the board.
    /// </summary>
    public static bool IsSafe(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 400 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':'))
            return false;
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || part.Any(char.IsControl))
                return false;
        }
        return true;
    }

    /// <summary>A file name that's safe inside a store path (characters Windows refuses become '_').</summary>
    public static string SafeFileName(string name)
    {
        var chars = name.Trim().Select(c => c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c)
            .ToArray();
        var safe = new string(chars).Trim('.', ' ');
        if (safe.Length == 0)
            safe = "file";
        return safe.Length > 120 ? safe[..120] : safe;
    }
}
