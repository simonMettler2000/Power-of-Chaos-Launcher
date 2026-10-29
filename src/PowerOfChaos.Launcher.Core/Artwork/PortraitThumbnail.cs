namespace PowerOfChaos.Launcher.Core.Artwork;

/// <summary>An in-memory, top-down thumbnail in premultiplied BGRA byte order.</summary>
public sealed record PortraitThumbnail(int Width, int Height, byte[] PremultipliedBgraPixels)
{
    private const int MaximumDimension = 4096;
    private const int MaximumPayloadBytes = 8_000_000;

    /// <summary>
    /// Fits the original canvas inside the requested pixel bounds without enlarging it.
    /// The unconstrained dimension is rounded to the nearest whole pixel, with a minimum of one.
    /// Source pixels are straight BGRA; area averaging happens after alpha weighting so hidden
    /// transparent colors cannot contribute to the thumbnail. The source is never modified.
    /// </summary>
    public static PortraitThumbnail Create(PortraitPixels source, int maxWidth, int maxHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Width > MaximumDimension
            || source.Height <= 0 || source.Height > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(source), "Portrait dimensions must be between 1 and 4096 pixels.");
        var sourceBytes = (long)source.Width * source.Height * 4;
        if (sourceBytes > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(source), "Portrait pixels exceed the 8,000,000-byte safety budget.");
        if (source.BgraPixels is null || source.BgraPixels.Length != sourceBytes)
            throw new ArgumentException("Portrait pixels must exactly cover its BGRA canvas.", nameof(source));
        if (maxWidth <= 0 || maxWidth > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(maxWidth), "Thumbnail bounds must be between 1 and 4096 pixels.");
        if (maxHeight <= 0 || maxHeight > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(maxHeight), "Thumbnail bounds must be between 1 and 4096 pixels.");
        cancellationToken.ThrowIfCancellationRequested();

        var width = source.Width;
        var height = source.Height;
        if (width > maxWidth || height > maxHeight)
        {
            if ((long)maxWidth * source.Height <= (long)maxHeight * source.Width)
            {
                width = maxWidth;
                height = Math.Max(1, (int)(((long)source.Height * width + source.Width / 2) / source.Width));
            }
            else
            {
                height = maxHeight;
                width = Math.Max(1, (int)(((long)source.Width * height + source.Height / 2) / source.Height));
            }
        }

        var pixels = new byte[width * height * 4];
        // One source pixel spans width integer units horizontally and height units vertically.
        // Every destination rectangle then has the same exact area, even at noninteger scales.
        var area = (long)source.Width * source.Height;
        var colorDivisor = area * 255;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var top = y * source.Height;
            var bottom = (y + 1) * source.Height;
            var firstSourceY = top / height;
            var lastSourceY = (bottom - 1) / height;
            for (var x = 0; x < width; x++)
            {
                var left = x * source.Width;
                var right = (x + 1) * source.Width;
                var firstSourceX = left / width;
                var lastSourceX = (right - 1) / width;
                long blue = 0, green = 0, red = 0, alpha = 0;
                for (var sourceY = firstSourceY; sourceY <= lastSourceY; sourceY++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var coverageY = Math.Min(bottom, (sourceY + 1) * height) - Math.Max(top, sourceY * height);
                    for (var sourceX = firstSourceX; sourceX <= lastSourceX; sourceX++)
                    {
                        var coverageX = Math.Min(right, (sourceX + 1) * width) - Math.Max(left, sourceX * width);
                        var sourceOffset = (sourceY * source.Width + sourceX) * 4;
                        var alphaWeight = (long)source.BgraPixels[sourceOffset + 3] * coverageX * coverageY;
                        alpha += alphaWeight;
                        blue += source.BgraPixels[sourceOffset] * alphaWeight;
                        green += source.BgraPixels[sourceOffset + 1] * alphaWeight;
                        red += source.BgraPixels[sourceOffset + 2] * alphaWeight;
                    }
                }

                var targetOffset = (y * width + x) * 4;
                pixels[targetOffset] = RoundChannel(blue, colorDivisor);
                pixels[targetOffset + 1] = RoundChannel(green, colorDivisor);
                pixels[targetOffset + 2] = RoundChannel(red, colorDivisor);
                pixels[targetOffset + 3] = RoundChannel(alpha, area);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new PortraitThumbnail(width, height, pixels);
    }

    private static byte RoundChannel(long sum, long divisor) => (byte)((sum + divisor / 2) / divisor);
}
