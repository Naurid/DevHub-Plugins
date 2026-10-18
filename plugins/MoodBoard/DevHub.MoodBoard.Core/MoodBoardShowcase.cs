using System.IO.Compression;
using System.Text;
using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.MoodBoard.Core;

/// <summary>
/// The mood board's "everything" sample: pictures (drawn here as PNGs, stored beside the board), swatches, shapes,
/// a frame, text, link cards, freehand strokes and arrows, with formatting.
/// </summary>
public static class MoodBoardShowcase
{
    public static IReadOnlyList<DiagramSample> Samples =>
    [
        new(L.T("Showcase: everything"), L.T("Pictures, swatches, shapes, a frame, text, links, drawing and arrows."), Create)
        {
            Assets = new Dictionary<string, byte[]>
            {
                ["sunset.png"] = Png.Gradient(320, 200, (0xFF, 0x9A, 0x3C), (0x5B, 0x2A, 0x86), sun: true),
                ["sea.png"] = Png.Gradient(240, 240, (0x8E, 0xD1, 0xFC), (0x1E, 0x4E, 0x8C), sun: false),
            },
        },
    ];

    private static Diagram Create()
    {
        var b = new SampleBuilder(new MoodBoardModel(), L.T("Summer collection"));
        var frame = b.Box(MoodBoardModel.Frame, 20, 50, 820, 520, L.T("Summer collection"), null, ("fill", "#FFFBF5"), ("line", "#D98C1F"));
        var sunset = b.Box(MoodBoardModel.Image, 50, 80, 320, 200, L.T("Evening light"), frame, ("src", DiagramSample.AssetsToken + "sunset.png"),
            ("radius", "12"), ("line", "#D98C1F"));
        var sea = b.Box(MoodBoardModel.Image, 400, 80, 200, 200, L.T("Sea"), frame, ("src", DiagramSample.AssetsToken + "sea.png"), ("opacity", "0.85"));
        var orange = b.Box(MoodBoardModel.Swatch, 640, 80, 90, 112, L.T("Sunset"), frame, ("color", "#FF9A3C"));
        var purple = b.Box(MoodBoardModel.Swatch, 740, 80, 90, 112, L.T("Dusk"), frame, ("color", "#5B2A86"));
        b.Box(MoodBoardModel.Swatch, 640, 200, 90, 112, L.T("Lagoon"), frame, ("color", "#8ED1FC"));
        b.Box(MoodBoardModel.Swatch, 740, 200, 90, 112, "", frame, ("color", "#1E4E8C"));
        b.Box(MoodBoardModel.TextBox, 50, 320, 360, 50, L.T("Warm, light, relaxed"), frame, ("fontSize", "24"), ("bold", "true"), ("textColor", "#5B2A86"));
        b.Box(MoodBoardModel.TextBox, 50, 375, 360, 70, L.T("Linen, straw hats, sandals.\nNo neon, no black."), frame, ("fontSize", "14"), ("fill", "#FFF0D9"));
        var keep = b.Box(MoodBoardModel.Rectangle, 440, 330, 160, 70, L.T("Keep"), frame, ("fill", "#E3F5E9"), ("line", "#2E9E5B"), ("radius", "16"), ("bold", "true"));
        var avoid = b.Box(MoodBoardModel.Ellipse, 640, 330, 150, 80, L.T("Avoid"), frame, ("fill", "#FDE7E6"), ("line", "#E5534B"), ("textColor", "#E5534B"));
        var link = b.Box(MoodBoardModel.Link, 440, 450, 300, 74, L.T("Colour trends"), frame, ("url", "https://example.com/trends"));
        b.Box(MoodBoardModel.Link, 900, 80, 260, 74, L.T("Fabric supplier"), null, ("url", "https://example.com/linen"), ("fill", "#F3F8FF"));
        b.Box(MoodBoardModel.TextBox, 900, 180, 260, 40, L.T("Ideas outside the frame"), null, ("fontSize", "16"));

        // Freehand: a wave and a circle around the keepers.
        var wave = Strokes.Make(b.Diagram, Enumerable.Range(0, 60).Select(i => new DPoint(60 + i * 5, 500 + 18 * Math.Sin(i / 5.0))).ToList(), "#3574F0", 4)!;
        wave.ParentId = frame.Id;
        b.Diagram.Nodes.Add(wave);
        var loop = Strokes.Make(b.Diagram, Enumerable.Range(0, 73).Select(i => new DPoint(520 + 105 * Math.Cos(i * Math.PI / 36), 365 + 52 * Math.Sin(i * Math.PI / 36))).ToList(), "#2E9E5B", 2.5)!;
        loop.ParentId = frame.Id;
        b.Diagram.Nodes.Add(loop);

        b.Edge(MoodBoardModel.Arrow, sunset, orange, "", ("line", "#D98C1F"), ("width", "2"));
        b.Edge(MoodBoardModel.Arrow, sunset, purple, "", ("dash", "dashed"));
        b.Edge(MoodBoardModel.Line, keep, avoid, L.T("versus"), ("dash", "dotted"), ("startArrow", "circle"), ("endArrow", "circle"));
        b.Edge(MoodBoardModel.Arrow, sea, link, "", ("startArrow", "open"), ("line", "#3574F0"));
        return b.Diagram;
    }
}

/// <summary>A small PNG writer (pictures for samples; no imaging library in a .Core).</summary>
public static class Png
{
    /// <summary>A vertical gradient from <paramref name="top"/> to <paramref name="bottom"/>, maybe with a sun.</summary>
    public static byte[] Gradient(int width, int height, (byte R, byte G, byte B) top, (byte R, byte G, byte B) bottom, bool sun)
    {
        var raw = new byte[height * (width * 3 + 1)];
        for (var y = 0; y < height; y++)
        {
            var t = y / (double)(height - 1);
            var row = y * (width * 3 + 1);
            raw[row] = 0;    // no filter
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = (Mix(top.R, bottom.R, t), Mix(top.G, bottom.G, t), Mix(top.B, bottom.B, t));
                if (sun && Math.Pow(x - width * 0.7, 2) + Math.Pow(y - height * 0.45, 2) < Math.Pow(height * 0.16, 2))
                    (r, g, b) = (255, 214, 102);
                if (!sun && y > height * 0.62 && (x / 6 + y / 3) % 7 == 0)
                    (r, g, b) = (255, 255, 255);
                raw[row + 1 + x * 3] = r;
                raw[row + 2 + x * 3] = g;
                raw[row + 3 + x * 3] = b;
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;   // bit depth
        header[9] = 2;   // RGB
        Chunk(png, "IHDR", header);
        using (var data = new MemoryStream())
        {
            using (var z = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw);
            Chunk(png, "IDAT", data.ToArray());
        }
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    private static void WriteBigEndian(byte[] buffer, int at, int value)
    {
        buffer[at] = (byte)(value >> 24);
        buffer[at + 1] = (byte)(value >> 16);
        buffer[at + 2] = (byte)(value >> 8);
        buffer[at + 3] = (byte)value;
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        png.Write(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, (int)Crc(typed));
        png.Write(crc);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
