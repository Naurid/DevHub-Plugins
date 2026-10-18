using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.MoodBoard.Core;

/// <summary>
/// A mood board: pictures, colour swatches, shapes and frames, link cards, free text and freehand drawing on an
/// unlimited canvas. Pictures are copied beside the board (<c>&lt;board&gt;.assets/</c>) so git keeps them with it.
/// </summary>
public sealed class MoodBoardModel : IDiagramKindModel
{
    public const string KindId = "moodboard";

    public const string Image = "image";
    public const string Swatch = "swatch";
    public const string Rectangle = "rectangle";
    public const string Ellipse = "ellipse";
    public const string Frame = "frame";
    public const string TextBox = "text";
    public const string Link = "link";
    public const string Stroke = "stroke";

    public const string Arrow = "arrow";
    public const string Line = "line";

    public string Id => KindId;
    public string Name => L.T("Mood board");
    public string Description => L.T("Pictures, colours, shapes, links and drawing, freely arranged: for a look, a style, ideas.");

    public IReadOnlyList<NodeTypeInfo> NodeTypes { get; } =
    [
        new(Image, L.T("Picture"), NodeShape.Rectangle, 240, 180)
            { Description = L.T("A picture: drop files, paste (Ctrl+V) or pick one."), Icon = "Palette" },
        new(Swatch, L.T("Colour swatch"), NodeShape.Rectangle, 110, 140, "")
            { Description = L.T("A colour with its code (Tools › Copy Colour copies it)."), Props = new Dictionary<string, string> { ["color"] = "#3574F0" } },
        new(Rectangle, L.T("Rectangle"), NodeShape.Rectangle, 160, 100, "") { Description = L.T("A shape, with an optional label.") },
        new(Ellipse, L.T("Ellipse"), NodeShape.Ellipse, 140, 100, "") { Description = L.T("A shape, with an optional label.") },
        new(Frame, L.T("Frame"), NodeShape.Rectangle, 520, 380, L.T("Frame"))
            { IsContainer = true, Description = L.T("Groups things under a title; they move with it.") },
        new(TextBox, L.T("Text"), NodeShape.Rectangle, 220, 44, L.T("Text")) { Description = L.T("Free text, a title or a comment.") },
        new(Link, L.T("Link"), NodeShape.RoundedRectangle, 260, 74, L.T("Link"))
            { Description = L.T("A web page, opened in the browser."), Props = new Dictionary<string, string> { ["url"] = "https://" } },
        new(Stroke, L.T("Drawing"), NodeShape.Path, 10, 10) { Hidden = true, Connectable = false },
    ];

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; } =
    [
        new(Arrow, L.T("Arrow"), L.T("Points from one thing to another.")),
        new(Line, L.T("Line"), L.T("Joins two things.")),
    ];

    public IReadOnlyList<IDiagramRule> Rules { get; } = [];

    public IReadOnlyList<IDiagramTextExporter> TextExporters { get; } = [new SwatchCss(), new LinkList()];

    public IReadOnlyList<DiagramSample> Samples => MoodBoardShowcase.Samples;

    public Diagram Create(string title) => new() { Kind = KindId, Title = title };
}

/// <summary>The swatches as CSS custom properties (for a site's style sheet).</summary>
public sealed class SwatchCss : IDiagramTextExporter
{
    public string Name => L.T("Colours (CSS)");
    public string Extension => "css";

    public string Export(Diagram d)
    {
        var w = new StringBuilder(":root {\n");
        var used = new HashSet<string>();
        var i = 1;
        foreach (var s in d.Nodes.Where(n => n.Type == MoodBoardModel.Swatch).OrderBy(n => n.Y).ThenBy(n => n.X))
        {
            var name = Slug(s.Text);
            if (name.Length == 0 || !used.Add(name))
                name = $"color-{i}";
            used.Add(name);
            i++;
            w.Append($"  --{name}: {s.Prop("color").ToLowerInvariant()};\n");
        }
        return w.Append("}\n").ToString();
    }

    private static string Slug(string text)
    {
        var plain = new StringBuilder();
        foreach (var c in text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            plain.Append(char.IsLetterOrDigit(c) ? c : '-');
        }
        return string.Join("-", plain.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>The board's links as a Markdown list.</summary>
public sealed class LinkList : IDiagramTextExporter
{
    public string Name => L.T("Links (Markdown)");
    public string Extension => "md";

    public string Export(Diagram d) => string.Concat(d.Nodes.Where(n => n.Type == MoodBoardModel.Link)
        .Select(n => $"- [{(n.Text.Trim().Length > 0 ? n.Text.Trim() : n.Prop("url"))}]({n.Prop("url")})\n"));
}

/// <summary>Pictures kept beside the board: <c>Board.assets/3f2a….png</c>, named by their content (pasting twice stores once).</summary>
public static class Assets
{
    public static string FolderFor(string boardPath) =>
        Path.Combine(Path.GetDirectoryName(boardPath) ?? ".", DiagramFile.NameOf(boardPath) + ".assets");

    /// <summary>Copies picture bytes beside the board; returns the path to store (relative, with /).</summary>
    public static async Task<string> StoreAsync(string boardPath, byte[] data, string extension, CancellationToken ct = default)
    {
        var folder = FolderFor(boardPath);
        Directory.CreateDirectory(folder);
        var name = Convert.ToHexStringLower(SHA256.HashData(data))[..16] + "." + extension.TrimStart('.').ToLowerInvariant();
        var path = Path.Combine(folder, name);
        if (!File.Exists(path))
            await File.WriteAllBytesAsync(path, data, ct);
        return Path.GetFileName(folder) + "/" + name;
    }

    /// <summary>The file a stored path points to (an absolute path stays as it is).</summary>
    public static string? Resolve(string? boardFolder, string src)
    {
        if (src.Length == 0)
            return null;
        if (Path.IsPathRooted(src))
            return src;
        return boardFolder is null ? null : Path.GetFullPath(Path.Combine(boardFolder, src.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    public static bool IsPicture(string path) => PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}

/// <summary>Freehand strokes: smoothed into fewer points, kept as a shape whose points are relative to its corner.</summary>
public static class Strokes
{
    /// <summary>Ramer–Douglas–Peucker: drops points closer than <paramref name="tolerance"/> to the line through their neighbours.</summary>
    public static List<DPoint> Simplify(IReadOnlyList<DPoint> points, double tolerance = 1.2)
    {
        if (points.Count < 3)
            return [.. points];
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            var max = 0.0;
            var index = -1;
            for (var i = a + 1; i < b; i++)
            {
                var d = Shapes.DistanceToSegment(points[i], points[a], points[b]);
                if (d > max)
                    (max, index) = (d, i);
            }
            if (index >= 0 && max > tolerance)
            {
                keep[index] = true;
                stack.Push((a, index));
                stack.Push((index, b));
            }
        }
        return points.Where((_, i) => keep[i]).ToList();
    }

    /// <summary>A stroke shape from points in diagram coordinates, or null for a stroke too short to keep.</summary>
    public static DiagramNode? Make(Diagram d, IReadOnlyList<DPoint> points, string color, double width)
    {
        var simple = Simplify(points, Math.Max(0.6, width * 0.25));
        if (simple.Count < 2)
            return null;
        var pad = width / 2 + 1;
        var minX = simple.Min(p => p.X) - pad;
        var minY = simple.Min(p => p.Y) - pad;
        var node = new DiagramNode
        {
            Id = DiagramIds.NewIn(d),
            Type = MoodBoardModel.Stroke,
            X = minX,
            Y = minY,
            Width = Math.Max(2, simple.Max(p => p.X) + pad - minX),
            Height = Math.Max(2, simple.Max(p => p.Y) + pad - minY),
            Points = simple.Select(p => new DPoint(Math.Round(p.X - minX, 1), Math.Round(p.Y - minY, 1))).ToList(),
        };
        node.Props["line"] = color;
        node.Props["width"] = width.ToString(CultureInfo.InvariantCulture);
        return node;
    }
}

/// <summary>The main colours of a picture: k-means over its pixels, largest group first.</summary>
public static class Palette
{
    public static IReadOnlyList<string> Dominant(IReadOnlyList<(byte R, byte G, byte B)> pixels, int count = 5, int seed = 7)
    {
        if (pixels.Count == 0)
            return [];
        var data = pixels.Select(p => (R: (double)p.R, G: (double)p.G, B: (double)p.B)).ToArray();
        var distinct = data.Distinct().Count();
        var k = Math.Min(count, distinct);
        var random = new Random(seed);

        // k-means++: each new centre far from the ones already chosen.
        var centres = new List<(double R, double G, double B)> { data[random.Next(data.Length)] };
        while (centres.Count < k)
        {
            var weights = data.Select(p => centres.Min(c => Distance(p, c))).ToArray();
            var total = weights.Sum();
            if (total <= 0)
                break;
            var pick = random.NextDouble() * total;
            var i = 0;
            for (; i < weights.Length - 1 && (pick -= weights[i]) > 0; i++)
            {
            }
            centres.Add(data[i]);
        }

        var groups = new int[data.Length];
        for (var round = 0; round < 24; round++)
        {
            var moved = false;
            for (var i = 0; i < data.Length; i++)
            {
                var best = 0;
                for (var c = 1; c < centres.Count; c++)
                    if (Distance(data[i], centres[c]) < Distance(data[i], centres[best]))
                        best = c;
                moved |= groups[i] != best;
                groups[i] = best;
            }
            for (var c = 0; c < centres.Count; c++)
            {
                var members = data.Where((_, i) => groups[i] == c).ToList();
                if (members.Count > 0)
                    centres[c] = (members.Average(m => m.R), members.Average(m => m.G), members.Average(m => m.B));
            }
            if (!moved && round > 0)
                break;
        }
        return centres
            .Select((c, i) => (Colour: c, Size: groups.Count(g => g == i)))
            .Where(x => x.Size > 0)
            .OrderByDescending(x => x.Size)
            .Select(x => $"#{(byte)Math.Round(x.Colour.R):X2}{(byte)Math.Round(x.Colour.G):X2}{(byte)Math.Round(x.Colour.B):X2}")
            .Distinct()
            .ToList();
    }

    private static double Distance((double R, double G, double B) a, (double R, double G, double B) b)
    {
        // Weighted RGB: closer to how different two colours look than plain distance.
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return 2 * dr * dr + 4 * dg * dg + 3 * db * db;
    }

    /// <summary>Black or white, whichever reads better on <paramref name="hex"/>.</summary>
    public static string TextOn(string hex)
    {
        if (hex.Length < 7 || !int.TryParse(hex.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return "#000000";
        var r = (rgb >> 16) & 255;
        var g = (rgb >> 8) & 255;
        var b = rgb & 255;
        return 0.299 * r + 0.587 * g + 0.114 * b > 150 ? "#1F1F1F" : "#FFFFFF";
    }
}
