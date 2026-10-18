using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DevHub.Diagrams;
using DevHub.Diagrams.Core;
using DevHub.Diagrams.Rendering;
using DevHub.MoodBoard.Core;
using DevHub.Sdk.Core.Localization;
using Icons = DevHub.Controls.IconKind;

namespace DevHub.MoodBoard;

/// <summary>Mood boards on the Diagrams canvas: pictures, swatches, shapes, frames, links, text and the pen.</summary>
public sealed class MoodBoardKind : DiagramKindBase
{
    private const double MaxPictureSide = 320;

    public override IDiagramKindModel Model { get; } = new MoodBoardModel();
    public override string IconKind => KindIcon("moodboard");
    public override int Order => 90;
    public override bool HasPen => true;

    public override bool MultilineText(DiagramNode n) => n.Type is MoodBoardModel.TextBox or MoodBoardModel.Rectangle or MoodBoardModel.Ellipse;

    protected override EdgeLook Look(DiagramEdge e) => e.Type == MoodBoardModel.Arrow ? new(End: LineEnd.Filled) : new();

    public override IReadOnlyList<PropertyField> DiagramFields => [new(L.T("Title"), "@title")];

    public override IReadOnlyList<PropertyField> NodeFields(DiagramNode n) => n.Type switch
    {
        MoodBoardModel.Image =>
        [
            new(L.T("Caption"), "@text"),
            new(L.T("Picture file"), "src", Hint: L.T("Stored beside the board; drop or paste a picture to replace")),
            new(L.T("Opacity"), "opacity", FieldKind.Number) { Default = "1", Minimum = 0.1, Maximum = 1, Step = 0.05 },
            new(L.T("Rounded corners"), "radius", FieldKind.Number) { Default = "0", Minimum = 0, Maximum = 48, Step = 1 },
            new(L.T("Border"), "line", FieldKind.Color),
        ],
        MoodBoardModel.Swatch => [new(L.T("Name"), "@text"), new(L.T("Colour"), "color", FieldKind.Color, L.T("#RRGGBB"))],
        MoodBoardModel.Link => [new(L.T("Title"), "@text"), new(L.T("Address"), "url"), .. StyleFields],
        MoodBoardModel.TextBox =>
        [
            new(L.T("Text"), "@text", FieldKind.MultilineText),
            new(L.T("Text size"), "fontSize", FieldKind.Number) { Default = "16", Minimum = 8, Maximum = 72, Step = 1 },
            new(L.T("Bold"), "bold", FieldKind.Toggle),
            new(L.T("Text colour"), "textColor", FieldKind.Color),
            new(L.T("Background"), "fill", FieldKind.Color),
        ],
        MoodBoardModel.Stroke => [new(L.T("Colour"), "line", FieldKind.Color), new(L.T("Width"), "width", FieldKind.Number) { Default = "3", Minimum = 1, Maximum = 24, Step = 0.5 }],
        MoodBoardModel.Frame => [new(L.T("Title"), "@text"), .. StyleFields],
        _ => [new(L.T("Label"), "@text", FieldKind.MultilineText), .. StyleFields],
    };

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        switch (n.Type)
        {
            case MoodBoardModel.Image:
            {
                var src = n.Prop("src");
                var path = Assets.Resolve(s.Folder, src);
                if (path is null)
                {
                    s.Rect(r, s.Style.ContainerFill, new DPen(s.Style.MutedText, 1, StrokeDash.Dashed));
                    s.Text(src.Length > 0 ? src : L.T("Drop or paste a picture"), r.Inflate(-8), new DText(11.5, Color: s.Style.MutedText));
                }
                else
                    s.Image(path, r, Number(n, "opacity", 1));
                if (TryColor(n.Prop("line")) is { } border)
                    s.Rect(r, null, new DPen(border, 2), Number(n, "radius", 0));
                if (n.Text.Length > 0)
                {
                    var style = new DText(11.5, Color: s.Style.MutedText);
                    var (_, h) = s.Measure(n.Text, style, r.Width);
                    s.Text(n.Text, new DRect(r.X, r.Bottom + 4, r.Width, h), style);
                }
                break;
            }
            case MoodBoardModel.Swatch:
            {
                var colour = TryColor(n.Prop("color")) ?? Colors.Gray;
                var label = Math.Min(42, r.Height * 0.4);
                var chip = new DRect(r.X, r.Y, r.Width, r.Height - label);
                s.Rect(r, s.Style.Fill, new DPen(s.Style.Grid, 1), 6);
                s.Rect(chip, colour, null, 6);
                s.Rect(new DRect(chip.X, chip.Bottom - 6, chip.Width, 6), colour, null);
                var name = n.Text.Length > 0 ? n.Text : n.Prop("color").ToUpperInvariant();
                s.Text(name, new DRect(r.X + 6, chip.Bottom + 3, r.Width - 12, 16), new DText(11.5, Bold: true, Align: TextAlign.Left), wrap: false);
                if (n.Text.Length > 0)
                    s.Text(n.Prop("color").ToUpperInvariant(), new DRect(r.X + 6, chip.Bottom + 19, r.Width - 12, 15),
                        new DText(10.5, Color: s.Style.MutedText, Align: TextAlign.Left, Mono: true), wrap: false);
                break;
            }
            case MoodBoardModel.Frame:
            {
                s.Rect(r, FillOf(s, n, s.Style.ContainerFill), PenOf(s, n) with { Thickness = 1 }, 8);
                s.Text(n.Text, new DRect(r.X + 4, r.Y - 22, r.Width - 8, 20), TextOf(n, new DText(13, Bold: true, Align: TextAlign.Left)), wrap: false);
                break;
            }
            case MoodBoardModel.TextBox:
            {
                if (TryColor(n.Prop("fill")) is { } back)
                    s.Rect(r, back, null, 4);
                var size = Number(n, "fontSize", 16);
                s.Text(n.Text, r.Inflate(-4), TextOf(n, new DText(size, Bold: n.Flag("bold"), Align: TextAlign.Left, Vertical: TextVertical.Top)));
                break;
            }
            case MoodBoardModel.Link:
            {
                s.Rect(r, FillOf(s, n), PenOf(s, n) with { Thickness = 1 }, 10);
                var icon = new DRect(r.X + 12, r.Center.Y - 11, 22, 22);
                s.Ellipse(icon, s.Style.Accent, null);
                s.Line(new DPoint(icon.X + 7, icon.Bottom - 7), new DPoint(icon.Right - 6, icon.Y + 6), new DPen(Colors.White, 1.8));
                s.Polyline([new(icon.Right - 11, icon.Y + 6), new(icon.Right - 6, icon.Y + 6), new(icon.Right - 6, icon.Y + 11)], new DPen(Colors.White, 1.8));
                var text = new DRect(icon.Right + 10, r.Y + 8, r.Width - icon.Width - 32, r.Height - 16);
                s.Text(n.Text, text with { Height = text.Height * 0.55 }, TextOf(n, new DText(12.5, Bold: true, Align: TextAlign.Left)), wrap: false);
                s.Text(Host(n.Prop("url")), text with { Y = text.Y + text.Height * 0.55, Height = text.Height * 0.45 },
                    new DText(10.5, Color: s.Style.Accent, Align: TextAlign.Left), wrap: false);
                break;
            }
            case MoodBoardModel.Stroke:
            {
                if (n.Points is not { Count: >= 2 } points)
                    break;
                var pen = new DPen(TryColor(n.Prop("line")) ?? s.Style.Line, Number(n, "width", 3));
                s.Curve(points.Select(p => new DPoint(p.X + n.X, p.Y + n.Y)).ToList(), pen);
                break;
            }
            case MoodBoardModel.Rectangle:
                s.Rect(r, FillOf(s, n), PenOf(s, n), Number(n, "radius", 6));
                s.Text(n.Text, r.Inflate(-8), TextOf(n, new DText(13)));
                break;
            default:
                base.DrawNode(s, d, n);
                break;
        }
    }

    private static double Number(DiagramNode n, string key, double fallback) =>
        double.TryParse(n.Prop(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host + uri.PathAndQuery.TrimEnd('/') : url;

    public override IDiagramOperation? Stroke(Diagram diagram, IReadOnlyList<DPoint> points, string color, double width) =>
        Strokes.Make(diagram, points, color, width) is { } node ? new AddElements([node], [], L.T("Draw")) : null;

    // ---- Pictures in ----

    private static string? BoardFile(IDiagramEditor editor)
    {
        if (editor.FilePath is { } path)
            return path;
        editor.Status.Show(L.T("Save the mood board first: its pictures are stored beside it."));
        return null;
    }

    /// <summary>A picture shape for stored bytes, at most <see cref="MaxPictureSide"/> on its long side.</summary>
    private static DiagramNode Picture(Diagram d, byte[] data, string src, DPoint at)
    {
        var (w, h) = (MaxPictureSide, MaxPictureSide * 0.75);
        try
        {
            using var stream = new MemoryStream(data);
            using var bitmap = new Bitmap(stream);
            var k = MaxPictureSide / Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            (w, h) = (Math.Max(24, bitmap.PixelSize.Width * Math.Min(1, k)), Math.Max(24, bitmap.PixelSize.Height * Math.Min(1, k)));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Unreadable here: a default size, drawn as a placeholder.
        }
        var node = new DiagramNode { Id = DiagramIds.NewIn(d), Type = MoodBoardModel.Image, X = Math.Round(at.X - w / 2), Y = Math.Round(at.Y - h / 2), Width = Math.Round(w), Height = Math.Round(h) };
        node.Props["src"] = src;
        return node;
    }

    public override async Task<bool> DropFilesAsync(IDiagramEditor editor, IReadOnlyList<string> paths, DPoint at)
    {
        var pictures = paths.Where(Assets.IsPicture).ToList();
        if (pictures.Count == 0 || BoardFile(editor) is not { } board)
            return pictures.Count > 0;
        var nodes = new List<DiagramNode>();
        var offset = 0.0;
        foreach (var file in pictures)
        {
            try
            {
                var data = await File.ReadAllBytesAsync(file);
                var src = await Assets.StoreAsync(board, data, Path.GetExtension(file));
                var node = Picture(editor.Diagram, data, src, at + new DPoint(offset, offset));
                editor.Diagram.Nodes.Add(node);         // reserve its id for the next ones
                nodes.Add(node);
                offset += 28;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                editor.Status.Show(L.T($"\"{Path.GetFileName(file)}\" couldn't be copied: {e.Message}"));
            }
        }
        foreach (var n in nodes)
            editor.Diagram.Nodes.Remove(n);
        if (nodes.Count > 0 && editor.Do(new AddElements(nodes, [], L.T("Add Pictures"))))
            editor.Select(nodes.Select(n => n.Id));
        return true;
    }

    public override async Task<bool> PasteAsync(IDiagramEditor editor, DPoint at)
    {
        if (await editor.Export.GetClipboardImageAsync() is { } png)
        {
            if (BoardFile(editor) is not { } board)
                return true;
            var src = await Assets.StoreAsync(board, png, ".png");
            var node = Picture(editor.Diagram, png, src, at);
            if (editor.Do(new AddElements([node], [], L.T("Paste Picture"))))
                editor.Select([node.Id]);
            return true;
        }
        if (await editor.Export.GetClipboardFilesAsync() is { Count: > 0 } files && files.Any(Assets.IsPicture))
            return await DropFilesAsync(editor, files, at);
        if (await editor.Export.GetClipboardTextAsync() is not { } text || text.Trim().Length == 0)
            return false;
        text = text.Trim();
        var model = Model;
        DiagramNode added;
        if (text.Length is 4 or 7 && text.StartsWith('#') && TryColor(text) is not null)
        {
            added = model.NewNode(editor.Diagram, MoodBoardModel.Swatch, at);
            added.Props["color"] = text.ToUpperInvariant();
        }
        else if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            added = model.NewNode(editor.Diagram, MoodBoardModel.Link, at, uri.Host);
            added.Props["url"] = uri.ToString();
        }
        else
            added = model.NewNode(editor.Diagram, MoodBoardModel.TextBox, at, text);
        if (editor.Do(new AddElements([added], [], L.T("Paste"))))
            editor.Select([added.Id]);
        return true;
    }

    // ---- Commands ----

    private static DiagramNode? Selected(IDiagramEditor editor, string type) =>
        editor.Selection.Select(editor.Diagram.Node).FirstOrDefault(n => n?.Type == type);

    public override IReadOnlyList<KindCommand> Commands =>
    [
        new(L.T("Add Picture…"), Icons.Add, async editor =>
        {
            var file = await editor.Dialogs.PickFileAsync(L.T("Add Picture"), L.T("Pictures"), Assets.PictureExtensions.Select(e => "*" + e).ToList());
            if (file is not null)
                await DropFilesAsync(editor, [file], editor.ViewCenter);
        })
        { Description = L.T("Copies a picture beside the board and places it in view.") },
        new(L.T("Palette from Picture"), Icons.Palette, editor =>
        {
            if (Selected(editor, MoodBoardModel.Image) is not { } image
                || Assets.Resolve(editor.FilePath is { } f ? Path.GetDirectoryName(f) : null, image.Prop("src")) is not { } path
                || ImageCache.Get(path) is not { } bitmap)
            {
                editor.Status.Show(L.T("Select a picture first."));
                return Task.CompletedTask;
            }
            var colours = Palette.Dominant(Pixels(bitmap), 5);
            var nodes = new List<DiagramNode>();
            var swatch = Model.NodeType(MoodBoardModel.Swatch)!;
            var width = Math.Min(swatch.Width, (image.Width - 8 * (colours.Count - 1)) / Math.Max(1, colours.Count));
            for (var i = 0; i < colours.Count; i++)
            {
                var node = Model.NewNode(editor.Diagram, MoodBoardModel.Swatch, default);
                editor.Diagram.Nodes.Add(node);
                (node.X, node.Y, node.Width, node.Height) = (image.X + i * (width + 8), image.Bounds.Bottom + (image.Text.Length > 0 ? 34 : 14), Math.Round(width), Math.Round(width * 1.25));
                node.Props["color"] = colours[i];
                nodes.Add(node);
            }
            foreach (var n in nodes)
                editor.Diagram.Nodes.Remove(n);
            if (nodes.Count > 0)
                editor.Do(new AddElements(nodes, [], L.T("Palette from Picture")));
            return Task.CompletedTask;
        })
        { Description = L.T("Adds swatches of the selected picture's main colours under it.") },
        new(L.T("Copy Colour"), Icons.Copy, async editor =>
        {
            if (Selected(editor, MoodBoardModel.Swatch) is not { } swatch)
            {
                editor.Status.Show(L.T("Select a colour swatch first."));
                return;
            }
            await editor.Export.CopyTextAsync(swatch.Prop("color").ToUpperInvariant());
            editor.Status.Show(L.T($"Copied {swatch.Prop("color").ToUpperInvariant()}."));
        })
        { Description = L.T("Copies the selected swatch's colour code.") },
        new(L.T("Open Link"), Icons.ExternalTools, editor =>
        {
            if (Selected(editor, MoodBoardModel.Link) is { } link && Uri.TryCreate(link.Prop("url"), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                try
                {
                    Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    editor.Status.Show(L.T($"The link couldn't be opened: {e.Message}"));
                }
            else
                editor.Status.Show(L.T("Select a link with a web address first."));
            return Task.CompletedTask;
        })
        { Description = L.T("Opens the selected link in the browser.") },
    ];

    /// <summary>The picture's pixels, scaled down to about 64 × 64 (transparent ones left out).</summary>
    private static List<(byte R, byte G, byte B)> Pixels(Bitmap bitmap)
    {
        var k = 64.0 / Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var size = new PixelSize(Math.Max(1, (int)(bitmap.PixelSize.Width * Math.Min(1, k))), Math.Max(1, (int)(bitmap.PixelSize.Height * Math.Min(1, k))));
        using var small = bitmap.CreateScaledBitmap(size, BitmapInterpolationMode.MediumQuality);
        var stride = size.Width * 4;
        var bytes = new byte[stride * size.Height];
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            small.CopyPixels(new PixelRect(size), buffer, bytes.Length, stride);
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        var rgba = small.Format == PixelFormat.Rgba8888;
        var pixels = new List<(byte, byte, byte)>(size.Width * size.Height);
        for (var i = 0; i + 3 < bytes.Length; i += 4)
        {
            if (bytes[i + 3] < 128)
                continue;
            pixels.Add(rgba ? (bytes[i], bytes[i + 1], bytes[i + 2]) : (bytes[i + 2], bytes[i + 1], bytes[i]));
        }
        return pixels;
    }
}
