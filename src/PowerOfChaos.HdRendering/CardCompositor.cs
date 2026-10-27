using PowerOfChaos.HdCards;

namespace PowerOfChaos.HdRendering;

/// <summary>
/// Authored RGB compositor. It samples original HD source pixels directly into the physical output.
/// It is not a game hook, a native surface adapter, or a complete scene renderer.
/// </summary>
public static class CardCompositor
{
    public static bool Draw(Span<byte> outputRgb, int outputWidth, int outputHeight,
        RgbCardImage image, LogicalRect destination, LogicalRect clip,
        CardFit fit = CardFit.Contain, CardRotation rotation = CardRotation.Upright)
    {
        ArgumentNullException.ThrowIfNull(image);
        var viewport = new HdViewport(outputWidth, outputHeight);
        if (outputRgb.Length != checked(outputWidth * outputHeight * 3))
            throw new ArgumentException("Output must contain exactly width * height * 3 RGB bytes.", nameof(outputRgb));
        if (!destination.IsValid || !clip.IsValid || !Enum.IsDefined(fit) || !Enum.IsDefined(rotation))
            return false;

        var swap = rotation is CardRotation.Clockwise or CardRotation.Counterclockwise;
        var sourceWidth = swap ? image.Height : image.Width;
        var sourceHeight = swap ? image.Width : image.Height;
        var relativeScale = fit == CardFit.Contain
            ? Math.Min(destination.Width / sourceWidth, destination.Height / sourceHeight)
            : Math.Max(destination.Width / sourceWidth, destination.Height / sourceHeight);
        var drawWidth = sourceWidth * relativeScale;
        var drawHeight = sourceHeight * relativeScale;
        var drawX = destination.X + (destination.Width - drawWidth) / 2;
        var drawY = destination.Y + (destination.Height - drawHeight) / 2;
        if (!double.IsFinite(relativeScale) || relativeScale <= 0 || !double.IsFinite(drawX)
            || !double.IsFinite(drawY) || !double.IsFinite(drawWidth) || !double.IsFinite(drawHeight))
            return false;

        // Half-open logical rectangles are intersected before conversion to bounded physical indices.
        var left = Math.Max(0, Math.Max(clip.X, Math.Max(destination.X, drawX)));
        var top = Math.Max(0, Math.Max(clip.Y, Math.Max(destination.Y, drawY)));
        var right = Math.Min(800, Math.Min(clip.X + clip.Width, Math.Min(destination.X + destination.Width, drawX + drawWidth)));
        var bottom = Math.Min(600, Math.Min(clip.Y + clip.Height, Math.Min(destination.Y + destination.Height, drawY + drawHeight)));
        if (left >= right || top >= bottom) return false;
        var x0 = FirstPixel(viewport.Left + left * viewport.Scale, outputWidth);
        var y0 = FirstPixel(viewport.Top + top * viewport.Scale, outputHeight);
        var x1 = FirstPixel(viewport.Left + right * viewport.Scale, outputWidth);
        var y1 = FirstPixel(viewport.Top + bottom * viewport.Scale, outputHeight);
        var pixels = image.Pixels;
        for (var py = y0; py < y1; py++)
        {
            var y = (py + .5 - viewport.Top) / viewport.Scale;
            var v = (y - drawY) / drawHeight;
            for (var px = x0; px < x1; px++)
            {
                var x = (px + .5 - viewport.Left) / viewport.Scale;
                var u = (x - drawX) / drawWidth;
                var (su, sv) = rotation switch
                {
                    CardRotation.Clockwise => (v, 1 - u),
                    CardRotation.UpsideDown => (1 - u, 1 - v),
                    CardRotation.Counterclockwise => (1 - v, u),
                    _ => (u, v)
                };
                Sample(pixels, image.Width, image.Height, su, sv, outputRgb, (py * outputWidth + px) * 3);
            }
        }
        return x0 < x1 && y0 < y1;
    }

    private static int FirstPixel(double edge, int limit) => (int)Math.Clamp(Math.Ceiling(edge - .5), 0, limit);

    private static void Sample(ReadOnlySpan<byte> rgb, int width, int height, double u, double v,
        Span<byte> target, int offset)
    {
        var sx = Math.Clamp(u * width - .5, 0, width - 1d);
        var sy = Math.Clamp(v * height - .5, 0, height - 1d);
        var x0 = (int)sx;
        var y0 = (int)sy;
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var fx = sx - x0;
        var fy = sy - y0;
        for (var c = 0; c < 3; c++)
        {
            var a = rgb[(y0 * width + x0) * 3 + c];
            var b = rgb[(y0 * width + x1) * 3 + c];
            var d = rgb[(y1 * width + x0) * 3 + c];
            var e = rgb[(y1 * width + x1) * 3 + c];
            var value = (a + (b - a) * fx) * (1 - fy) + (d + (e - d) * fx) * fy;
            target[offset + c] = (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
        }
    }
}
