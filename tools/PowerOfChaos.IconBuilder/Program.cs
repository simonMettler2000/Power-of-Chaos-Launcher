using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace PowerOfChaos.IconBuilder;

internal static class Program
{
    private static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    public static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected original SVG path and generated ICO path.");
        var source = Path.GetFullPath(args[0]);
        var destination = Path.GetFullPath(args[1]);
        if (new FileInfo(source).Length > 64 * 1024) throw new InvalidDataException("Icon SVG is too large.");
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024
        });
        var svg = XDocument.Load(reader).Root ?? throw new InvalidDataException("Missing SVG root.");
        if (svg.Name != XName.Get("svg", "http://www.w3.org/2000/svg") || (string?)svg.Attribute("viewBox") != "0 0 256 256")
            throw new InvalidDataException("Expected an authored 256-unit SVG icon.");
        var images = Sizes.Select(size => Render(svg, size)).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var writer = new BinaryWriter(File.Create(destination)))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)Sizes.Length);
            var offset = 6 + 16 * Sizes.Length;
            for (var i = 0; i < Sizes.Length; i++)
            {
                writer.Write(Sizes[i] == 256 ? (byte)0 : (byte)Sizes[i]);
                writer.Write(Sizes[i] == 256 ? (byte)0 : (byte)Sizes[i]);
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(images[i].Length); writer.Write(offset);
                offset += images[i].Length;
            }
            foreach (var image in images) writer.Write(image);
        }
        File.WriteAllBytes(Path.ChangeExtension(destination, ".png"), images[^1]);
        Console.WriteLine("Generated original launcher icon at 16–256 pixels.");
        return 0;
    }

    private static byte[] Render(XElement svg, int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size * 4, size * 4, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Icon drawing surface unavailable.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size * 4 / 256f);
        foreach (var element in svg.Elements())
        {
            using var path = Shape(element);
            var fill = (string?)element.Attribute("fill") ?? "black";
            if (fill != "none")
            {
                using var paint = new SKPaint { Color = SKColor.Parse(fill), IsAntialias = true, Style = SKPaintStyle.Fill };
                canvas.DrawPath(path, paint);
            }
            if ((string?)element.Attribute("stroke") is { } stroke && stroke != "none")
            {
                using var paint = new SKPaint
                {
                    Color = SKColor.Parse(stroke), IsAntialias = true, Style = SKPaintStyle.Stroke,
                    StrokeWidth = Number(element, "stroke-width", 1), StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round
                };
                canvas.DrawPath(path, paint);
            }
        }
        using var snapshot = surface.Snapshot();
        using var original = SKBitmap.FromImage(snapshot);
        using var resized = original.Resize(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High)
            ?? throw new InvalidOperationException("Icon resizing failed.");
        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static SKPath Shape(XElement element)
    {
        if (element.Name.NamespaceName != "http://www.w3.org/2000/svg")
            throw new InvalidDataException("Unexpected icon element namespace.");
        if (element.Name.LocalName == "path")
            return SKPath.ParseSvgPathData((string?)element.Attribute("d") ?? "")
                ?? throw new InvalidDataException("Invalid icon path.");
        var path = new SKPath();
        switch (element.Name.LocalName)
        {
            case "rect":
                var x = Number(element, "x"); var y = Number(element, "y");
                var width = Number(element, "width"); var height = Number(element, "height");
                var radius = Number(element, "rx");
                path.AddRoundRect(new SKRect(x, y, x + width, y + height), radius, radius);
                break;
            case "circle":
                path.AddCircle(Number(element, "cx"), Number(element, "cy"), Number(element, "r"));
                break;
            default:
                path.Dispose();
                throw new InvalidDataException("Only authored icon paths, rectangles and circles are supported.");
        }
        return path;
    }

    private static float Number(XElement element, string name, float fallback = 0)
    {
        var value = (string?)element.Attribute(name);
        var number = value is null ? fallback : float.Parse(value, CultureInfo.InvariantCulture);
        return float.IsFinite(number) ? number : throw new InvalidDataException("Invalid icon coordinate.");
    }
}
