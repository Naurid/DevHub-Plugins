using System.Text;
using DevHub.Templates.Core.Rendering;

namespace DevHub.Templates.Core.Generation;

/// <summary>
/// Writes a template's content files (§8): binary files byte for byte, text files rendered with
/// their encoding kept. A UTF-8 / UTF-16 / UTF-32 BOM is detected and written back; a file without
/// one must be valid UTF-8 (else it's copied as is, not rendered: rendering would corrupt it). Line
/// endings are never touched.
/// </summary>
public static class ContentFiles
{
    /// <summary>Extensions that are always binary (on top of a template's own binaryExtensions).</summary>
    public static readonly IReadOnlySet<string> BuiltInBinaryExtensions = new HashSet<string>(
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".icns", ".webp", ".tga", ".psd", ".tif", ".tiff", ".exr", ".hdr",
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".lib", ".a", ".o", ".obj", ".class", ".jar",
        ".zip", ".7z", ".gz", ".tar", ".rar", ".nupkg", ".unitypackage",
        ".pdf", ".docx", ".xlsx", ".pptx",
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".mp3", ".wav", ".ogg", ".flac", ".mp4", ".mov", ".avi", ".webm",
        ".fbx", ".blend", ".glb", ".gltf", ".dae", ".3ds", ".max",
        ".bin", ".dat", ".db", ".sqlite", ".snk", ".pfx",
    ], StringComparer.OrdinalIgnoreCase);

    private const int SniffLength = 8000;

    /// <summary>Binary: a known extension, or a NUL byte in the first 8 KB.</summary>
    public static bool IsBinary(string path, IReadOnlyCollection<string> extraExtensions)
    {
        var extension = Path.GetExtension(path);
        if (BuiltInBinaryExtensions.Contains(extension) || extraExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return true;

        using var stream = File.OpenRead(path);
        var buffer = new byte[SniffLength];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        // UTF-16 / UTF-32 text is full of NULs: a BOM says it's text.
        return DetectBom(buffer.AsSpan(0, read)) is null && Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/>: rendered when it's text, as is when binary.</summary>
    public static Task CopyAsync(string source, string target, RenderContext context,
        IReadOnlyCollection<string> extraBinaryExtensions, CancellationToken ct = default) =>
        TransformAsync(source, target, text => PlaceholderRenderer.Render(text, context), extraBinaryExtensions, ct);

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/>, changing a text file's text with
    /// <paramref name="transform"/> (encoding, BOM and line endings kept). Binary files, and text that
    /// isn't UTF-8 without a BOM (an old ANSI file), are copied unchanged: the only safe thing.
    /// </summary>
    public static async Task TransformAsync(string source, string target, Func<string, string> transform,
        IReadOnlyCollection<string> extraBinaryExtensions, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (IsBinary(source, extraBinaryExtensions) || await ReadAsync(source, ct) is not { } file)
        {
            File.Copy(source, target, overwrite: true);
            return;
        }

        await using var output = File.Create(target);
        if (file.Bom is not null)
            await output.WriteAsync(file.Bytes.AsMemory(0, file.Bom.Length), ct);
        await output.WriteAsync(file.Encoding.GetBytes(transform(file.Text)), ct);
    }

    /// <summary>A text file's text, or null when it's binary or can't be decoded (see <see cref="TransformAsync"/>).</summary>
    public static async Task<string?> ReadTextAsync(string path, IReadOnlyCollection<string> extraBinaryExtensions, CancellationToken ct = default) =>
        IsBinary(path, extraBinaryExtensions) ? null : (await ReadAsync(path, ct))?.Text;

    private sealed record TextFile(byte[] Bytes, Bom? Bom, Encoding Encoding, string Text);

    private static async Task<TextFile?> ReadAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var bom = DetectBom(bytes);
        var encoding = bom?.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return new TextFile(bytes, bom, encoding, encoding.GetString(bytes, bom?.Length ?? 0, bytes.Length - (bom?.Length ?? 0)));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private sealed record Bom(Encoding Encoding, int Length);

    private static Bom? DetectBom(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xEF, 0xBB, 0xBF, ..] => new Bom(new UTF8Encoding(false, true), 3),
        [0xFF, 0xFE, 0x00, 0x00, ..] => new Bom(new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true), 4),
        [0x00, 0x00, 0xFE, 0xFF, ..] => new Bom(new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true), 4),
        [0xFF, 0xFE, ..] => new Bom(new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), 2),
        [0xFE, 0xFF, ..] => new Bom(new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), 2),
        _ => null,
    };
}
