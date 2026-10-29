using PowerOfChaos.Launcher.Core.Artwork;

namespace PowerOfChaos.Launcher.Tests;

internal static class PortraitThumbnailChecks
{
    public static async Task RunAll(string fixtures, Func<string, Func<Task>, Task> run)
    {
        _ = fixtures; // All thumbnail fixtures are authored pixels held only in memory.
        await run("thumbnail native conversion preserves opaque near-green detail and owns its output", () =>
        {
            var source = Image(4, 2,
                (0, 255, 0, 255), (6, 255, 0, 255), (5, 255, 0, 255), (6, 254, 1, 255),
                (100, 50, 200, 128), (255, 255, 255, 1), (6, 255, 0, 0), (25, 70, 125, 255));
            var before = source.BgraPixels.ToArray();
            var result = PortraitThumbnail.Create(source, 40, 20);
            Size(result, 4, 2);
            Pixel(result, 0, 0, 0, 255, 0, 255);
            Pixel(result, 1, 0, 6, 255, 0, 255);
            Pixel(result, 2, 0, 5, 255, 0, 255);
            Pixel(result, 3, 0, 6, 254, 1, 255);
            Pixel(result, 0, 1, 50, 25, 100, 128);
            Pixel(result, 1, 1, 1, 1, 1, 1);
            Pixel(result, 2, 1, 0, 0, 0, 0);
            Pixel(result, 3, 1, 25, 70, 125, 255);
            Check(!ReferenceEquals(source.BgraPixels, result.PremultipliedBgraPixels), "Thumbnail aliases the source array.");
            Check(source.BgraPixels.SequenceEqual(before), "Thumbnail conversion changed source pixels.");
            result.PremultipliedBgraPixels[0] = 100;
            Check(source.BgraPixels.SequenceEqual(before), "Editing a thumbnail changed source pixels.");
            return Task.CompletedTask;
        });

        await run("thumbnail quarter-covered silhouette gains alpha without hidden matte color", () =>
        {
            var source = Image(2, 2,
                (0, 0, 255, 255), (0, 255, 0, 0),
                (6, 255, 0, 0), (255, 40, 10, 0));
            var before = source.BgraPixels.ToArray();
            var result = PortraitThumbnail.Create(source, 1, 1);
            Pixel(result, 0, 0, 0, 0, 64, 64);
            Check(source.BgraPixels.SequenceEqual(before), "Downsampling changed source pixels.");
            return Task.CompletedTask;
        });

        await run("thumbnail combines partial alpha before averaging color", () =>
        {
            var source = Image(2, 1, (200, 100, 50, 128), (30, 80, 160, 64));
            var result = PortraitThumbnail.Create(source, 1, 1);
            Pixel(result, 0, 0, 54, 35, 33, 96);
            return Task.CompletedTask;
        });

        await run("thumbnail noninteger two-dimensional coverage follows analytic areas", () =>
        {
            // Each output covers 2.25 source pixels. A corner contributes 4/9 of a cell;
            // the center pixel contributes 1/9 to each of the four cells.
            var source = Image(3, 3,
                (0, 0, 255, 255), (6, 255, 0, 0), (6, 255, 0, 0),
                (6, 255, 0, 0), (255, 0, 0, 255), (6, 255, 0, 0),
                (6, 255, 0, 0), (6, 255, 0, 0), (0, 255, 0, 255));
            var result = PortraitThumbnail.Create(source, 2, 2);
            Size(result, 2, 2);
            Pixel(result, 0, 0, 28, 0, 113, 142);
            Pixel(result, 1, 0, 28, 0, 0, 28);
            Pixel(result, 0, 1, 28, 0, 0, 28);
            Pixel(result, 1, 1, 28, 113, 0, 142);
            return Task.CompletedTask;
        });

        await run("thumbnail unequal source contributions preserve an opaque color ramp", () =>
        {
            var source = Image(3, 1, (0, 0, 30, 255), (0, 0, 90, 255), (0, 0, 240, 255));
            var result = PortraitThumbnail.Create(source, 2, 1);
            Size(result, 2, 1);
            Pixel(result, 0, 0, 0, 0, 50, 255);
            Pixel(result, 1, 0, 0, 0, 190, 255);
            return Task.CompletedTask;
        });

        await run("thumbnail constant partial-alpha color survives noninteger downsampling", () =>
        {
            var source = Solid(17, 13, 73, 151, 229, 117);
            var result = PortraitThumbnail.Create(source, 7, 5);
            Size(result, 7, 5);
            AllPixels(result, 33, 69, 105, 117);
            return Task.CompletedTask;
        });

        await run("thumbnail fine opaque line retains one-third coverage with clean edges", () =>
        {
            var source = Solid(9, 9, 6, 255, 0, 0);
            for (var y = 0; y < source.Height; y++)
                new byte[] { 102, 204, 255, 255 }.CopyTo(source.BgraPixels, (y * source.Width + 4) * 4);
            var result = PortraitThumbnail.Create(source, 3, 3);
            for (var y = 0; y < result.Height; y++)
            {
                Pixel(result, 0, y, 0, 0, 0, 0);
                Pixel(result, 1, y, 34, 68, 85, 85);
                Pixel(result, 2, y, 0, 0, 0, 0);
            }
            return Task.CompletedTask;
        });

        await run("thumbnail fully transparent canvases always discard hidden RGB", () =>
        {
            var source = Image(3, 2,
                (0, 255, 0, 0), (6, 255, 0, 0), (255, 255, 255, 0),
                (100, 10, 250, 0), (0, 0, 255, 0), (255, 0, 0, 0));
            AllPixels(PortraitThumbnail.Create(source, 1, 1), 0, 0, 0, 0);
            AllPixels(PortraitThumbnail.Create(source, 3, 2), 0, 0, 0, 0);
            return Task.CompletedTask;
        });

        await run("thumbnail aspect fit respects both bounds and never enlarges the native canvas", () =>
        {
            Size(PortraitThumbnail.Create(Solid(60, 40, 1, 2, 3, 255), 18, 18), 18, 12);
            Size(PortraitThumbnail.Create(Solid(40, 60, 1, 2, 3, 255), 18, 18), 12, 18);
            Size(PortraitThumbnail.Create(Solid(13, 7, 1, 2, 3, 255), 5, 5), 5, 3);
            Size(PortraitThumbnail.Create(Solid(7, 13, 1, 2, 3, 255), 5, 5), 3, 5);
            Size(PortraitThumbnail.Create(Solid(2, 3, 1, 2, 3, 255), 100, 100), 2, 3);
            Size(PortraitThumbnail.Create(Solid(4096, 1, 1, 2, 3, 255), 1, 4096), 1, 1);
            Size(PortraitThumbnail.Create(Solid(1, 4096, 1, 2, 3, 255), 4096, 1), 1, 1);
            return Task.CompletedTask;
        });

        await run("thumbnail rounding always produces valid premultiplied channels", () =>
        {
            var pixels = new byte[31 * 23 * 4];
            var random = new Random(72028);
            random.NextBytes(pixels);
            var source = new PortraitPixels(31, 23, pixels);
            foreach (var bounds in new[] { (31, 23), (17, 11), (5, 7), (1, 1) })
            {
                var result = PortraitThumbnail.Create(source, bounds.Item1, bounds.Item2);
                var output = result.PremultipliedBgraPixels;
                for (var offset = 0; offset < output.Length; offset += 4)
                    Check(output[offset] <= output[offset + 3] && output[offset + 1] <= output[offset + 3]
                        && output[offset + 2] <= output[offset + 3], "A premultiplied channel exceeded its alpha.");
            }
            return Task.CompletedTask;
        });

        await run("thumbnail accepts the exact source safety budget and dimension limit", () =>
        {
            var source = new PortraitPixels(1000, 2000, new byte[8_000_000]);
            AllPixels(PortraitThumbnail.Create(source, 1, 1), 0, 0, 0, 0);
            var wide = PortraitThumbnail.Create(Solid(4096, 1, 2, 3, 4, 255), 4096, 4096);
            Size(wide, 4096, 1);
            AllPixels(wide, 2, 3, 4, 255);
            return Task.CompletedTask;
        });

        await run("thumbnail rejects malformed source dimensions and buffers before allocating", () =>
        {
            Expect<ArgumentNullException>(() => PortraitThumbnail.Create(null!, 1, 1));
            foreach (var dimensions in new[] { (0, 1), (1, 0), (-1, 1), (1, -1), (4097, 1), (1, 4097), (int.MaxValue, int.MaxValue), (1000, 2001) })
                Expect<ArgumentOutOfRangeException>(() => PortraitThumbnail.Create(new PortraitPixels(dimensions.Item1, dimensions.Item2, []), 1, 1));
            Expect<ArgumentException>(() => PortraitThumbnail.Create(new PortraitPixels(1, 1, null!), 1, 1));
            Expect<ArgumentException>(() => PortraitThumbnail.Create(new PortraitPixels(1, 1, new byte[3]), 1, 1));
            Expect<ArgumentException>(() => PortraitThumbnail.Create(new PortraitPixels(1, 1, new byte[5]), 1, 1));
            return Task.CompletedTask;
        });

        await run("thumbnail rejects unsafe destination bounds and propagates cancellation", () =>
        {
            var source = Solid(2, 2, 2, 3, 4, 255);
            var before = source.BgraPixels.ToArray();
            foreach (var invalid in new[] { 0, -1, 4097, int.MaxValue })
            {
                Expect<ArgumentOutOfRangeException>(() => PortraitThumbnail.Create(source, invalid, 1));
                Expect<ArgumentOutOfRangeException>(() => PortraitThumbnail.Create(source, 1, invalid));
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Expect<OperationCanceledException>(() => PortraitThumbnail.Create(source, 1, 1, cancellation.Token));
            Check(source.BgraPixels.SequenceEqual(before), "Validation or cancellation changed source pixels.");
            return Task.CompletedTask;
        });
    }

    private static PortraitPixels Image(int width, int height, params (byte Blue, byte Green, byte Red, byte Alpha)[] pixels)
    {
        Check(pixels.Length == width * height, "Invalid authored pixel fixture.");
        return new PortraitPixels(width, height, pixels.SelectMany(pixel => new[] { pixel.Blue, pixel.Green, pixel.Red, pixel.Alpha }).ToArray());
    }

    private static PortraitPixels Solid(int width, int height, byte blue, byte green, byte red, byte alpha)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }
        return new PortraitPixels(width, height, pixels);
    }

    private static void Size(PortraitThumbnail thumbnail, int width, int height)
    {
        Check(thumbnail.Width == width && thumbnail.Height == height, $"Unexpected thumbnail dimensions {thumbnail.Width}x{thumbnail.Height}; expected {width}x{height}.");
        Check(thumbnail.PremultipliedBgraPixels.Length == width * height * 4, "Thumbnail buffer does not exactly cover its canvas.");
    }

    private static void AllPixels(PortraitThumbnail thumbnail, byte blue, byte green, byte red, byte alpha)
    {
        for (var y = 0; y < thumbnail.Height; y++)
            for (var x = 0; x < thumbnail.Width; x++) Pixel(thumbnail, x, y, blue, green, red, alpha);
    }

    private static void Pixel(PortraitThumbnail thumbnail, int x, int y, byte blue, byte green, byte red, byte alpha)
    {
        var actual = thumbnail.PremultipliedBgraPixels.AsSpan((y * thumbnail.Width + x) * 4, 4);
        Check(actual.SequenceEqual(new byte[] { blue, green, red, alpha }),
            $"Unexpected premultiplied BGRA at {x},{y}: [{string.Join(",", actual.ToArray())}], expected [{blue},{green},{red},{alpha}].");
    }

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
